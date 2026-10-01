using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// What the user currently has open, so the app bar can offer to record or run in a connected
/// page without knowing anything about the editor.
///
/// Scoped, so it is per user. The editor sets it on load and clears it on the way out; the
/// connected-pages popover reads it. Nothing else should write to it.
/// </summary>
public sealed class ActiveScenario
{
    public Scenario? Scenario { get; private set; }

    /// <summary>What to do when the user picks a page to record in. Supplied by the editor.</summary>
    public Func<PageSession, Task>? Record { get; private set; }

    /// <summary>What to do when the user picks a page to replay in.</summary>
    public Func<PageSession, Task>? Run { get; private set; }

    public bool HasScenario => Scenario is not null && Record is not null;

    public event Action? Changed;

    public void Set(Scenario scenario, Func<PageSession, Task> record, Func<PageSession, Task> run)
    {
        Scenario = scenario;
        Record = record;
        Run = run;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Scenario = null;
        Record = null;
        Run = null;
        Changed?.Invoke();
    }
}
