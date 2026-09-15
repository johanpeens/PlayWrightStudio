using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace PlaywrightStudio.Services;

/// <summary>
/// Playwright only ever writes WebM, so run videos are transcoded to mp4 afterwards.
/// The ffmpeg Playwright bundles is a cut-down build with no mp4 muxer, so this needs a
/// real ffmpeg on the machine.
/// </summary>
public sealed class VideoConverter
{
    /// <summary>Video encoders we can write into an mp4, best first.</summary>
    private static readonly string[] Encoders = { "libx264", "h264_mf", "libopenh264", "h264_nvenc", "h264_qsv", "mpeg4" };

    private readonly SettingsService _settings;
    private readonly StudioPaths _paths;
    private readonly ILogger<VideoConverter> _log;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private string? _probedFor;
    private Capability? _capability;

    public VideoConverter(SettingsService settings, StudioPaths paths, ILogger<VideoConverter> log)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
    }

    /// <summary>Where a studio-managed ffmpeg lives once downloaded.</summary>
    public string ManagedFfmpeg => Path.Combine(_paths.Tools, "ffmpeg", "ffmpeg.exe");

    public bool HasManagedFfmpeg => File.Exists(ManagedFfmpeg);

    public record Capability(bool CanMakeMp4, string? FfmpegPath, string? Encoder, string Message);

    /// <summary>Where ffmpeg might be, in the order we'd rather use it.</summary>
    private IEnumerable<string> Candidates()
    {
        var configured = _settings.Current.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured!;

        if (HasManagedFfmpeg) yield return ManagedFfmpeg;   // the one the studio fetched

        yield return "ffmpeg";   // anything on PATH

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var files = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(files, "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
        yield return @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
    }

    /// <summary>Finds an ffmpeg that can actually write mp4, and remembers the answer.</summary>
    public async Task<Capability> ProbeAsync(bool force = false)
    {
        var key = _settings.Current.FfmpegPath ?? "";
        await _probeGate.WaitAsync();
        try
        {
            if (!force && _capability is not null && _probedFor == key) return _capability;

            foreach (var candidate in Candidates())
            {
                var (ok, output) = await RunAsync(candidate, new[] { "-hide_banner", "-encoders" }, TimeSpan.FromSeconds(20));
                if (!ok) continue;

                var encoder = Encoders.FirstOrDefault(e => output.Contains(" " + e + " ", StringComparison.Ordinal)
                                                        || output.Contains(e + " ", StringComparison.Ordinal));

                var (muxOk, muxers) = await RunAsync(candidate, new[] { "-hide_banner", "-muxers" }, TimeSpan.FromSeconds(20));
                var hasMp4 = muxOk && muxers.Contains(" mp4", StringComparison.Ordinal);

                if (encoder is null || !hasMp4)
                {
                    _log.LogDebug("{Path} cannot write mp4 (encoder={Encoder}, mp4Muxer={Mux})", candidate, encoder, hasMp4);
                    continue;
                }

                _capability = new Capability(true, candidate, encoder,
                    $"Videos will be saved as mp4 using {encoder} ({candidate}).");
                _probedFor = key;
                return _capability;
            }

            _capability = new Capability(false, null, null,
                "No ffmpeg that can write mp4 was found, so run videos stay as .webm. "
                + "Use Download ffmpeg below, or point this at one you already have.");
            _probedFor = key;
            return _capability;
        }
        finally { _probeGate.Release(); }
    }

    /// <summary>
    /// Transcodes a Playwright webm to mp4 beside it and deletes the webm. On failure the
    /// webm is left alone and the reason comes back in the message.
    /// </summary>
    public async Task<(string file, string? warning)> ToMp4Async(string webmPath, CancellationToken ct = default)
    {
        var webmName = Path.GetFileName(webmPath);
        var capability = await ProbeAsync();

        if (!capability.CanMakeMp4 || capability.FfmpegPath is null)
            return (webmName, capability.Message);

        var mp4Path = Path.ChangeExtension(webmPath, ".mp4");
        var args = new List<string>
        {
            "-y", "-loglevel", "error",
            "-i", webmPath,
            "-an",                               // Playwright videos carry no audio
            "-c:v", capability.Encoder!,
            "-pix_fmt", "yuv420p",               // so every player will take it
            "-movflags", "+faststart"
        };
        // crf is a quality target rather than a bitrate: lower is sharper and larger.
        var (crf, preset, bitrate) = _settings.Current.VideoQuality switch
        {
            Models.VideoQuality.Small => (30, "veryfast", "1M"),
            Models.VideoQuality.High => (18, "medium", "8M"),
            _ => (23, "veryfast", "3M")
        };

        if (capability.Encoder is "libx264" or "libopenh264")
        {
            args.Add("-preset"); args.Add(preset);
            args.Add("-crf"); args.Add(crf.ToString());
        }
        else
        {
            // Hardware and Media Foundation encoders ignore crf, so aim at a bitrate instead.
            args.Add("-b:v"); args.Add(bitrate);
        }
        args.Add(mp4Path);

        var (ok, output) = await RunAsync(capability.FfmpegPath, args, TimeSpan.FromMinutes(5), ct);

        if (!ok || !File.Exists(mp4Path) || new FileInfo(mp4Path).Length == 0)
        {
            _log.LogWarning("ffmpeg could not convert {File}: {Output}", webmName, Tail(output));
            TryDelete(mp4Path);
            return (webmName, "Video kept as .webm - ffmpeg failed: " + Tail(output));
        }

        TryDelete(webmPath);
        return (Path.GetFileName(mp4Path), null);
    }

    /// <summary>
    /// Fetches a static ffmpeg build into the studio's own tools folder - the same idea as
    /// `playwright install`, so nothing has to be installed machine-wide.
    /// </summary>
    public async Task<(bool ok, string message)> DownloadAsync(IProgress<string>? progress = null,
                                                              CancellationToken ct = default)
    {
        const string url = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
        var targetDir = Path.GetDirectoryName(ManagedFfmpeg)!;
        var zipPath = Path.Combine(_paths.Tools, "ffmpeg-download.zip");

        try
        {
            Directory.CreateDirectory(targetDir);
            progress?.Report("Downloading ffmpeg…");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(zipPath);

                var buffer = new byte[128 * 1024];
                long read = 0;
                var lastReport = -1;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total <= 0) continue;
                    var percent = (int)(read * 100 / total);
                    if (percent == lastReport || percent % 5 != 0) continue;
                    lastReport = percent;
                    progress?.Report($"Downloading ffmpeg… {percent}% of {total / 1024 / 1024} MB");
                }
            }

            progress?.Report("Extracting…");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) return (false, "That archive did not contain bin/ffmpeg.exe.");

                entry.ExtractToFile(ManagedFfmpeg, overwrite: true);
            }

            TryDelete(zipPath);

            var capability = await ProbeAsync(force: true);
            return capability.CanMakeMp4
                ? (true, $"ffmpeg is ready. {capability.Message}")
                : (false, "Downloaded, but it still cannot write mp4: " + capability.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not download ffmpeg");
            TryDelete(zipPath);
            return (false, "Download failed: " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* it can stay */ }
    }

    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .Where(l => l.Length > 0)
                          .ToArray();
        var text = lines.Length == 0 ? "no output" : string.Join(" ", lines.TakeLast(2));
        return text.Length > 220 ? text[..220] + "…" : text;
    }

    private async Task<(bool ok, string output)> RunAsync(string exe, IEnumerable<string> args,
                                                         TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "could not start " + exe);

            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (false, "ffmpeg timed out");
            }

            var text = new StringBuilder().Append(await stdout).Append(await stderr).ToString();
            return (proc.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            // Missing executable lands here, which is a normal outcome while probing.
            return (false, ex.Message);
        }
    }
}
