using System.Text.Json;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// One folder per manual: its json, and the images it uses. Copying pictures in means a manual
/// keeps working after the run that produced it is cleared.
/// </summary>
public class ManualStore
{
    private readonly StudioPaths _paths;
    private readonly ILogger<ManualStore> _log;

    public ManualStore(StudioPaths paths, ILogger<ManualStore> log)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.Manuals);
    }

    public event Action? Changed;

    public string FolderFor(string manualId)
    {
        var dir = Path.Combine(_paths.Manuals, manualId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string FileFor(string manualId) => Path.Combine(FolderFor(manualId), "manual.json");

    public IReadOnlyList<Manual> All()
    {
        var list = new List<Manual>();
        foreach (var dir in Directory.EnumerateDirectories(_paths.Manuals))
        {
            var manual = Get(Path.GetFileName(dir));
            if (manual is not null) list.Add(manual);
        }
        return list.OrderByDescending(m => m.UpdatedUtc).ToList();
    }

    public Manual? Get(string manualId)
    {
        var file = Path.Combine(_paths.Manuals, manualId, "manual.json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<Manual>(File.ReadAllText(file), ScenarioStore.Json); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read manual {Id}", manualId); return null; }
    }

    public void Save(Manual manual)
    {
        manual.UpdatedUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(FileFor(manual.Id), JsonSerializer.Serialize(manual, ScenarioStore.Json));
        Changed?.Invoke();
    }

    public void Delete(string manualId)
    {
        var dir = Path.Combine(_paths.Manuals, manualId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Changed?.Invoke();
    }

    public Manual Duplicate(Manual manual)
    {
        var copy = new Manual
        {
            Title = manual.Title + " (copy)",
            Intro = manual.Intro,
            ScenarioId = manual.ScenarioId,
            RunId = manual.RunId,
            Steps = manual.Steps.Select(s => s.Clone()).ToList()
        };

        // Images belong to the folder, so they have to be copied too.
        var from = FolderFor(manual.Id);
        var to = FolderFor(copy.Id);
        foreach (var step in copy.Steps.Where(s => !string.IsNullOrWhiteSpace(s.ImageFile)))
        {
            var source = Path.Combine(from, step.ImageFile!);
            if (File.Exists(source)) File.Copy(source, Path.Combine(to, step.ImageFile!), overwrite: true);
            else step.ImageFile = null;
        }

        Save(copy);
        return copy;
    }

    /// <summary>Picture formats a browser will render and the pdf can embed.</summary>
    private static readonly string[] Allowed = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>
    /// Copies a picture into the manual's folder and returns the name to store on the step.
    /// Async the whole way down: a file picked in the browser is streamed over the circuit, and
    /// that stream refuses synchronous reads - CopyTo throws where CopyToAsync works.
    /// </summary>
    public async Task<string> AddImageAsync(string manualId, Stream content, string suggestedName,
                                            CancellationToken ct = default)
    {
        var extension = Path.GetExtension(suggestedName).ToLowerInvariant();
        if (!Allowed.Contains(extension)) extension = ".png";

        var name = $"{Guid.NewGuid():n}{extension}";
        var path = Path.Combine(FolderFor(manualId), name);

        try
        {
            await using (var file = File.Create(path))
                await content.CopyToAsync(file, ct);

            // An empty file would sit in the manual as a broken picture, so say so instead.
            if (new FileInfo(path).Length == 0)
                throw new InvalidOperationException("that file is empty");
        }
        catch
        {
            // Never leave a half-written picture behind that no step will ever point at.
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }

        return name;
    }

    public string AddImage(string manualId, string existingFile)
    {
        var name = $"{Guid.NewGuid():n}{Path.GetExtension(existingFile)}";
        File.Copy(existingFile, Path.Combine(FolderFor(manualId), name), overwrite: true);
        return name;
    }

    /// <summary>Deletes an image no step refers to any more.</summary>
    public void ForgetImage(string manualId, string? imageFile)
    {
        if (string.IsNullOrWhiteSpace(imageFile)) return;
        try
        {
            var path = Path.Combine(_paths.Manuals, manualId, imageFile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not delete a manual image"); }
    }
}
