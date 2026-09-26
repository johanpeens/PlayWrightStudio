using System.Text.Json;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// Drives a headed browser, injects the recorder script into every document and turns
/// what comes back into <see cref="TestStep"/>s. One recording at a time.
/// </summary>
public sealed class RecorderService : IAsyncDisposable
{
    private static readonly TimeSpan NavigationGrace = TimeSpan.FromMilliseconds(1500);

    private readonly SettingsService _settings;
    private readonly ScenarioStore _scenarios;
    private readonly ILogger<RecorderService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stepLock = new();


    /// <summary>
    /// Set when recording a page the user already had open, instead of one launched here.
    /// The two modes share everything downstream - OnRecorderMessage does not care where the
    /// json came from.
    /// </summary>
    private PageSession? _attached;

    /// <summary>True when recording a page the user opened, rather than a launched browser.</summary>
    public bool IsAttached => _attached is not null;

    public string? AttachedUrl => _attached?.Url;
    private DateTime _lastInteractionUtc = DateTime.MinValue;
    private string? _lastNavUrl;
    private (string sel, string action, DateTime at) _lastRaw;
    private int _committing;

    public RecorderService(SettingsService settings, ScenarioStore scenarios,
                           PageSessions pages, ILogger<RecorderService> log)
    {
        _settings = settings;
        _scenarios = scenarios;
        _pages = pages;
        _log = log;
    }

    private readonly PageSessions _pages;

    /// <summary>True while steps are being replayed into the live window.</summary>
    public bool IsPlaying { get; private set; }

    public RecorderState State { get; private set; } = RecorderState.Stopped;
    public string? StatusMessage { get; private set; }
    public string? CurrentUrl { get; private set; }
    public string? PickAction { get; private set; }

    /// <summary>The scenario captured steps are written into when recording ends.</summary>
    public string? TargetScenarioId { get; private set; }

    /// <summary>Steps captured in this session, oldest first.</summary>
    public List<TestStep> Steps { get; } = new();

    /// <summary>Variables the recorder created for password fields.</summary>
    public List<ScenarioVariable> CapturedVariables { get; } = new();

    /// <summary>Raised whenever anything above changes; components re-render off this.</summary>
    public event Action? Changed;

    /// <summary>Raised after captured steps have been written to the scenario, with how many.</summary>
    public event Action<int>? Committed;

    public bool IsLive => State is RecorderState.Recording or RecorderState.Paused;

    private void Raise() => Changed?.Invoke();

    // ------------------------------------------------------------------ start / stop


    /// <summary>
    /// Records a page the user already has open. Nothing is launched: the page called home when
    /// it loaded the studio's script, and this takes ownership of it.
    /// </summary>
    public async Task AttachAsync(PageSession page, string scenarioId, IEnumerable<TestStep>? seed = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsLive) return;

            TargetScenarioId = scenarioId;
            lock (_stepLock)
            {
                Steps.Clear();
                if (seed is not null) Steps.AddRange(seed.Select(s => s.Clone()));
            }
            CapturedVariables.Clear();
            _lastNavUrl = null;
            _lastInteractionUtc = DateTime.MinValue;

            _attached = page;
            page.Message += OnRecorderMessage;

