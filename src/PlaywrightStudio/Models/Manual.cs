namespace PlaywrightStudio.Models;

/// <summary>One numbered instruction in a manual, with the picture that goes with it.</summary>
public class ManualStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>What the reader is told to do, in markdown.</summary>
    public string Text { get; set; } = "";

    /// <summary>File name inside the manual's own folder. Copied there, so deleting the run is safe.</summary>
    public string? ImageFile { get; set; }

    /// <summary>Where this came from, for the "refresh from the latest run" button.</summary>
    public string? SourceStepId { get; set; }

    public bool Include { get; set; } = true;

    public ManualStep Clone() => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Text = Text,
        ImageFile = ImageFile,
        SourceStepId = SourceStepId,
        Include = Include
    };
}

/// <summary>
/// A written document in its own right. Created from a run, then edited by hand - the whole
/// point is that the words and pictures stop being tied to the test once you start writing.
/// </summary>
public class Manual
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = "Untitled manual";

    /// <summary>Markdown shown under the title on the cover.</summary>
    public string? Intro { get; set; }

    /// <summary>Where it came from, so it can be refreshed from a later run.</summary>
    public string? ScenarioId { get; set; }
    public string? RunId { get; set; }

    public List<ManualStep> Steps { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public int IncludedCount => Steps.Count(s => s.Include);
    public int WithPictures => Steps.Count(s => s.Include && !string.IsNullOrWhiteSpace(s.ImageFile));
}
