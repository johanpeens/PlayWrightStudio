using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// First launch has nothing to look at, so drop in one worked example pointed at the
/// demo form the studio serves itself.
/// </summary>
public static class DemoSeeder
{
    public const string DemoName = "Demo signup - happy path";

    public static void SeedIfEmpty(IServiceProvider services)
    {
        var store = services.GetRequiredService<ScenarioStore>();
        if (store.All().Count > 0) return;

        var baseUrl = ResolveBaseUrl(services);
        store.Save(Build($"{baseUrl}/demo-form.html"));
    }

    private static string ResolveBaseUrl(IServiceProvider services)
    {
        var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        // Prefer http so the recording browser never trips over the dev certificate.
        var url = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                  ?? addresses?.FirstOrDefault()
                  ?? "http://localhost:5050";
        return url.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost").TrimEnd('/');
    }

    private static Scenario Build(string formUrl) => new()
    {
        Name = DemoName,
        Description = "A worked example against the demo form this app serves. Open it, hit Run, "
                    + "then try Record more steps to see how capturing works.",
        StartUrl = formUrl,
        Tags = { "demo" },
        Variables =
        {
            new ScenarioVariable { Name = "password", Value = "hunter2hunter2", IsSecret = true },
            new ScenarioVariable { Name = "plan", Value = "pro", Note = "free | pro | team" }
        },
        Steps =
        {
            new TestStep { Action = StepAction.Fill, Selector = "#firstName", Value = "Jane", Description = "Fill \"First name\"" },
            new TestStep { Action = StepAction.Fill, Selector = "#lastName", Value = "Doe", Description = "Fill \"Last name\"" },
            new TestStep
            {
                Action = StepAction.Fill, Selector = "#email", Value = "jane+{{random:4}}@example.com",
                Description = "Fill \"Email address\" with a fresh address each run"
            },
            new TestStep { Action = StepAction.Fill, Selector = "#password", Value = "{{password}}", Description = "Fill \"Password\"" },
            new TestStep
            {
                Action = StepAction.Select, Selector = "select[name=\"country\"]", Value = "za",
                Description = "Select in \"Country\" → South Africa"
            },
            new TestStep
            {
                Action = StepAction.Check, Selector = "input[name=\"plan\"][value=\"{{plan}}\"]",
                Description = "Pick the {{plan}} plan"
            },
            new TestStep
            {
                Action = StepAction.Check, Selector = "[data-testid=\"accept-terms\"]",
                Description = "Check \"I accept the terms\""
            },
            new TestStep
            {
                Action = StepAction.Click, Selector = "[data-testid=\"submit-signup\"]",
                Description = "Click \"Create account\""
            },
            new TestStep
            {
                Action = StepAction.AssertVisible, Selector = "[data-testid=\"signup-success\"]",
                Description = "Expect the success banner"
            },
            new TestStep
            {
                Action = StepAction.AssertText, Selector = "#successDetail", Value = "{{plan}} plan",
                Description = "Expect the confirmed plan to be echoed back"
            },
            new TestStep
            {
                Action = StepAction.Screenshot, Description = "Capture the finished state"
            }
        }
    };
}
