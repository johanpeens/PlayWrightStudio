namespace PlaywrightStudio.Models;

/// <summary>Everything a recorded or hand-written step can do.</summary>
public enum StepAction
{
    Navigate,
    Click,
    DoubleClick,
    RightClick,
    Fill,
    Type,
    Press,
    Select,
    Check,
    Uncheck,
    Hover,
    Upload,
    Focus,
    ScrollIntoView,
    WaitForSelector,
    WaitForHidden,
    WaitForUrl,
    WaitForTimeout,
    AssertVisible,
    AssertHidden,
    AssertText,
    AssertValue,
    AssertChecked,
    AssertUrl,
    AssertTitle,
    AssertCount,
    Screenshot,
    Comment
}

public static class StepActionInfo
{
    /// <summary>Steps that need a selector to make sense.</summary>
    public static bool NeedsSelector(this StepAction a) => a switch
    {
        StepAction.Navigate or StepAction.WaitForUrl or StepAction.WaitForTimeout
            or StepAction.AssertUrl or StepAction.AssertTitle
            or StepAction.Screenshot or StepAction.Comment => false,
        _ => true
    };

    /// <summary>Steps that need a value to make sense.</summary>
    public static bool NeedsValue(this StepAction a) => a switch
    {
        StepAction.Navigate or StepAction.Fill or StepAction.Type or StepAction.Press
            or StepAction.Select or StepAction.Upload or StepAction.WaitForUrl
            or StepAction.WaitForTimeout or StepAction.AssertText or StepAction.AssertValue
            or StepAction.AssertChecked or StepAction.AssertUrl or StepAction.AssertTitle
            or StepAction.AssertCount or StepAction.Comment => true,
        _ => false
    };

    public static bool IsAssertion(this StepAction a) => a.ToString().StartsWith("Assert", StringComparison.Ordinal);

    public static string Icon(this StepAction a) => a switch
    {
        StepAction.Navigate => "\U0001F310",
        StepAction.Click or StepAction.DoubleClick or StepAction.RightClick => "\U0001F5B1",
        StepAction.Fill or StepAction.Type => "\u2328",
        StepAction.Press => "\u23CE",
        StepAction.Select => "\u25BC",
        StepAction.Check or StepAction.Uncheck => "\u2611",
        StepAction.Hover => "\u261D",
        StepAction.Upload => "\U0001F4CE",
        StepAction.Screenshot => "\U0001F4F7",
        StepAction.Comment => "\U0001F4AC",
        _ when a.IsAssertion() => "\u2714",
        _ => "\u23F1"
    };
}
