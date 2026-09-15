namespace PlaywrightStudio.Services;

/// <summary>
/// Lets the folder picker walk this machine's drives. The studio is a localhost tool, so the
/// "server" filesystem is the user's own - but every call is read-only and swallows the
/// permission errors you hit walking a Windows disk.
/// </summary>
public sealed class FileSystemBrowser
{
    public record Entry(string Name, string Path, bool IsDrive);

    /// <summary>Drives, plus the handful of places anyone actually starts from.</summary>
    public IReadOnlyList<Entry> Shortcuts()
    {
        var list = new List<Entry>();

        foreach (var special in new[]
        {
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.UserProfile
        })
        {
            try
            {
                var path = Environment.GetFolderPath(special);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    list.Add(new Entry(Path.GetFileName(path.TrimEnd('\\', '/')) ?? path, path, false));
            }
            catch { /* not every special folder exists */ }
        }

        foreach (var drive in Drives()) list.Add(drive);
        return list;
    }

    public IReadOnlyList<Entry> Drives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new Entry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, true))
                .ToList();
        }
        catch
        {
            return Array.Empty<Entry>();
        }
    }

    /// <summary>Sub-folders of a path, hidden and inaccessible ones left out.</summary>
    public IReadOnlyList<Entry> Children(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new Entry(d.Name, d.FullName, false))
                .ToList();
        }
        catch (Exception)
        {
            // Access denied, a disconnected network drive, a path that vanished - all the same
            // to the picker: nothing to show.
            return Array.Empty<Entry>();
        }
    }

    public string? Parent(string path)
    {
        try { return Directory.GetParent(path)?.FullName; }
        catch { return null; }
    }

    public bool Exists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>Creates a sub-folder, returning the error text when it cannot.</summary>
    public (bool ok, string? error, string path) CreateFolder(string parent, string name)
    {
        try
        {
            var cleaned = name.Trim();
            if (cleaned.Length == 0) return (false, "Give the folder a name.", parent);
            if (cleaned.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return (false, "That name has characters a folder cannot use.", parent);

            var full = Path.Combine(parent, cleaned);
            Directory.CreateDirectory(full);
            return (true, null, full);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, parent);
        }
    }
}