            State = RecorderState.Recording;
            StatusMessage = $"Recording {page.Url}";
            await BroadcastModeAsync("record");
            Raise();
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var saved = Commit();
            await TearDownAsync();
            State = RecorderState.Stopped;
            PickAction = null;
            StatusMessage = Describe(saved, "Recorder stopped.");
            Raise();
            if (saved > 0) Committed?.Invoke(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes whatever has been captured into the target scenario. Runs when recording ends by
    /// either route - Stop in the studio, or the browser window being closed - so a recording is
    /// never left sitting in memory waiting to be claimed.
    /// </summary>
    private int Commit()
    {
        if (Interlocked.CompareExchange(ref _committing, 1, 0) != 0) return 0;
        try
        {
            List<TestStep> pending;
            lock (_stepLock) pending = Steps.ToList();
            if (pending.Count == 0) return 0;

            if (TargetScenarioId is null)
            {
                _log.LogWarning("Recorded {Count} steps with no target scenario; keeping them in memory", pending.Count);
                return 0;
            }

            var scenario = _scenarios.Get(TargetScenarioId);
            if (scenario is null)
            {
                _log.LogWarning("Scenario {Id} is gone; keeping {Count} recorded steps in memory",
                    TargetScenarioId, pending.Count);
                return 0;
            }

            scenario.Steps.AddRange(pending.Select(s => s.Clone()));

            foreach (var captured in CapturedVariables)
            {
                var existing = scenario.Variables
                    .FirstOrDefault(v => string.Equals(v.Name, captured.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    scenario.Variables.Add(new ScenarioVariable
                    {
                        Name = captured.Name,
                        Value = captured.Value,
                        IsSecret = captured.IsSecret,
                        Note = captured.Note
                    });
                }
                else
                {
                    existing.Value = captured.Value;
                    existing.IsSecret |= captured.IsSecret;
                }
            }

            if (string.IsNullOrWhiteSpace(scenario.StartUrl))
            {
                scenario.StartUrl = pending.FirstOrDefault(s => s.Action == StepAction.Navigate)?.Value
                                    ?? CurrentUrl ?? "";
            }

            _scenarios.Save(scenario);

            lock (_stepLock) Steps.Clear();
            CapturedVariables.Clear();

            _log.LogInformation("Saved {Count} recorded steps to scenario {Id}", pending.Count, scenario.Id);
            return pending.Count;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not save the recording; the steps are still in memory");
            return 0;
        }
        finally
        {
            Interlocked.Exchange(ref _committing, 0);
        }
    }

    private string Describe(int saved, string prefix)
    {
        if (saved > 0) return $"{prefix} {saved} step{(saved == 1 ? "" : "s")} saved - ready to replay.";
        lock (_stepLock)
        {
            if (Steps.Count > 0)
                return $"{prefix} {Steps.Count} captured step{(Steps.Count == 1 ? "" : "s")} could not be saved - see the log.";
        }
        return $"{prefix} Nothing was captured.";
    }

    public async Task SetPausedAsync(bool paused)
    {
        if (!IsLive) return;
        await BroadcastModeAsync(paused ? "paused" : "record");
        State = paused ? RecorderState.Paused : RecorderState.Recording;
        StatusMessage = paused ? "Paused - clicks are being ignored." : "Recording.";
        Raise();
    }

    /// <summary>Puts the in-page overlay into element-pick mode for an assertion.</summary>
    public Task PickAsync(string action) => BroadcastModeAsync("pick:" + action);

    /// <summary>Tells the page which mode its toolbar should be in: record, paused, or pick.</summary>
    private async Task BroadcastModeAsync(string mode)
    {
        if (_attached is null) return;
        await _attached.SendAsync(System.Text.Json.JsonSerializer.Serialize(
            new { type = "mode", value = mode }));
    }

    /// <summary>
    /// Asks the page how many elements a selector picks out, and flashes an outline over the
    /// first one. The page answers, because the page is the only thing that can see itself.
    /// </summary>
    public async Task<string> TrySelectorAsync(string selector, IReadOnlyList<string>? frameChain = null)
    {
        if (_attached is null)
            return "Record in one of your open pages first - selectors are tested against a real page.";

        var answer = await _attached.RequestAsync("highlight",
            new { selector, frames = frameChain ?? Array.Empty<string>() }, TimeSpan.FromSeconds(10));

        if (answer is null) return "The page did not answer.";

        if (!answer.Value.TryGetProperty("result", out var result))
            return "The page did not answer.";

        if (result.TryGetProperty("error", out var err) && err.ValueKind == System.Text.Json.JsonValueKind.String)
            return err.GetString() ?? "That selector could not be resolved.";

        var count = result.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
        var shown = result.TryGetProperty("visible", out var v) ? v.GetInt32() : 0;

        return count switch
        {
            0 => "Nothing matched.",
            1 when shown == 1 => "Matched exactly one element, highlighted in your page.",
            1 => "Matched one element, but it is not visible.",
            _ => $"{count} elements matched, {shown} of them visible. The first visible one is highlighted."
        };
    }




    private async Task TearDownAsync()
    {
        if (_attached is not null)
        {
            // The user's own tab - let go of it, never close it.
            _attached.Message -= OnRecorderMessage;
            _pages?.Release(_attached.Id);
            _attached = null;
        }

    }

    // ------------------------------------------------------------------ incoming events

    private void OnNavigated(string url)
    {
        // about:srcdoc, about:blank, blob: and friends are not places a test can navigate to.
        if (!Navigable(url)) return;
        if (string.Equals(url, _lastNavUrl, StringComparison.OrdinalIgnoreCase)) return;

        _lastNavUrl = url;
        CurrentUrl = url;

        if (State != RecorderState.Recording) { Raise(); return; }

        // A navigation right after a click is a consequence of that click, not a step.
        var sinceInteraction = DateTime.UtcNow - _lastInteractionUtc;
        var isFirst = Steps.Count == 0;

        // A sign-in redirect carries a one-time nonce and code challenge. Recording it produces
        // a step that can never be replayed - the click that caused it is the real step.
        if (!isFirst && IsOneTimeAuthUrl(url)) { Raise(); return; }

        if (isFirst || sinceInteraction > NavigationGrace)
        {
            Append(new TestStep
            {
                Action = StepAction.Navigate,
                Value = url,
                Description = "Go to " + url
            });
        }
        Raise();
    }

    private void OnRecorderMessage(string json)
    {
        RecordedEvent? ev;
        try { ev = JsonSerializer.Deserialize<RecordedEvent>(json); }
        catch (Exception ex) { _log.LogDebug(ex, "Bad recorder payload: {Json}", json); return; }
        if (ev is null) return;

        switch (ev.Type)
        {
            case "mode":
                ApplyMode(ev.Value);
                Raise();
                return;

            case "nav":
                OnNavigated(ev.Value ?? "");
                return;

            case "step":
                if (State != RecorderState.Recording) return;
                AddFromEvent(ev);
                Raise();
                return;
        }
    }

    private void ApplyMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return;
        if (mode.StartsWith("pick:", StringComparison.Ordinal))
        {
            PickAction = mode[5..];
            StatusMessage = $"Pick mode - click an element in the browser to add \"{PickAction}\".";
            return;
        }
        PickAction = null;
        if (mode == "paused") { State = RecorderState.Paused; StatusMessage = "Paused - clicks are being ignored."; }
        else { State = RecorderState.Recording; StatusMessage = "Recording."; }
    }

    private void AddFromEvent(RecordedEvent ev)
    {
        if (!Enum.TryParse<StepAction>(ev.Action, ignoreCase: true, out var action)) return;

        if (!string.IsNullOrWhiteSpace(ev.Url)) CurrentUrl = ev.Url;
        _lastInteractionUtc = DateTime.UtcNow;

        var step = new TestStep
        {
            Action = action,
            Selector = ev.Selector,
            SelectorAlternatives = ev.Alternatives ?? new List<string>(),
            Value = ev.Value,
            Description = ev.Description,
            FrameChain = ev.Frames ?? new List<string>()
        };

        if (action == StepAction.Comment)
        {
            step.Selector = null;
            step.SelectorAlternatives.Clear();
        }

        // Passwords become a variable so the step itself stays readable.
        if (ev.Secret && !string.IsNullOrEmpty(ev.Value))
        {
            var name = VariableName(ev.Field);
            var existing = CapturedVariables.FirstOrDefault(v => v.Name == name);
            if (existing is null)
                CapturedVariables.Add(new ScenarioVariable { Name = name, Value = ev.Value!, IsSecret = true, Note = "Captured from a password field" });
            else
                existing.Value = ev.Value!;
            step.Value = "{{" + name + "}}";
        }

        lock (_stepLock)
        {
            var last = Steps.Count > 0 ? Steps[^1] : null;

            // Typing fires repeatedly - keep one Fill per field.
            if (last is not null && action == StepAction.Fill && last.Action == StepAction.Fill &&
                last.Selector == step.Selector && SameFrame(last, step))
            {
                last.Value = step.Value;
                last.Description = step.Description;
                return;
            }

            // Some frameworks fire a click twice; swallow the echo.
            var now = DateTime.UtcNow;
            if (_lastRaw.sel == step.Selector && _lastRaw.action == ev.Action &&
                action is StepAction.Click or StepAction.Check or StepAction.Uncheck &&
                (now - _lastRaw.at).TotalMilliseconds < 350)
            {
                return;
            }
            _lastRaw = (step.Selector ?? "", ev.Action ?? "", now);

            Steps.Add(step);
        }
    }

    private void Append(TestStep step)
    {
        lock (_stepLock) Steps.Add(step);
    }

    private static bool SameFrame(TestStep a, TestStep b) =>
        a.FrameChain.Count == b.FrameChain.Count && !a.FrameChain.Where((t, i) => t != b.FrameChain[i]).Any();

    private string VariableName(string? field)
    {
        var raw = string.IsNullOrWhiteSpace(field) ? "password" : field!;
        var cleaned = new string(raw.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "password";
        if (char.IsDigit(cleaned[0])) cleaned = "v" + cleaned;
        return cleaned.ToLowerInvariant();
    }

    /// <summary>
    /// Single-use sign-in urls: nonces, PKCE challenges, authorization codes. Valid once, so a
    /// step pointing at one is dead the moment it is written.
    /// </summary>
    private static bool IsOneTimeAuthUrl(string url) =>
        url.Contains("code_challenge", StringComparison.OrdinalIgnoreCase)
        || url.Contains("nonce=", StringComparison.OrdinalIgnoreCase)
        || url.Contains("id_token", StringComparison.OrdinalIgnoreCase)
        || url.Contains("access_token", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/saml2/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only http, https and file urls are somewhere a replay can actually go.</summary>
    private static bool Navigable(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    private static string Normalise(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return url;
        return "https://" + url;
    }


    // ------------------------------------------------------------------ editing helpers

    public void RemoveStep(string id)
    {
        lock (_stepLock) Steps.RemoveAll(s => s.Id == id);
        Raise();
    }

    public void ClearSteps()
    {
        lock (_stepLock) Steps.Clear();
        CapturedVariables.Clear();
        Raise();
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync();
        _gate.Dispose();
    }
}
