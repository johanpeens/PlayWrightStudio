using System.Collections.Concurrent;
using System.Text.Json;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

public class RunStore
{
    private readonly StudioPaths _paths;
    private readonly ILogger<RunStore> _log;

    public RunStore(StudioPaths paths, ILogger<RunStore> log)
    {
        _paths = paths;
        _log = log;
    }

    public event Action? Changed;

    /// <summary>
    /// Runs being written to right now. Clearing history while one is in flight would pull the
    /// folder out from under it mid-screenshot.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _active = new();

    public void MarkActive(string runId) => _active[runId] = 1;
    public void MarkFinished(string runId) => _active.TryRemove(runId, out _);
    public bool IsActive(string runId) => _active.ContainsKey(runId);

    private string FileFor(string runId) => Path.Combine(_paths.RunFolder(runId), "run.json");

    public void Save(RunResult run)
    {
        File.WriteAllText(FileFor(run.Id), JsonSerializer.Serialize(run, ScenarioStore.Json));
        Changed?.Invoke();
    }

    public RunResult? Get(string runId)
    {
        var file = Path.Combine(_paths.Runs, runId, "run.json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<RunResult>(File.ReadAllText(file), ScenarioStore.Json); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read run {Id}", runId); return null; }
    }

    public IReadOnlyList<RunResult> Recent(int take = 60, string? scenarioId = null)
    {
        var runs = new List<RunResult>();
        foreach (var dir in Directory.EnumerateDirectories(_paths.Runs).OrderByDescending(d => d))
        {
            var run = Get(Path.GetFileName(dir));
            if (run is null) continue;
            if (scenarioId is not null && run.ScenarioId != scenarioId) continue;
            runs.Add(run);
            if (runs.Count >= take) break;
        }
        return runs;
    }

    public void Delete(string runId)
    {
        var dir = Path.Combine(_paths.Runs, runId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Changed?.Invoke();
    }

    public void DeleteAll()
    {
        foreach (var dir in Directory.EnumerateDirectories(_paths.Runs))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not delete run folder {Dir}", dir); }
        }
        Changed?.Invoke();
    }
}
