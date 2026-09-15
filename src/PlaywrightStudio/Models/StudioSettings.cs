namespace PlaywrightStudio.Models;

/// <summary>How sharp the run video is captured. Playwright's own default shrinks the frame
/// to fit an 800x800 box, which softens text badly at a normal viewport.</summary>
public enum VideoResolution
{
    MatchViewport,
    PlaywrightDefault,
    Hd720,
    Hd1080
}

/// <summary>Encode quality for the mp4. Trades file size against sharpness.</summary>
public enum VideoQuality
{
    Small,
    Balanced,
    High
}

public class StudioSettings
{
    /// <summary>chromium | firefox | webkit | msedge | chrome</summary>
    public string Browser { get; set; } = "chromium";

    /// <summary>Run scenarios without a visible window. Recording is always headed.</summary>
    public bool Headless { get; set; }

    /// <summary>Milliseconds Playwright waits between actions during a run - handy for watching a run.</summary>
    public int SlowMoMs { get; set; } = 250;

    /// <summary>Default per-step timeout.</summary>
    public int DefaultTimeoutMs { get; set; } = 10_000;

    public int ViewportWidth { get; set; } = 1440;
    public int ViewportHeight { get; set; } = 900;

    /// <summary>Screenshot every step, not just failures.</summary>
    public bool ScreenshotEveryStep { get; set; }
    public bool ScreenshotOnFailure { get; set; } = true;

    /// <summary>Record a Playwright trace zip you can open with `playwright show-trace`.</summary>
    public bool RecordTrace { get; set; }
    public bool RecordVideo { get; set; }

    /// <summary>Frame size of the run video.</summary>
    public VideoResolution VideoResolution { get; set; } = VideoResolution.MatchViewport;

    /// <summary>How hard the mp4 is compressed.</summary>
    public VideoQuality VideoQuality { get; set; } = VideoQuality.Balanced;

    /// <summary>Keep the browser open after a run so you can inspect the end state.</summary>
    public bool KeepBrowserOpenAfterRun { get; set; }

    /// <summary>Reuse a signed-in profile: path to a Playwright storageState json, or blank.</summary>
    public string? StorageStatePath { get; set; }

    public bool IgnoreHttpsErrors { get; set; }

    /// <summary>
    /// Drive the browser you are already using instead of launching a private one. Needs that
    /// browser started with a remote debugging port - Settings has a button for it.
    /// </summary>
    public bool AttachToMyBrowser { get; set; }

    /// <summary>Where that browser is listening.</summary>
    public string CdpEndpoint { get; set; } = "http://localhost:9222";

    /// <summary>Full path to an ffmpeg that can write mp4. Blank = look on PATH and the usual places.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Namespace used when exporting generated test code.</summary>
    public string ExportNamespace { get; set; } = "UiTests";
}
