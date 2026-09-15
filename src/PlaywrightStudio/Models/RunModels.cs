namespace PlaywrightStudio.Models;

public enum RunStatus { Pending, Running, Passed, Failed, Aborted }
public enum StepStatus { Pending, Running, Passed, Failed, Skipped }

public class StepResult
{
    public string StepId { get; set; } = "";
    public int Index { get; set; }
    public StepAction Action { get; set; }
    public string? Selector { get; set; }
    public string? Value { get; set; }
    public string Title { get; set; } = "";
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string? Error { get; set; }
    public long DurationMs { get; set; }
    /// <summary>File name (not full path) of a screenshot inside the run folder.</summary>
    public string? ScreenshotFile { get; set; }
    public string? Note { get; set; }
}

public class RunResult
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
    public string ScenarioId { get; set; } = "";
    public string ScenarioName { get; set; } = "";
    public string StartUrl { get; set; } = "";
    public string Browser { get; set; } = "chromium";
    public bool Headless { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public string? Error { get; set; }
    public List<StepResult> Steps { get; set; } = new();
    public List<string> Log { get; set; } = new();
    /// <summary>File name of the Playwright trace zip inside the run folder, when tracing was on.</summary>
    public string? TraceFile { get; set; }
    public string? VideoFile { get; set; }

    public TimeSpan Duration => (FinishedUtc ?? DateTimeOffset.UtcNow) - StartedUtc;
    public int Passed => Steps.Count(s => s.Status == StepStatus.Passed);
    public int Failed => Steps.Count(s => s.Status == StepStatus.Failed);
    public int Skipped => Steps.Count(s => s.Status == StepStatus.Skipped);
}
