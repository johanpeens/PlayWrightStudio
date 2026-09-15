namespace PlaywrightStudio.Services;

/// <summary>
/// Where the studio keeps scenarios, runs and screenshots. Lives outside bin/ so a
/// rebuild never wipes your tests.
/// </summary>
public class StudioPaths
{
    public StudioPaths(IConfiguration config)
    {
        // appsettings wins, because someone who edited it meant it; then the folder chosen
        // in Settings; then the default.
        ConfiguredRoot = config["Studio:DataRoot"] ?? "";
        Root = !string.IsNullOrWhiteSpace(ConfiguredRoot) ? ConfiguredRoot
             : ReadOverride() ?? DefaultRoot;

        Directory.CreateDirectory(Scenarios);
        Directory.CreateDirectory(Runs);
        var videos = config["Studio:VideoFolder"];
        Videos = string.IsNullOrWhiteSpace(videos)
            ? Path.Combine(AppContext.BaseDirectory, "Videos")
            : videos!;

        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Tools);
        var captures = config["Studio:CaptureFolder"];
        Captures = string.IsNullOrWhiteSpace(captures)
            ? Path.Combine(AppContext.BaseDirectory, "Captures")
            : captures!;

        Directory.CreateDirectory(Videos);
        Directory.CreateDirectory(Captures);
        Directory.CreateDirectory(Manuals);
    }

    public string Root { get; }

    /// <summary>Set in appsettings.json, in which case the folder cannot be changed in the app.</summary>
    public string ConfiguredRoot { get; }
    public bool RootIsLocked => !string.IsNullOrWhiteSpace(ConfiguredRoot);

    public string Scenarios => Path.Combine(Root, "scenarios");
    public string Runs => Path.Combine(Root, "runs");
    public string Profiles => Path.Combine(Root, "profiles");

    /// <summary>Helper binaries the studio downloads for itself, ffmpeg among them.</summary>
    public string Tools => Path.Combine(Root, "tools");

    /// <summary>
    /// Run videos land next to the app rather than in the data folder, so they are easy to
    /// find. Note this sits under bin, so a rebuild clears them - set Studio:VideoFolder to
    /// keep them somewhere permanent.
    /// </summary>
    public string Videos { get; }

    /// <summary>Page captures for site duplication, one folder per scenario.</summary>
    public string Captures { get; }

    /// <summary>Written manuals, one folder each, holding the json and its pictures.</summary>
    public string Manuals => Path.Combine(Root, "manuals");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string RunFolder(string runId)
    {
        var dir = Path.Combine(Runs, runId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ------------------------------------------------------------------ choosing the folder

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlaywrightStudio");

    /// <summary>
    /// The chosen folder cannot be remembered inside itself - settings.json lives there - so a
    /// one-line pointer sits at a fixed spot instead.
    /// </summary>
    private static string PointerFile => Path.Combine(DefaultRoot, "dataroot.txt");

    private static string? ReadOverride()
    {
        try
        {
            if (!File.Exists(PointerFile)) return null;
            var path = File.ReadAllText(PointerFile).Trim();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;   // unreadable pointer just means "use the default"
        }
    }

    /// <summary>Remembers the folder for next start. Pass null to go back to the default.</summary>
    public static void SetOverride(string? folder)
    {
        Directory.CreateDirectory(DefaultRoot);
        if (string.IsNullOrWhiteSpace(folder) || PathsEqual(folder!, DefaultRoot))
        {
            if (File.Exists(PointerFile)) File.Delete(PointerFile);
            return;
        }
        File.WriteAllText(PointerFile, folder!.Trim());
    }

    public static bool PathsEqual(string a, string b)
    {
        static string Normal(string p) => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(p.Trim().Trim('"'))).Replace('/', Path.DirectorySeparatorChar);
        try { return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// Copies what the studio owns into a new folder, leaving the originals alone - so a wrong
    /// turn costs disk space rather than your scenarios.
    /// </summary>
    public int CopyDataTo(string destination)
    {
        var copied = 0;
        Directory.CreateDirectory(destination);

        foreach (var name in new[] { "scenarios", "runs", "manuals", "profiles" })
        {
            var from = Path.Combine(Root, name);
            if (Directory.Exists(from)) copied += CopyTree(from, Path.Combine(destination, name));
        }

        if (File.Exists(SettingsFile))
        {
            File.Copy(SettingsFile, Path.Combine(destination, "settings.json"), overwrite: true);
            copied++;
        }

        return copied;
    }

    private static int CopyTree(string from, string to)
    {
        var copied = 0;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
            copied++;
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            copied += CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        return copied;
    }
}
