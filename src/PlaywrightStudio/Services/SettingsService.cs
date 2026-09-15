using System.Text.Json;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

public class SettingsService
{
    // Enum names rather than bare integers, so settings.json stays readable by hand.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly StudioPaths _paths;
    private readonly ILogger<SettingsService> _log;

    public SettingsService(StudioPaths paths, ILogger<SettingsService> log)
    {
        _paths = paths;
        _log = log;
        Current = Load();
    }

    public StudioSettings Current { get; private set; }

    public event Action? Changed;

    private StudioSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(_paths.SettingsFile));
                if (s is not null) return s;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read settings.json, falling back to defaults");
        }
        return new StudioSettings();
    }

    public void Save(StudioSettings settings)
    {
        Current = settings;
        File.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(settings, Json));
        Changed?.Invoke();
    }

    public void Save() => Save(Current);
}
