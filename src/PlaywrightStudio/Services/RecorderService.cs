using System.Text.Json;
using Microsoft.Playwright;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// Drives a headed browser, injects the recorder script into every document and turns
/// what comes back into <see cref="TestStep"/>s. One recording at a time.
/// </summary>
public sealed class RecorderService : IAsyncDisposable
{
    private static readonly TimeSpan NavigationGrace = TimeSpan.FromMilliseconds(1500);

    private readonly PlaywrightHost _host;
    private readonly SettingsService _settings;
    private readonly ScenarioStore _scenarios;
    private readonly RunnerService _runner;
    private readonly ILogger<RecorderService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stepLock = new();

    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private string? _recorderJs;
    private DateTime _lastInteractionUtc = DateTime.MinValue;
    private string? _lastNavUrl;
    private (string sel, string action, DateTime at) _lastRaw;
    private int _committing;
    private bool _stopping;

    public RecorderService(PlaywrightHost host, SettingsService settings, ScenarioStore scenarios,
                           RunnerService runner, ILogger<RecorderService> log)
    {
        _host = host;
        _settings = settings;
        _scenarios = scenarios;
        _runner = runner;
        _log = log;
    }

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

    public async Task StartAsync(string startUrl, string scenarioId, IEnumerable<TestStep>? seed = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsLive) return;

            State = RecorderState.Starting;
            StatusMessage = "Launching browser…";
            PickAction = null;
            TargetScenarioId = scenarioId;
            _stopping = false;
            Raise();

            lock (_stepLock)
            {
                Steps.Clear();
                if (seed is not null) Steps.AddRange(seed.Select(s => s.Clone()));
            }
            CapturedVariables.Clear();
            _lastNavUrl = null;
            _lastInteractionUtc = DateTime.MinValue;

            _recorderJs ??= await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Assets", "recorder.js"));

            var settings = _settings.Current;
            // Recording is always headed and never slowed down - you are the one driving.
            _browser = await _host.LaunchAsync(settings, headless: false, slowMoMs: 0);
            _context = await _host.NewContextAsync(_browser, settings);

            await _context.ExposeBindingAsync("__pwsEmit", (BindingSource _, string json) => OnRecorderMessage(json));
            await _context.AddInitScriptAsync(_recorderJs);

            // The session window doubles as the window runs happen in, so give it the run bar
            // too. It stays invisible until a run calls begin().
            try
            {
                var runBarJs = await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "runbar.js"));
                await _context.ExposeBindingAsync("__pwsRunStop", (BindingSource _, string _) => _runner.Abort());
                await _context.AddInitScriptAsync(runBarJs);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Run bar unavailable in the recording session");
            }

            _context.Page += (_, page) => WirePage(page);
            _browser.Disconnected += (_, _) => OnBrowserGone();

            _page = await _context.NewPageAsync();
            WirePage(_page);

            State = RecorderState.Recording;
            StatusMessage = "Recording. Click through the form in the browser window.";
            Raise();

            var url = Normalise(startUrl);
            if (!string.IsNullOrWhiteSpace(url))
            {
                CurrentUrl = url;
                try
                {
                    await _page.GotoAsync(url);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Recording, but {url} did not load: {ex.Message}";
                    Raise();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recorder failed to start");
            State = RecorderState.Stopped;
            StatusMessage = "Could not start the recorder: " + ex.Message;
            await TearDownAsync();
            Raise();
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _stopping = true;
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
            _stopping = false;
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

    private async Task BroadcastModeAsync(string mode)
    {
        if (_context is null) return;
        foreach (var page in _context.Pages)
        {
            try { await page.EvaluateAsync("m => window.__pwsSetMode && window.__pwsSetMode(m)", mode); }
            catch (Exception ex) { _log.LogDebug(ex, "Could not set recorder mode on a page"); }
        }
    }

    /// <summary>Flash the element a selector points at, in the live recording browser.</summary>
    public async Task<string> TrySelectorAsync(string selector, IReadOnlyList<string>? frameChain = null)
    {
        if (_page is null || !IsLive) return "Start the recorder first - selectors are tested in the live browser.";
        try
        {
            var locator = Resolve(_page, selector, frameChain);
            var count = await locator.CountAsync();
            if (count > 0) await locator.First.HighlightAsync();
            return count switch
            {
                0 => "No match on the current page.",
                1 => "1 match - highlighted in the browser.",
                _ => $"{count} matches - the first one is highlighted. Consider a tighter selector."
            };
        }
        catch (Exception ex)
        {
            return "Selector error: " + ex.Message;
        }
    }

    public static ILocator Resolve(IPage page, string selector, IReadOnlyList<string>? frameChain)
    {
        if (frameChain is null || frameChain.Count == 0) return page.Locator(selector);

        // Take the first matching frame at each level. A recorded chain can still be ambiguous
        // on a page carrying several hidden iframes, and acting on one beats refusing to run.
        var frame = page.Locator(frameChain[0]).First.ContentFrame;
        for (var i = 1; i < frameChain.Count; i++)
            frame = frame.Locator(frameChain[i]).First.ContentFrame;
        return frame.Locator(selector);
    }

    private void WirePage(IPage page)
    {
        page.FrameNavigated += (_, frame) =>
        {
            if (frame.ParentFrame is not null) return;
            OnNavigated(frame.Url);
        };
        page.Close += (_, _) =>
        {
            // The closing page is often still listed here, so ask each one whether it is gone
            // rather than waiting for the collection to empty.
            if (_context is not null && _context.Pages.All(p => p.IsClosed)) OnBrowserGone();
        };
    }

    private void OnBrowserGone()
    {
        // StopAsync does its own commit; don't race it from the disconnect event.
        if (_stopping || State == RecorderState.Stopped) return;

        var saved = Commit();
        State = RecorderState.Stopped;
        PickAction = null;
        StatusMessage = Describe(saved, "Browser closed.");
        _page = null;
        _context = null;
        _browser = null;
        Raise();
        if (saved > 0) Committed?.Invoke(saved);
    }

    private async Task TearDownAsync()
    {
        try { if (_context is not null) await _context.CloseAsync(); } catch { /* already gone */ }
        try { if (_browser is not null) await _browser.CloseAsync(); } catch { /* already gone */ }
        _page = null;
        _context = null;
        _browser = null;
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

    /// <summary>
    /// Runs a scenario properly - results, timings, history - but in the window that is already
    /// open, rather than launching a second one. Capture is paused so the run is not recorded.
    /// </summary>
    public async Task<RunResult?> RunHereAsync(Scenario scenario, int startIndex = 0)
    {
        if (_page is null || !IsLive) return null;
        if (IsPlaying) return null;

        var resumeAfter = State == RecorderState.Recording;
        IsPlaying = true;
        try
        {
            await BroadcastModeAsync("paused");
            State = RecorderState.Paused;
            StatusMessage = "Running in this window…";
            Raise();

            return await _runner.RunInSessionAsync(_page, scenario, startIndex,
                progress: message => { StatusMessage = message; Raise(); });
        }
        finally
        {
            IsPlaying = false;
            if (resumeAfter && IsLive)
            {
                await BroadcastModeAsync("record");
                State = RecorderState.Recording;
                StatusMessage = "Recording. The window is yours again.";
            }
            Raise();
        }
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
