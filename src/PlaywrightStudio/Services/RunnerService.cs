using System.Diagnostics;
using Microsoft.Playwright;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>Plays a scenario back through Playwright and records what happened, step by step.</summary>
public sealed class RunnerService
{
    private readonly PlaywrightHost _host;
    private readonly SettingsService _settings;
    private readonly StudioPaths _paths;
    private readonly RunStore _runs;
    private readonly VideoConverter _video;
    private readonly ILogger<RunnerService> _log;

    private CancellationTokenSource? _cts;
    private string? _runBarJs;

    public RunnerService(PlaywrightHost host, SettingsService settings, StudioPaths paths, RunStore runs,
                         VideoConverter video, ILogger<RunnerService> log)
    {
        _host = host;
        _settings = settings;
        _paths = paths;
        _runs = runs;
        _video = video;
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public RunResult? Current { get; private set; }
    public event Action? Changed;

    private void Raise() => Changed?.Invoke();

    public void Abort() => _cts?.Cancel();

    public async Task<RunResult> RunAsync(Scenario scenario, int startIndex = 0, bool? headlessOverride = null)
    {
        var settings = _settings.Current;
        var headless = headlessOverride ?? settings.Headless;

        var run = new RunResult
        {
            ScenarioId = scenario.Id,
            ScenarioName = scenario.Name,
            StartUrl = scenario.StartUrl,
            Browser = settings.Browser,
            Headless = headless,
            Status = RunStatus.Running
        };

        var steps = scenario.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            run.Steps.Add(new StepResult
            {
                StepId = s.Id,
                Index = i,
                Action = s.Action,
                Selector = s.Selector,
                Value = s.Value,
                Title = s.Summarise(),
                Status = (!s.Enabled || i < startIndex) ? StepStatus.Skipped : StepStatus.Pending
            });
        }

        Current = run;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Raise();

        var folder = _paths.RunFolder(run.Id);
        PlaywrightHost.Session? session = null;
        IBrowserContext? context = null;
        IPage? openedPage = null;

        try
        {
            session = await _host.OpenAsync(settings, headless, settings.SlowMoMs);
            if (!session.Owned)
            {
                run.Browser = settings.Browser + " (your browser)";
                run.Log.Add("Attached to the browser you are using; the run happens in a tab there.");
            }
            context = await _host.ContextForAsync(session, settings,
                settings.RecordVideo && session.Owned ? Path.Combine(folder, "video") : null);

            if (settings.RecordTrace && session.Owned)
            {
                await context.Tracing.StartAsync(new TracingStartOptions
                {
                    Screenshots = true,
                    Snapshots = true,
                    Sources = false,
                    Title = scenario.Name
                });
            }

            // A headed run gets a bar showing progress, with its own Stop button, so you can
            // call it off from the browser you are watching rather than the studio.
            var showRunBar = !headless;
            if (showRunBar)
            {
                try
                {
                    _runBarJs ??= await File.ReadAllTextAsync(
                        Path.Combine(AppContext.BaseDirectory, "Assets", "runbar.js"), ct);
                    await context.ExposeBindingAsync("__pwsRunStop", (BindingSource _, string _) =>
                    {
                        run.Log.Add("Stopped from the browser window.");
                        Abort();
                    });
                    await context.AddInitScriptAsync(_runBarJs);
                }
                catch (Exception ex)
                {
                    // The bar is a convenience. Losing it must never cost you the run.
                    _log.LogWarning(ex, "Could not install the run bar");
                    run.Log.Add("The on-page run bar could not be loaded, so the run has no Stop button "
                                + "in the browser. Stop it from the studio instead. (" + Tidy(ex.Message) + ")");
                    showRunBar = false;
                }
            }

            var page = await context.NewPageAsync();
            openedPage = page;

            // A journey that opens a tab used to become unreplayable: every later step kept
            // looking at the old page. Follow the newest tab, and fall back when it closes.
            var active = page;
            void Wire(IPage p)
            {
                // Playwright dismisses dialogs by default, but while recording you saw the box
                // and clicked OK - so accept, and say so in the log.
                p.Dialog += async (_, dialog) =>
                {
                    run.Log.Add($"Accepted a {dialog.Type} dialog: \"{dialog.Message}\"");
                    try { await dialog.AcceptAsync(); } catch { /* already handled */ }
                };
                p.Close += (_, _) =>
                {
                    var open = context.Pages.LastOrDefault(x => !x.IsClosed);
                    if (open is not null) active = open;
                };
            }

            // Site duplication: one capture per distinct page the journey reaches.
            var captureDir = scenario.CaptureForDuplication
                ? (string.IsNullOrWhiteSpace(scenario.CaptureFolder)
                    ? Path.Combine(_paths.Captures, CodeExporter.Slug(scenario.Name))
                    : scenario.CaptureFolder!)
                : null;
            var captured = new List<string>();
            var capturedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (captureDir is not null)
            {
                Directory.CreateDirectory(captureDir);
                run.Log.Add("Capturing pages for duplication into " + captureDir);
            }

            Wire(page);
            context.Page += (_, opened) =>
            {
                Wire(opened);
                active = opened;
                run.Log.Add("A new tab opened; the run followed it.");
            };

            // If the scenario does not open with a Navigate step, use the start url.
            var firstLive = steps.Skip(startIndex).FirstOrDefault(s => s.Enabled);
            if (!string.IsNullOrWhiteSpace(scenario.StartUrl) &&
                (firstLive is null || firstLive.Action != StepAction.Navigate))
            {
                run.Log.Add($"Opening start url {scenario.StartUrl}");
                await page.GotoAsync(scenario.StartUrl);
            }

            if (showRunBar) await CallRunBarAsync(active, "begin", $"Starting {scenario.Name}…");

            // The landing page is reached before the first step, so capture it here or it is
            // missed entirely the moment a click navigates away.
            if (captureDir is not null)
                await CapturePageAsync(active, captureDir, capturedUrls, captured, run);

            for (var i = startIndex; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = steps[i];
                var result = run.Steps[i];
                if (!step.Enabled) continue;

                result.Status = StepStatus.Running;
                Raise();

                if (showRunBar)
                    await UpdateRunBarAsync(active, $"Step {i + 1} of {steps.Count} · {step.Summarise()}");

                var sw = Stopwatch.StartNew();
                try
                {
                    if (step.DelayBeforeMs > 0) await Task.Delay(step.DelayBeforeMs, ct);
                    if (active != page && !active.IsClosed) await active.BringToFrontAsync();

                    var pagesBefore = context.Pages.Count;
                    try
                    {
                        await ExecuteAsync(active, step, scenario, settings, result, folder, ct);
                    }
                    catch (Exception ex) when (showRunBar && ex.Message.Contains("pws-runbar", StringComparison.Ordinal))
                    {
                        // The bar was sitting on top of the very thing this step needed to click.
                        await CallRunBarAsync(active, "hide", "");
                        try { await ExecuteAsync(active, step, scenario, settings, result, folder, ct); }
                        finally { await CallRunBarAsync(active, "show", ""); }
                        result.Note = "The run bar was covering this element, so it stepped aside for this action.";
                    }

                    // A click returns before Playwright reports the tab it opened. Without this
                    // pause the next step binds its selector to the old page and waits out its
                    // whole timeout on a tab that moved on.
                    if (Opens(step.Action))
                    {
                        var settled = await SettleForNewTabAsync(context, pagesBefore, ct);
                        if (settled is not null) active = settled;
                    }

                    result.Status = StepStatus.Passed;
                }
                catch (OperationCanceledException)
                {
                    result.Status = StepStatus.Skipped;
                    throw;
                }
                catch (Exception ex)
                {
                    result.Status = StepStatus.Failed;
                    result.Error = Tidy(ex.Message) + await DiagnoseAsync(active, step, scenario);
                    if (settings.ScreenshotOnFailure)
                        result.ScreenshotFile = await ShootAsync(active, folder, i, "fail");
                    if (!step.ContinueOnFailure)
                    {
                        run.Status = RunStatus.Failed;
                        run.Error = $"Step {i + 1} failed: {result.Error}";
                        for (var j = i + 1; j < run.Steps.Count; j++)
                            if (run.Steps[j].Status == StepStatus.Pending) run.Steps[j].Status = StepStatus.Skipped;
                        break;
                    }
                }
                finally
                {
                    sw.Stop();
                    result.DurationMs = sw.ElapsedMilliseconds;
                    Raise();
                }

                if (captureDir is not null && result.Status == StepStatus.Passed)
                    await CapturePageAsync(active, captureDir, capturedUrls, captured, run);

                // A Screenshot step already took one; don't shoot the same moment twice.
                if (result.Status == StepStatus.Passed && step.Action != StepAction.Screenshot &&
                    (settings.ScreenshotEveryStep || step.CaptureScreenshot))
                {
                    result.ScreenshotFile = await ShootAsync(active, folder, i, "ok");
                    Raise();
                }
            }

            if (captureDir is not null)
            {
                await CapturePageAsync(active, captureDir, capturedUrls, captured, run);
                var manifest = new System.Text.StringBuilder()
                    .AppendLine($"# {scenario.Name}")
                    .AppendLine()
                    .AppendLine($"Captured {DateTime.Now:yyyy-MM-dd HH:mm}. {captured.Count} page(s), "
                               + "each saved as a full-page png next to its html.")
                    .AppendLine();
                foreach (var entry in captured) manifest.AppendLine("- " + entry);
                await File.WriteAllTextAsync(Path.Combine(captureDir, "pages.md"), manifest.ToString(), ct);
                run.Log.Add($"Captured {captured.Count} page(s) for duplication.");
            }

            if (run.Status == RunStatus.Running)
                run.Status = run.Failed > 0 ? RunStatus.Failed : RunStatus.Passed;

            if (settings.RecordTrace && session.Owned)
            {
                run.TraceFile = "trace.zip";
                await context.Tracing.StopAsync(new TracingStopOptions { Path = Path.Combine(folder, "trace.zip") });
            }

            if (showRunBar)
            {
                var summary = run.Status == RunStatus.Passed
                    ? $"Finished · {run.Passed} of {run.Steps.Count} steps passed"
                    : $"{run.Status} · {run.Passed} passed, {run.Failed} failed";
                await CallRunBarAsync(active, "finish", summary);
            }

            if (!session.Owned || (settings.KeepBrowserOpenAfterRun && !headless))
            {
                run.Log.Add(session.Owned
                    ? "Browser left open - close the window when you are done."
                    : "The tab is still open in your browser - carry on from where the run stopped.");
                context = null;                 // never close what we did not open
                openedPage = null;
                session = session with { Owned = false };
            }
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Aborted;
            run.Error = "Run stopped by you.";
            foreach (var r in run.Steps.Where(r => r.Status is StepStatus.Pending or StepStatus.Running))
                r.Status = StepStatus.Skipped;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run blew up");
            run.Status = RunStatus.Failed;
            run.Error = Tidy(ex.Message);
        }
        finally
        {
            try
            {
                if (context is not null && session?.Owned == true)
                {
                    await context.CloseAsync();
                    if (_settings.Current.RecordVideo)
                    {
                        var videoDir = Path.Combine(folder, "video");
                        if (Directory.Exists(videoDir))
                        {
                            var file = Directory.EnumerateFiles(videoDir, "*.webm").FirstOrDefault();
                            if (file is not null)
                            {
                                // Playwright writes webm; the studio keeps mp4, next to the app
                                // under a name you can recognise in Explorer.
                                var (name, warning) = await _video.ToMp4Async(file);
                                if (warning is not null) run.Log.Add(warning);

                                var produced = Path.Combine(videoDir, name);
                                var friendly = $"{CodeExporter.Slug(scenario.Name)}-{run.Id}{Path.GetExtension(name)}";
                                var destination = Path.Combine(_paths.Videos, friendly);
                                try
                                {
                                    File.Move(produced, destination, overwrite: true);
                                    Directory.Delete(videoDir, recursive: true);
                                    run.VideoFile = friendly;
                                    run.Log.Add("Video saved to " + destination);
                                }
                                catch (Exception ex)
                                {
                                    _log.LogWarning(ex, "Could not move the video next to the app");
                                    run.VideoFile = friendly;
                                    run.Log.Add("Video left in the run folder: " + produced);
                                }
                            }
                        }
                    }
                }
                if (session is { Owned: true }) await session.Browser.CloseAsync();
            }
            catch (Exception ex) { _log.LogDebug(ex, "Cleanup after run"); }

            run.FinishedUtc = DateTimeOffset.UtcNow;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            _runs.Save(run);
            Raise();
        }

        return run;
    }

