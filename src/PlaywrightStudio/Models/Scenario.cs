namespace PlaywrightStudio.Models;

public class ScenarioVariable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>Captured from a password field - the UI masks it, the scenario file does not.</summary>
    public bool IsSecret { get; set; }
}

/// <summary>A named click-through: where to start, and what to do.</summary>
public class Scenario
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "New scenario";
    public string? Description { get; set; }
    public string StartUrl { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public List<ScenarioVariable> Variables { get; set; } = new();
    public List<TestStep> Steps { get; set; } = new();

    /// <summary>
    /// Save each distinct page this journey visits - full-page screenshot plus its html - into
    /// a folder, so the site can be rebuilt from it later.
    /// </summary>
    public bool CaptureForDuplication { get; set; }

    /// <summary>Where those captures go. Blank means the studio's default Captures folder.</summary>
    public string? CaptureFolder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public int EnabledStepCount => Steps.Count(s => s.Enabled);

    public Scenario Clone()
    {
        return new Scenario
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = Name + " (copy)",
            Description = Description,
            StartUrl = StartUrl,
            Tags = new List<string>(Tags),
            Variables = Variables.Select(v => new ScenarioVariable { Name = v.Name, Value = v.Value, Note = v.Note, IsSecret = v.IsSecret }).ToList(),
            Steps = Steps.Select(s => s.Clone()).ToList(),
            CaptureForDuplication = CaptureForDuplication,
            CaptureFolder = CaptureFolder,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
    }
}
