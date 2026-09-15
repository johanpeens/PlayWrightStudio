using System.Text.Json;
using System.Text.Json.Serialization;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>One json file per scenario, so they can be committed next to the app under test.</summary>
public class ScenarioStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly StudioPaths _paths;
    private readonly ILogger<ScenarioStore> _log;

    public ScenarioStore(StudioPaths paths, ILogger<ScenarioStore> log)
    {
        _paths = paths;
        _log = log;
    }

    public event Action? Changed;

    private string FileFor(string id) => Path.Combine(_paths.Scenarios, id + ".json");

    public IReadOnlyList<Scenario> All()
    {
        var list = new List<Scenario>();
        foreach (var file in Directory.EnumerateFiles(_paths.Scenarios, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(file), Json);
                if (s is not null) list.Add(s);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping unreadable scenario {File}", file);
            }
        }
        return list.OrderByDescending(s => s.UpdatedUtc).ToList();
    }

    public Scenario? Get(string id)
    {
        var file = FileFor(id);
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<Scenario>(File.ReadAllText(file), Json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read scenario {Id}", id);
            return null;
        }
    }

    public void Save(Scenario scenario)
    {
        scenario.UpdatedUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(FileFor(scenario.Id), JsonSerializer.Serialize(scenario, Json));
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        var file = FileFor(id);
        if (File.Exists(file)) File.Delete(file);
        Changed?.Invoke();
    }

    public Scenario Import(string json)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(json, Json)
                       ?? throw new InvalidOperationException("That json is not a scenario.");
        if (string.IsNullOrWhiteSpace(scenario.Id) || File.Exists(FileFor(scenario.Id)))
            scenario.Id = Guid.NewGuid().ToString("n");
        foreach (var step in scenario.Steps)
            if (string.IsNullOrWhiteSpace(step.Id)) step.Id = Guid.NewGuid().ToString("n");
        Save(scenario);
        return scenario;
    }

    public string Export(Scenario scenario) => JsonSerializer.Serialize(scenario, Json);
}