    // ------------------------------------------------------------------ one step

    private async Task ExecuteAsync(IPage page, TestStep step, Scenario scenario, StudioSettings settings,
                                    StepResult result, string folder, CancellationToken ct)
    {
        var timeout = (float)(step.TimeoutMs ?? settings.DefaultTimeoutMs);
        var value = ValueResolver.Resolve(step.Value, scenario.Variables) ?? "";
        var selector = ValueResolver.Resolve(step.Selector, scenario.Variables);

        if (step.Action == StepAction.Comment) { result.Note = value; return; }

        if (step.Action == StepAction.Navigate)
        {
            await page.GotoAsync(value, new PageGotoOptions { Timeout = timeout });
            return;
        }
        if (step.Action == StepAction.WaitForTimeout)
        {
            var ms = int.TryParse(value, out var v) ? v : 1000;
            await Task.Delay(ms, ct);
            return;
        }
        if (step.Action == StepAction.WaitForUrl)
        {
            await page.WaitForURLAsync(value, new PageWaitForURLOptions { Timeout = timeout });
            return;
        }
        if (step.Action == StepAction.AssertUrl)
        {
            await PollAsync(() => Task.FromResult(Matches(page.Url, value)), timeout, ct,
                () => $"Url is \"{page.Url}\", expected {Describe(value)}");
            return;
        }
        if (step.Action == StepAction.AssertTitle)
        {
            string title = "";
            await PollAsync(async () => { title = await page.TitleAsync(); return Matches(title, value); }, timeout, ct,
                () => $"Title is \"{title}\", expected {Describe(value)}");
            return;
        }
        if (step.Action == StepAction.Screenshot)
        {
            result.ScreenshotFile = await ShootAsync(page, folder, result.Index, "shot");
            return;
        }

        if (string.IsNullOrWhiteSpace(selector))
            throw new InvalidOperationException($"{step.Action} needs a selector.");

        // Steps that are legitimately about hidden elements opt out of visible-only matching.
        var wantsHidden = step.Action is StepAction.AssertHidden or StepAction.WaitForHidden
                                      or StepAction.AssertCount or StepAction.Upload;

        var all = await ChooseSelectorAsync(page, step, scenario, selector!, wantsHidden, timeout, result, ct);

        // A loose selector very often matches a hidden duplicate first - a collapsed mobile nav,
        // an offscreen carousel slide. Taking the first match in DOM order then waits forever for
        // something that will never be clickable, so act on the first *visible* match instead.
        var locator = wantsHidden ? all.First : all.Filter(new LocatorFilterOptions { Visible = true }).First;

        switch (step.Action)
        {
            case StepAction.Click:
                await locator.ClickAsync(new LocatorClickOptions { Timeout = timeout });
                break;
            case StepAction.DoubleClick:
                await locator.DblClickAsync(new LocatorDblClickOptions { Timeout = timeout });
                break;
            case StepAction.RightClick:
                await locator.ClickAsync(new LocatorClickOptions { Timeout = timeout, Button = MouseButton.Right });
                break;
            case StepAction.Fill:
                await locator.FillAsync(value, new LocatorFillOptions { Timeout = timeout });
                break;
            case StepAction.Type:
                await locator.PressSequentiallyAsync(value, new LocatorPressSequentiallyOptions { Timeout = timeout, Delay = 45 });
                break;
            case StepAction.Press:
                await locator.PressAsync(string.IsNullOrEmpty(value) ? "Enter" : value, new LocatorPressOptions { Timeout = timeout });
                break;
            case StepAction.Select:
                var options = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                try
                {
                    await locator.SelectOptionAsync(options, new LocatorSelectOptionOptions { Timeout = timeout });
                }
                catch
                {
                    // Option values are often database ids that change between environments,
                    // while the visible label stays put. Try that before giving up.
                    await locator.SelectOptionAsync(
                        options.Select(o => new SelectOptionValue { Label = o }).ToArray(),
                        new LocatorSelectOptionOptions { Timeout = timeout });
                    result.Note = "No option had that value, so it was selected by its visible label instead.";
                }
                break;
            case StepAction.Check:
                await locator.CheckAsync(new LocatorCheckOptions { Timeout = timeout });
                break;
            case StepAction.Uncheck:
                await locator.UncheckAsync(new LocatorUncheckOptions { Timeout = timeout });
                break;
            case StepAction.Hover:
                await locator.HoverAsync(new LocatorHoverOptions { Timeout = timeout });
                break;
            case StepAction.Focus:
                await locator.FocusAsync(new LocatorFocusOptions { Timeout = timeout });
                break;
            case StepAction.ScrollIntoView:
                await locator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = timeout });
                break;
            case StepAction.Upload:
                var files = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var missing = files.Where(f => !File.Exists(f)).ToList();
                if (missing.Count > 0)
                    throw new FileNotFoundException("Upload needs full paths on this machine. Not found: " + string.Join(", ", missing));
                await locator.SetInputFilesAsync(files, new LocatorSetInputFilesOptions { Timeout = timeout });
                break;
            case StepAction.WaitForSelector:
                await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeout });
                break;
            case StepAction.WaitForHidden:
                await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = timeout });
                break;
            case StepAction.AssertVisible:
                await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeout });
                break;
            case StepAction.AssertHidden:
                await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = timeout });
                break;
            case StepAction.AssertText:
                var text = "";
                await PollAsync(async () =>
                {
                    text = (await locator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1500 })).Trim();
                    return Matches(text, value);
                }, timeout, ct, () => $"Text is \"{Trim(text)}\", expected {Describe(value)}");
                break;
            case StepAction.AssertValue:
                var actual = "";
                await PollAsync(async () =>
                {
                    actual = await locator.InputValueAsync(new LocatorInputValueOptions { Timeout = 1500 });
                    return Matches(actual, value);
                }, timeout, ct, () => $"Value is \"{Trim(actual)}\", expected {Describe(value)}");
                break;
            case StepAction.AssertChecked:
                var want = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                var isChecked = false;
                await PollAsync(async () =>
                {
                    isChecked = await locator.IsCheckedAsync(new LocatorIsCheckedOptions { Timeout = 1500 });
                    return isChecked == want;
                }, timeout, ct, () => $"Checked is {isChecked}, expected {want}");
                break;
            case StepAction.AssertCount:
                var expected = int.TryParse(value, out var c) ? c : 1;
                var got = 0;
                await PollAsync(async () => { got = await all.CountAsync(); return got == expected; }, timeout, ct,
                    () => $"Found {got} matching element(s), expected {expected}");
                break;
            default:
                throw new NotSupportedException($"{step.Action} is not implemented.");
        }

        if (step.Action != StepAction.AssertCount)
        {
            try
            {
                var matches = await all.CountAsync();
                if (matches > 1)
                {
                    var visible = await all.Filter(new LocatorFilterOptions { Visible = true }).CountAsync();
                    result.Note = wantsHidden
                        ? $"{matches} elements matched - the first one was used."
                        : $"{matches} elements matched, {visible} of them visible - the first visible one was used.";
                }
            }
            catch { /* the page may have navigated away, which is fine */ }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>`=exact`, `/regex/` or plain contains.</summary>
    private static bool Matches(string actual, string expected)
    {
        actual ??= "";
        if (string.IsNullOrEmpty(expected)) return true;
        if (expected.StartsWith('=')) return string.Equals(actual.Trim(), expected[1..].Trim(), StringComparison.Ordinal);
        if (expected.Length > 1 && expected.StartsWith('/') && expected.EndsWith('/'))
            return System.Text.RegularExpressions.Regex.IsMatch(actual, expected[1..^1]);
        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(string expected) =>
        expected.StartsWith('=') ? $"exactly \"{expected[1..]}\""
        : expected.Length > 1 && expected.StartsWith('/') && expected.EndsWith('/') ? $"to match /{expected[1..^1]}/"
        : $"to contain \"{expected}\"";

    private static async Task PollAsync(Func<Task<bool>> check, float timeoutMs, CancellationToken ct, Func<string> onFail)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { if (await check()) return; last = null; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
            await Task.Delay(200, ct);
        }
        throw new Exception(onFail() + (last is null ? "" : $" ({Tidy(last.Message)})"));
    }

    private async Task<string?> ShootAsync(IPage page, string folder, int index, string tag)
    {
        try
        {
            var name = $"step-{index + 1:D3}-{tag}.png";
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, name) });
            return name;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Screenshot failed");
            return null;
        }
    }

    /// <summary>
    /// A bare "Timeout exceeded" says nothing useful. Look at the page and report what was
    /// actually there: the frame, the number of matches, and how many were visible.
    /// </summary>
    private static async Task<string> DiagnoseAsync(IPage page, TestStep step, Scenario scenario)
    {
        var notes = new List<string>();

        if (step.FrameChain.Count > 0)
        {
            var outer = step.FrameChain[0];
            try
            {
                var frames = await page.Locator(outer).CountAsync();
                if (frames == 0)
                    notes.Add($"No iframe matched \"{outer}\" this time, so the frame this step was "
                              + "recorded in never appeared. If that is a pop-up that only shows "
                              + "sometimes, turn on \"Carry on if this fails\" for this step.");
                else if (frames > 1)
                    notes.Add($"{frames} iframes matched \"{outer}\" and the first was used - it may be the wrong one.");
            }
            catch { /* nothing useful to add */ }
        }

        var selector = ValueResolver.Resolve(step.Selector, scenario.Variables);
        if (!string.IsNullOrWhiteSpace(selector) && step.Action.NeedsSelector())
        {
            try
            {
                var all = RecorderService.Resolve(page, selector!, step.FrameChain);
                var total = await all.CountAsync();
                var visible = total == 0 ? 0 : await all.Filter(new LocatorFilterOptions { Visible = true }).CountAsync();

                if (total == 0)
                    notes.Add("Nothing on the page matches that selector - the page may have changed, "
                              + "or the step before this one did not get you where you expected.");
                else if (visible == 0)
                    notes.Add($"{total} element(s) match, but none are visible, so none can be clicked or typed into.");
                else if (total > 1)
                    notes.Add($"{total} elements match and {visible} are visible.");
            }
            catch { /* the frame itself is probably the problem, already covered above */ }
        }

        return notes.Count == 0 ? "" : " " + string.Join(" ", notes);
    }

    private static string Trim(string? s) => (s ?? "").Length > 120 ? s![..120] + "…" : s ?? "";

    /// <summary>
    /// Saves a page once per distinct url: a full-page screenshot and the html behind it, which
    /// together are enough to rebuild the page later.
    /// </summary>
    private async Task CapturePageAsync(IPage page, string folder, HashSet<string> seen,
                                        List<string> captured, RunResult run)
    {
        try
        {
            var url = page.Url;
            if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return;
            if (!seen.Add(url)) return;                       // already have this page

            var name = $"{seen.Count:D2}-{FileNameFor(url)}";
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(folder, name + ".png"),
                FullPage = true
            });
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".html"), await page.ContentAsync());
            captured.Add($"`{name}` - {url}");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not capture a page for duplication");
            run.Log.Add("A page could not be captured: " + Tidy(ex.Message));
        }
    }

    /// <summary>Turns a url into something recognisable and legal as a file name.</summary>
    private static string FileNameFor(string url)
    {
        var text = url;
        try
        {
            var uri = new Uri(url);
            text = uri.Host + uri.AbsolutePath;
        }
        catch { /* keep the raw string */ }

        var slug = CodeExporter.Slug(text);
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }

    private static Task UpdateRunBarAsync(IPage page, string text) => CallRunBarAsync(page, "update", text);

    /// <summary>Best-effort: a bar that is not there yet must never fail a run.</summary>
    private static async Task CallRunBarAsync(IPage page, string method, string text)
    {
        try
        {
            await page.EvaluateAsync(
                "args => window.__pwsRunBar && window.__pwsRunBar[args.m] && window.__pwsRunBar[args.m](args.t)",
                new { m = method, t = text });
        }
        catch { /* mid-navigation, or the page went away */ }
    }

    /// <summary>
    /// A full run - results, timings, screenshots, history - executed in a window that is already
    /// open rather than a fresh one. No video or trace: those are fixed when a context is created,
    /// and this context belongs to the recording session.
    /// </summary>
    public async Task<RunResult> RunInSessionAsync(IPage page, Scenario scenario, int startIndex = 0,
                                                   Action<string>? progress = null)
    {
        var settings = _settings.Current;
        var run = new RunResult
        {
            ScenarioId = scenario.Id,
            ScenarioName = scenario.Name,
            StartUrl = scenario.StartUrl,
            Browser = settings.Browser + " (open window)",
            Headless = false,
            Status = RunStatus.Running
        };

        var steps = scenario.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            run.Steps.Add(new StepResult
            {
                StepId = step.Id,
                Index = i,
                Action = step.Action,
                Selector = step.Selector,
                Value = step.Value,
                Title = step.Summarise(),
                Status = (!step.Enabled || i < startIndex) ? StepStatus.Skipped : StepStatus.Pending
            });
        }

        Current = run;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Raise();

        var folder = _paths.RunFolder(run.Id);
        await CallRunBarAsync(page, "begin", $"Starting {scenario.Name}…");

        try
        {
            for (var i = startIndex; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = steps[i];
                var result = run.Steps[i];
                if (!step.Enabled) continue;

                result.Status = StepStatus.Running;
                progress?.Invoke($"Step {i + 1} of {steps.Count}: {step.Summarise()}");
                await UpdateRunBarAsync(page, $"Step {i + 1} of {steps.Count} · {step.Summarise()}");
                Raise();

                var sw = Stopwatch.StartNew();
                try
                {
                    if (step.DelayBeforeMs > 0) await Task.Delay(step.DelayBeforeMs, ct);
                    try
                    {
                        await ExecuteAsync(page, step, scenario, settings, result, folder, ct);
                    }
                    catch (Exception ex) when (ex.Message.Contains("pws-runbar", StringComparison.Ordinal))
                    {
                        await CallRunBarAsync(page, "hide", "");
                        try { await ExecuteAsync(page, step, scenario, settings, result, folder, ct); }
                        finally { await CallRunBarAsync(page, "show", ""); }
                        result.Note = "The run bar was covering this element, so it stepped aside.";
                    }
                    result.Status = StepStatus.Passed;
                }
                catch (OperationCanceledException)
                {
                    result.Status = StepStatus.Skipped;
                    throw;
                }
                catch (Exception ex)
                {
                    result.Status = StepStatus.Failed;
                    result.Error = Tidy(ex.Message) + await DiagnoseAsync(page, step, scenario);
                    if (settings.ScreenshotOnFailure)
                        result.ScreenshotFile = await ShootAsync(page, folder, i, "fail");
                    if (!step.ContinueOnFailure)
                    {
                        run.Status = RunStatus.Failed;
                        run.Error = $"Step {i + 1} failed: {result.Error}";
                        foreach (var rest in run.Steps.Skip(i + 1).Where(r => r.Status == StepStatus.Pending))
                            rest.Status = StepStatus.Skipped;
                        break;
                    }
                }
                finally
                {
                    sw.Stop();
                    result.DurationMs = sw.ElapsedMilliseconds;
                    Raise();
                }

                if (result.Status == StepStatus.Passed && step.Action != StepAction.Screenshot &&
                    (settings.ScreenshotEveryStep || step.CaptureScreenshot))
                {
                    result.ScreenshotFile = await ShootAsync(page, folder, i, "ok");
                    Raise();
                }
            }

            if (run.Status == RunStatus.Running)
                run.Status = run.Failed > 0 ? RunStatus.Failed : RunStatus.Passed;
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Aborted;
            run.Error = "Run stopped by you.";
            foreach (var r in run.Steps.Where(r => r.Status is StepStatus.Pending or StepStatus.Running))
                r.Status = StepStatus.Skipped;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Run in the open window blew up");
            run.Status = RunStatus.Failed;
            run.Error = Tidy(ex.Message);
        }
        finally
        {
            run.FinishedUtc = DateTimeOffset.UtcNow;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            _runs.Save(run);
            Raise();

            var summary = run.Status == RunStatus.Passed
                ? $"Finished · {run.Passed} of {run.Steps.Count} steps passed"
                : $"{run.Status} · {run.Passed} passed, {run.Failed} failed";
            await CallRunBarAsync(page, "finish", summary);
        }

        return run;
    }

    /// <summary>Actions that can open a tab, and so are worth pausing after.</summary>    /// <summary>Actions that can open a tab, and so are worth pausing after.</summary>
    private static bool Opens(StepAction action) =>
        action is StepAction.Click or StepAction.DoubleClick or StepAction.Press or StepAction.Navigate;

    /// <summary>
    /// Waits briefly for a tab opened by the step just run, returning it once its document is
    /// ready. Gives up quickly so steps that open nothing are barely slowed.
    /// </summary>
    private static async Task<IPage?> SettleForNewTabAsync(IBrowserContext context, int pagesBefore, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < deadline)
        {
            if (context.Pages.Count > pagesBefore)
            {
                var opened = context.Pages.Last(p => !p.IsClosed);
                try { await opened.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 5000 }); }
                catch { /* it can still be usable */ }
                return opened;
            }
            await Task.Delay(40, ct);
        }
        return null;
    }

    /// <summary>
    /// The recorder keeps every selector it considered for an element. If the one we saved has
    /// stopped matching - a regenerated id, a renamed label, a restyled control - one of the
    /// others usually still does, so try them rather than failing outright. The primary gets
    /// the first slice of the budget; the rest are only checked if it comes up empty.
    /// </summary>
    private async Task<ILocator> ChooseSelectorAsync(IPage page, TestStep step, Scenario scenario,
                                                     string selector, bool wantsHidden, float timeout,
                                                     StepResult result, CancellationToken ct)
    {
        var primary = RecorderService.Resolve(page, selector, step.FrameChain);
        if (step.SelectorAlternatives.Count == 0) return primary;

        // Give the page a fair chance to render before deciding the selector is wrong.
        var grace = Math.Max(1500, timeout / 3);
        if (await WaitForMatchAsync(primary, wantsHidden, grace, ct)) return primary;

        foreach (var raw in step.SelectorAlternatives)
        {
            ct.ThrowIfCancellationRequested();
            var alternative = ValueResolver.Resolve(raw, scenario.Variables);
            if (string.IsNullOrWhiteSpace(alternative)) continue;
            try
            {
                var candidate = RecorderService.Resolve(page, alternative!, step.FrameChain);
                if (!await WaitForMatchAsync(candidate, wantsHidden, 400, ct)) continue;

                result.Note = $"\"{selector}\" matched nothing, so the recorded alternative "
                            + $"\"{alternative}\" was used instead. Worth making it the step's selector.";
                return candidate;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* a malformed alternative should not sink the step */ }
        }

        // Nothing matched. Hand back the primary so the failure is reported against it.
        return primary;
    }

    private static async Task<bool> WaitForMatchAsync(ILocator locator, bool wantsHidden, float budgetMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hit = wantsHidden
                    ? await locator.CountAsync() > 0
                    : await locator.Filter(new LocatorFilterOptions { Visible = true }).CountAsync() > 0;
                if (hit) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* frame not there yet */ }

            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(150, ct);
        }
    }

    /// <summary>
    /// Playwright's call log explains *why* an action never became possible - an overlay eating
    /// the click, an element that stayed disabled. Dropping the whole log threw that away, so
    /// keep the one line that answers the question.
    /// </summary>
    private static string Tidy(string message)
    {
        string? hint = null;
        var cut = message.IndexOf("Call log:", StringComparison.Ordinal);
        if (cut > 0)
        {
            var log = message[cut..];
            var intercept = log.Split('\n')
                .Select(l => l.Trim(' ', '-', '\r'))
                .FirstOrDefault(l => l.Contains("intercepts pointer events", StringComparison.Ordinal));
            if (intercept is not null)
                hint = "Something is sitting on top of it and swallowing the click - often a cookie "
                     + $"banner or modal that was not up when you recorded: {Trim(intercept)}";
            else if (log.Contains("not visible", StringComparison.OrdinalIgnoreCase))
                hint = "The element never became visible.";
            else if (log.Contains("not enabled", StringComparison.OrdinalIgnoreCase))
                hint = "The element stayed disabled.";
            else if (log.Contains("not stable", StringComparison.OrdinalIgnoreCase))
                hint = "The element kept moving - an animation never settled.";
            else if (log.Contains("resolved to 0 elements", StringComparison.Ordinal))
                hint = "The selector resolved to nothing.";

            message = message[..cut];
        }

        message = message.Replace("\n", " ").Replace("\r", " ").Trim();
        return hint is null ? message : message + " " + hint;
    }
}
