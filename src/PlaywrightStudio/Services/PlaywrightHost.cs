using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// Owns the single <see cref="IPlaywright"/> instance and knows how to launch a browser
/// the way the studio settings ask for. Also wraps `playwright install`.
/// </summary>
public sealed class PlaywrightHost : IAsyncDisposable
{
    private readonly ILogger<PlaywrightHost> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;

    public PlaywrightHost(ILogger<PlaywrightHost> log) => _log = log;

    public async Task<IPlaywright> GetAsync()
    {
        if (_playwright is not null) return _playwright;
        await _gate.WaitAsync();
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            return _playwright;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A browser to work in, and whether the studio owns it. An attached browser is the one you
    /// are using yourself, so it must never be closed from under you.
    /// </summary>
    public record Session(IBrowser Browser, bool Owned);

    /// <summary>
    /// Attaches to the browser you are already running when that is switched on, otherwise
    /// launches a private one.
    /// </summary>
    public async Task<Session> OpenAsync(StudioSettings settings, bool headless, int slowMoMs)
    {
        if (settings.AttachToMyBrowser)
        {
            var pw = await GetAsync();
            var browser = await pw.Chromium.ConnectOverCDPAsync(settings.CdpEndpoint);
            return new Session(browser, Owned: false);
        }

        return new Session(await LaunchAsync(settings, headless, slowMoMs), Owned: true);
    }

    /// <summary>Is a debuggable browser actually listening where we expect?</summary>
    public async Task<(bool ok, string message)> ProbeAttachAsync(StudioSettings settings)
    {
        try
        {
            var pw = await GetAsync();
            var browser = await pw.Chromium.ConnectOverCDPAsync(settings.CdpEndpoint);
            var tabs = browser.Contexts.Sum(c => c.Pages.Count);
            var version = browser.Version;
            await browser.CloseAsync();          // detaches; it does not close your browser
            return (true, $"Attached to {version} at {settings.CdpEndpoint} - {tabs} tab(s) open.");
        }
        catch (Exception ex)
        {
            return (false, $"Nothing is listening at {settings.CdpEndpoint}. "
                         + "Use \"Open a debuggable browser\" below, then browse the studio in that window. "
                         + $"({Flatten(ex.Message)})");
        }
    }

    /// <summary>
    /// Starts Edge or Chrome with a remote debugging port and its own profile folder, which is
    /// the browser you then use for everything. A normal browser cannot be attached to: Chromium
    /// refuses remote debugging against the default profile.
    /// </summary>
    public (bool ok, string message) OpenDebuggableBrowser(StudioSettings settings, string profileDir, string? startUrl)
    {
        var port = 9222;
        try
        {
            var uri = new Uri(settings.CdpEndpoint);
            port = uri.Port;
        }
        catch { /* stick with the default */ }

        var exe = FindBrowserExecutable(settings.Browser);
        if (exe is null) return (false, "Could not find Edge or Chrome on this machine.");

        try
        {
            Directory.CreateDirectory(profileDir);
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = true };
            psi.ArgumentList.Add($"--remote-debugging-port={port}");
            psi.ArgumentList.Add($"--user-data-dir={profileDir}");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            if (!string.IsNullOrWhiteSpace(startUrl)) psi.ArgumentList.Add(startUrl);

            Process.Start(psi);
            return (true, $"Opened {Path.GetFileNameWithoutExtension(exe)} on port {port}. "
                        + "Use that window from now on - the studio will drive tabs in it.");
        }
        catch (Exception ex)
        {
            return (false, "Could not start the browser: " + ex.Message);
        }
    }

