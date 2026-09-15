using System.Text.Json.Serialization;

namespace PlaywrightStudio.Models;

/// <summary>Raw payload posted back from the recorder script running inside the target page.</summary>
public class RecordedEvent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("alternatives")] public List<string>? Alternatives { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("frames")] public List<string>? Frames { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("secret")] public bool Secret { get; set; }
    [JsonPropertyName("field")] public string? Field { get; set; }
}

public enum RecorderState { Stopped, Starting, Recording, Paused }
