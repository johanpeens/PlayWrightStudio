using System.Text.Json;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// Replays a scenario in a page the user already has open.
///
/// The old runner executed every step itself through Playwright. This one only orchestrates:
/// it substitutes variables, sends one step down the socket, waits for the answer, and writes
/// the result down. All the doing happens in player.js, in the browser.
/// </summary>
public sealed class PageRunner
{
    private readonly PageSessions _pages;
    private readonly SettingsService _settings;
    private readonly StudioPaths _paths;
    private readonly RunStore _runs;
    private readonly ILogger<PageRunner> _log;

    private CancellationTokenSource? _cts;

    public PageRunner(PageSessions pages, SettingsService settings, StudioPaths paths,
                      RunStore runs, ILogger<PageRunner> log)
    {
        _pages = pages;
        _settings = settings;
        _paths = paths;
        _runs = runs;
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public RunResult? Current { get; private set; }

    public event Action? Changed;

    public void Abort() => _cts?.Cancel();

    public async Task<RunResult> RunAsync(PageSession page, Scenario scenario, int startIndex = 0)
    {
        var settings = _settings.Current;

        var run = new RunResult
        {
            ScenarioId = scenario.Id,
            ScenarioName = scenario.Name,
            StartUrl = page.Url,
            Browser = "your browser",
            Headless = false,
            Status = RunStatus.Running
        };

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsRunning = true;
        Current = run;
        _runs.MarkActive(run.Id);
        Raise();

        var folder = _paths.RunFolder(run.Id);
        var steps = scenario.Steps.Where(s => s.Enabled).ToList();

        try
        {
            await BarAsync(page, "begin", scenario.Name);

            // rrweb replaces the old mp4 and trace.zip: smaller, scrubbable, and you can stop
            // on any frame and read the DOM as it was.
            if (settings.RecordVideo)
                await page.RequestAsync("tape", new { action = "start" }, TimeSpan.FromSeconds(20), ct);

            for (var i = 0; i < steps.Count; i++)
            {
                if (i < startIndex)
                {
                    run.Steps.Add(Skipped(steps[i], i));
                    continue;
                }

                if (ct.IsCancellationRequested)
                {
                    run.Steps.Add(Skipped(steps[i], i));
                    continue;
                }

                var step = steps[i];
                var result = await RunStepAsync(page, scenario, step, i, folder, settings, ct);
                run.Steps.Add(result);
                Raise();

                if (result.Status == StepStatus.Failed && !step.ContinueOnFailure)
                {
                    // Everything after a hard failure is skipped, same as it has always been.
                    for (var rest = i + 1; rest < steps.Count; rest++)
                        run.Steps.Add(Skipped(steps[rest], rest));
                    break;
                }
            }

            run.Status = ct.IsCancellationRequested ? RunStatus.Aborted
                       : run.Steps.Any(s => s.Status == StepStatus.Failed) ? RunStatus.Failed
                       : RunStatus.Passed;

            if (ct.IsCancellationRequested) run.Error ??= "Run stopped by you.";
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
            _log.LogError(ex, "Run {Id} fell over", run.Id);
        }
        finally
        {
            if (settings.RecordVideo)
                run.VideoFile = await SaveTapeAsync(page, folder);

            run.FinishedUtc = DateTimeOffset.UtcNow;
            _runs.Save(run);
            _runs.MarkFinished(run.Id);

            await BarAsync(page, "finish", Summary(run));

            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            Raise();
        }

        return run;
    }

    // ------------------------------------------------------------------ one step

    private async Task<StepResult> RunStepAsync(PageSession page, Scenario scenario, TestStep step,
                                                int index, string folder, StudioSettings settings,
                                                CancellationToken ct)
    {
        var began = DateTimeOffset.UtcNow;

        var result = new StepResult
        {
            StepId = step.Id,
            Index = index,
            Action = step.Action,
            Selector = step.Selector,
            Status = StepStatus.Running
        };

        await BarAsync(page, "update", $"{index + 1}. {step.Summarise()}");

        if (step.DelayBeforeMs > 0)
            await Task.Delay(step.DelayBeforeMs, ct);

        var timeout = step.TimeoutMs is > 0 ? step.TimeoutMs.Value : settings.DefaultTimeoutMs;

        // Variables land in selectors as well as values, so both go through the resolver.
        var payload = new
        {
            step = new
            {
                action = step.Action.ToString(),
                selector = ValueResolver.Resolve(step.Selector, scenario.Variables),
                value = ValueResolver.Resolve(step.Value, scenario.Variables),
                frames = step.FrameChain,
                timeoutMs = timeout
            }
        };

        // Give the page a little longer than the step's own budget, so a step that is genuinely
        // timing out reports its own good message rather than this one.
        var answer = await page.RequestAsync("step", payload, TimeSpan.FromMilliseconds(timeout + 5000), ct);

        result.DurationMs = (int)(DateTimeOffset.UtcNow - began).TotalMilliseconds;

        if (answer is null)
        {
            result.Status = StepStatus.Failed;
            result.Error = ct.IsCancellationRequested
                ? "Run stopped by you."
                : "The page stopped answering. It may have navigated, or the tab was closed.";
            return result;
        }

        var body = answer.Value;
        var ok = body.TryGetProperty("result", out var r) &&
                 r.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();

        result.Status = ok ? StepStatus.Passed : StepStatus.Failed;

        if (body.TryGetProperty("result", out var detail))
        {
            if (detail.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                result.Error = err.GetString();
            if (detail.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
                result.Note = note.GetString();
        }

        var wantShot = step.CaptureScreenshot
                       || settings.ScreenshotEveryStep
                       || (!ok && settings.ScreenshotOnFailure);

        if (wantShot)
            result.ScreenshotFile = await ShootAsync(page, folder, index, ok, ct);

        // A navigation tears the page down and brings it back; give it a moment to reconnect
        // before the next step is sent into a document that is on its way out.
        if (step.Action == StepAction.Navigate && ok)
            await SettleAsync(page, ct);

        return result;
    }

    /// <summary>
    /// Pulls the rrweb events off the page and writes them beside the run. Best effort - losing
    /// the recording must not lose the run.
    /// </summary>
    private async Task<string?> SaveTapeAsync(PageSession page, string folder)
    {
        try
        {
            var answer = await page.RequestAsync("tape", new { action = "stop" }, TimeSpan.FromSeconds(30));
            if (answer is null) return null;

            if (!answer.Value.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                return null;
            if (events.GetArrayLength() == 0) return null;

            const string name = "session.rrweb.json";
            await File.WriteAllTextAsync(Path.Combine(folder, name), events.GetRawText());
            return name;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No session recording for this run");
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Asks the page to photograph itself. Best effort - a run should not fail over a picture.</summary>
    private async Task<string?> ShootAsync(PageSession page, string folder, int index, bool ok, CancellationToken ct)
    {
        try
        {
            var answer = await page.RequestAsync("shot", new { }, TimeSpan.FromSeconds(20), ct);
            if (answer is null) return null;

            if (!answer.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                return null;

            var raw = data.GetString() ?? "";
            var comma = raw.IndexOf(',');
            if (comma < 0) return null;

            var bytes = Convert.FromBase64String(raw[(comma + 1)..]);
            var name = $"step-{index + 1:D3}-{(ok ? "ok" : "fail")}.png";
            await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, ct);
            return name;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No screenshot for step {Index}", index + 1);
            return null;
        }
    }

    /// <summary>Waits for the page to come back after it navigates.</summary>
    private static async Task SettleAsync(PageSession page, CancellationToken ct)
    {
        for (var i = 0; i < 40 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(100, ct);
            var alive = await page.RequestAsync("step",
                new { step = new { action = "Comment" } }, TimeSpan.FromMilliseconds(400), ct);
            if (alive is not null) return;
        }
    }

    private async Task BarAsync(PageSession page, string method, string text)
    {
        try { await page.SendAsync(JsonSerializer.Serialize(new { type = "runbar", method, text })); }
        catch (Exception ex) { _log.LogDebug(ex, "Run bar unavailable"); }
    }

    private static StepResult Skipped(TestStep step, int index) => new()
    {
        StepId = step.Id,
        Index = index,
        Action = step.Action,
        Selector = step.Selector,
        Status = StepStatus.Skipped
    };

    private static string Summary(RunResult run) =>
        run.Status == RunStatus.Passed
            ? $"Passed - {run.Passed} steps"
            : $"{run.Status} - {run.Passed} passed, {run.Failed} failed";

    private void Raise() => Changed?.Invoke();
}
