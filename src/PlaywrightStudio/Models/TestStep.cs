namespace PlaywrightStudio.Models;

/// <summary>A single action in a scenario.</summary>
public class TestStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public StepAction Action { get; set; } = StepAction.Click;

    /// <summary>Playwright selector, e.g. <c>#email</c>, <c>[data-testid="save"]</c>, <c>role=button[name="Save"]</c>.</summary>
    public string? Selector { get; set; }

    /// <summary>Other selectors the recorder found for the same element, best first.</summary>
    public List<string> SelectorAlternatives { get; set; } = new();

    /// <summary>Text to fill, key to press, option to select, URL to open, expected value...</summary>
    public string? Value { get; set; }

    /// <summary>Human label shown in the step list. Auto-filled by the recorder from labels / accessible names.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// What a reader should be told to do at this point, in markdown. Written by you, not the
    /// recorder, and it is what ends up in the printed manual.
    /// </summary>
    public string? Instruction { get; set; }

    /// <summary>Leave this step out of the manual - a wait, a scroll, plumbing nobody needs to read.</summary>
    public bool HideFromManual { get; set; }

    /// <summary>Selector chain for the iframe the element lives in (one entry per nesting level).</summary>
    public List<string> FrameChain { get; set; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>Per-step override of the default timeout.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Wait this long before running the step.</summary>
    public int DelayBeforeMs { get; set; }

    /// <summary>Keep going when this step fails instead of aborting the run.</summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>Take a screenshot after this step even when the run-wide setting is off.</summary>
    public bool CaptureScreenshot { get; set; }

    public TestStep Clone() => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Action = Action,
        Selector = Selector,
        SelectorAlternatives = new List<string>(SelectorAlternatives),
        Value = Value,
        Description = Description,
        Instruction = Instruction,
        HideFromManual = HideFromManual,
        FrameChain = new List<string>(FrameChain),
        Enabled = Enabled,
        TimeoutMs = TimeoutMs,
        DelayBeforeMs = DelayBeforeMs,
        ContinueOnFailure = ContinueOnFailure,
        CaptureScreenshot = CaptureScreenshot
    };

    /// <summary>One-line summary used in the step list and in run output.</summary>
    public string Summarise()
    {
        if (!string.IsNullOrWhiteSpace(Description)) return Description!;
        var target = string.IsNullOrWhiteSpace(Selector) ? "" : $" {Selector}";
        var val = string.IsNullOrWhiteSpace(Value) ? "" : $" \u2192 \"{Value}\"";
        return $"{Action}{target}{val}".Trim();
    }
}