    private static string Flatten(string message) =>
        message.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string? FindBrowserExecutable(string? preferred)
    {
        var files = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var files64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var edge = new[]
        {
            Path.Combine(files, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(files64, "Microsoft", "Edge", "Application", "msedge.exe")
        };
        var chrome = new[]
        {
            Path.Combine(files, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(files64, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
        };

        var order = (preferred ?? "").ToLowerInvariant() == "chrome"
            ? chrome.Concat(edge)
            : edge.Concat(chrome);

        return order.FirstOrDefault(File.Exists);
    }

    /// <summary>Maps the settings' browser name onto a browser type plus channel.</summary>
    public async Task<IBrowser> LaunchAsync(StudioSettings settings, bool headless, int slowMoMs)
    {
        var pw = await GetAsync();
        var options = new BrowserTypeLaunchOptions
        {
            Headless = headless,
            SlowMo = slowMoMs <= 0 ? null : slowMoMs
        };

        IBrowserType type;
        switch ((settings.Browser ?? "chromium").ToLowerInvariant())
        {
            case "firefox":
                type = pw.Firefox;
                break;
            case "webkit":
                type = pw.Webkit;
                break;
            case "msedge":
                type = pw.Chromium;
                options.Channel = "msedge";
                break;
            case "chrome":
                type = pw.Chromium;
                options.Channel = "chrome";
                break;
            default:
                type = pw.Chromium;
                break;
        }

        return await type.LaunchAsync(options);
    }

    /// <summary>
    /// The context to work in. When attached, that is the profile you are already browsing in -
    /// your cookies, your logins - so none of the per-context options apply.
    /// </summary>
    public async Task<IBrowserContext> ContextForAsync(Session session, StudioSettings settings, string? videoDir = null)
    {
        if (!session.Owned)
        {
            var existing = session.Browser.Contexts.FirstOrDefault();
            if (existing is not null)
            {
                existing.SetDefaultTimeout(settings.DefaultTimeoutMs);
                existing.SetDefaultNavigationTimeout(Math.Max(settings.DefaultTimeoutMs, 30_000));
                return existing;
            }
        }

        return await NewContextAsync(session.Browser, settings, videoDir);
    }

    public async Task<IBrowserContext> NewContextAsync(IBrowser browser, StudioSettings settings, string? videoDir = null)
    {
        var options = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = Math.Max(320, settings.ViewportWidth),
                Height = Math.Max(320, settings.ViewportHeight)
            },
            IgnoreHTTPSErrors = settings.IgnoreHttpsErrors
        };

        if (!string.IsNullOrWhiteSpace(settings.StorageStatePath) && File.Exists(settings.StorageStatePath))
            options.StorageStatePath = settings.StorageStatePath;

        if (!string.IsNullOrWhiteSpace(videoDir))
        {
            options.RecordVideoDir = videoDir;
            // Left unset, Playwright shrinks the frame to fit 800x800 - a 1440x900 viewport
            // lands at 800x500 and the text goes soft. Ask for the size we actually want.
            options.RecordVideoSize = VideoSize(settings, options.ViewportSize!);
        }

        var context = await browser.NewContextAsync(options);
        context.SetDefaultTimeout(settings.DefaultTimeoutMs);
        context.SetDefaultNavigationTimeout(Math.Max(settings.DefaultTimeoutMs, 30_000));
        return context;
    }

    /// <summary>Frame size for the run video, or null to leave Playwright's default alone.</summary>
    private static RecordVideoSize? VideoSize(StudioSettings settings, ViewportSize viewport) =>
        settings.VideoResolution switch
        {
            VideoResolution.PlaywrightDefault => null,
            VideoResolution.Hd720 => new RecordVideoSize { Width = 1280, Height = 720 },
            VideoResolution.Hd1080 => new RecordVideoSize { Width = 1920, Height = 1080 },
            _ => new RecordVideoSize { Width = viewport.Width, Height = viewport.Height }
        };

    /// <summary>True when a browser binary for the configured engine can actually start.</summary>
    public async Task<(bool ok, string message)> ProbeAsync(StudioSettings settings)
    {
        try
        {
            await using var browser = await LaunchAsync(settings, headless: true, slowMoMs: 0);
            var version = browser.Version;
            return (true, $"{settings.Browser} {version} is ready.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Runs `playwright install` for the given engine and returns the console output.</summary>
    public async Task<(bool ok, string output)> InstallBrowsersAsync(string? browser = null)
    {
        var args = new List<string> { "install" };
        var engine = (browser ?? "").ToLowerInvariant() switch
        {
            "firefox" => "firefox",
            "webkit" => "webkit",
            "msedge" => "msedge",
            "chrome" => "chrome",
            "chromium" => "chromium",
            _ => null
        };
        if (engine is not null) args.Add(engine);

        var script = Path.Combine(AppContext.BaseDirectory, "playwright.ps1");
        if (File.Exists(script))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                using var proc = Process.Start(psi)!;
                var sb = new StringBuilder();
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                sb.Append(await stdout).Append(await stderr);
                return (proc.ExitCode == 0, sb.ToString().Trim());
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "playwright.ps1 failed, falling back to the in-process installer");
            }
        }

        // Fallback: the installer that ships inside the Microsoft.Playwright assembly.
        var exit = await Task.Run(() => Microsoft.Playwright.Program.Main(args.ToArray()));
        return (exit == 0, exit == 0
            ? "Browsers installed."
            : $"playwright install exited with code {exit}. Run it manually from the build output folder.");
    }

    public async ValueTask DisposeAsync()
    {
        _playwright?.Dispose();
        _playwright = null;
        await Task.CompletedTask;
    }
}
