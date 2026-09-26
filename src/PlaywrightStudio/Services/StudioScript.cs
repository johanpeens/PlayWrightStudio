using System.Text;

namespace PlaywrightStudio.Services;

/// <summary>
/// Serves the one file a page under test loads. It is the recorder, the player and the transport
/// concatenated, so the app being tested needs exactly one script tag and no build step.
///
/// Cached in memory after the first read, except in development where editing the js and hitting
/// refresh should be enough.
/// </summary>
public sealed class StudioScript
{
    private static readonly string[] Parts =
    {
        "transport.js",   // must be first - it defines the channel the others use
        "recorder.js",
        "player.js",
        "capture.js",
        "runbar.js"
    };

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<StudioScript> _log;
    private string? _cached;

    public StudioScript(IWebHostEnvironment env, ILogger<StudioScript> log)
    {
        _env = env;
        _log = log;
    }

    public async Task<string> BuildAsync()
    {
        if (_cached is not null && !_env.IsDevelopment()) return _cached;

        var sb = new StringBuilder();
        sb.AppendLine("/* Playwright Studio - recorder and player. Served from the studio itself. */");
        sb.AppendLine("/* Nothing here is fetched from a CDN. */");

        foreach (var part in Parts)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", part);
            if (!File.Exists(path))
            {
                _log.LogWarning("Script part missing: {Part}", part);
                continue;
            }

            sb.AppendLine().AppendLine($"/* ---- {part} ---- */");
            sb.AppendLine(await File.ReadAllTextAsync(path));
        }

        _cached = sb.ToString();
        return _cached;
    }
}
