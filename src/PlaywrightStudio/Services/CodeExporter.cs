using System.Text;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

public enum ExportFormat { NUnitCSharp, TypeScript, PythonPytest }

/// <summary>Turns a scenario into a test file you can drop into a real test project.</summary>
public class CodeExporter
{
    private readonly SettingsService _settings;

    public CodeExporter(SettingsService settings) => _settings = settings;

    public string Export(Scenario scenario, ExportFormat format) => format switch
    {
        ExportFormat.TypeScript => TypeScript(scenario),
        ExportFormat.PythonPytest => Python(scenario),
        _ => CSharp(scenario)
    };

    public static string FileName(Scenario scenario, ExportFormat format) => format switch
    {
        ExportFormat.TypeScript => Slug(scenario.Name) + ".spec.ts",
        ExportFormat.PythonPytest => "test_" + Snake(scenario.Name) + ".py",
        _ => Pascal(scenario.Name) + "Tests.cs"
    };

    // ------------------------------------------------------------------ C# / NUnit

    private string CSharp(Scenario s)
    {
        const ExportLang lang = ExportLang.CSharp;
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Text.RegularExpressions;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Playwright;");
        sb.AppendLine("using Microsoft.Playwright.NUnit;");
        sb.AppendLine("using NUnit.Framework;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_settings.Current.ExportNamespace};");
        sb.AppendLine();
        sb.AppendLine("[Parallelizable(ParallelScope.Self)]");
        sb.AppendLine("[TestFixture]");
        sb.AppendLine($"public class {Pascal(s.Name)}Tests : PageTest");
        sb.AppendLine("{");

        foreach (var v in s.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
            sb.AppendLine($"    private const string {Pascal(v.Name)} = {Q(v.Value)};");
        if (s.Variables.Count > 0) sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(s.Description))
            sb.AppendLine($"    /// <summary>{s.Description.Replace("\r", "").Replace("\n", " ")}</summary>");
        sb.AppendLine("    [Test]");
        sb.AppendLine($"    public async Task {Pascal(s.Name)}()");
        sb.AppendLine("    {");

        if (!string.IsNullOrWhiteSpace(s.StartUrl) &&
            s.Steps.FirstOrDefault(x => x.Enabled)?.Action != StepAction.Navigate)
            sb.AppendLine($"        await Page.GotoAsync({ExportTemplate.Expression(s.StartUrl, s, lang)});");

        foreach (var step in s.Steps.Where(x => x.Enabled))
        {
            if (!string.IsNullOrWhiteSpace(step.Description) && step.Action != StepAction.Comment)
                sb.AppendLine($"        // {Flatten(step.Description)}");
            sb.AppendLine(CSharpStep(step, s));
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string CSharpStep(TestStep step, Scenario s)
    {
        const ExportLang lang = ExportLang.CSharp;
        const string pad = "        ";

        var target = "Page";
        foreach (var frame in step.FrameChain)
            target += $".FrameLocator({ExportTemplate.Expression(frame, s, lang)})";
        var all = $"{target}.Locator({ExportTemplate.Expression(step.Selector ?? "", s, lang)})";
        var loc = all + ".First";
        var val = ExportTemplate.Expression(step.Value, s, lang);

        return step.Action switch
        {
            StepAction.Comment => $"{pad}// {Flatten(step.Value)}",
            StepAction.Navigate => $"{pad}await Page.GotoAsync({val});",
            StepAction.Click => $"{pad}await {loc}.ClickAsync();",
            StepAction.DoubleClick => $"{pad}await {loc}.DblClickAsync();",
            StepAction.RightClick => $"{pad}await {loc}.ClickAsync(new() {{ Button = MouseButton.Right }});",
            StepAction.Fill => $"{pad}await {loc}.FillAsync({val});",
            StepAction.Type => $"{pad}await {loc}.PressSequentiallyAsync({val});",
            StepAction.Press => $"{pad}await {loc}.PressAsync({val});",
            StepAction.Select => $"{pad}await {loc}.SelectOptionAsync(new[] {{ {ExportTemplate.Join(ExportTemplate.List(step.Value, ',', s, lang))} }});",
            StepAction.Check => $"{pad}await {loc}.CheckAsync();",
            StepAction.Uncheck => $"{pad}await {loc}.UncheckAsync();",
            StepAction.Hover => $"{pad}await {loc}.HoverAsync();",
            StepAction.Focus => $"{pad}await {loc}.FocusAsync();",
            StepAction.ScrollIntoView => $"{pad}await {loc}.ScrollIntoViewIfNeededAsync();",
            StepAction.Upload => $"{pad}await {loc}.SetInputFilesAsync(new[] {{ {ExportTemplate.Join(ExportTemplate.List(step.Value, ';', s, lang))} }});",
            StepAction.WaitForSelector => $"{pad}await {loc}.WaitForAsync(new() {{ State = WaitForSelectorState.Visible }});",
            StepAction.WaitForHidden => $"{pad}await {loc}.WaitForAsync(new() {{ State = WaitForSelectorState.Hidden }});",
            StepAction.WaitForUrl => $"{pad}await Page.WaitForURLAsync({val});",
            StepAction.WaitForTimeout => $"{pad}await Page.WaitForTimeoutAsync({Ms(step.Value)});",
            StepAction.AssertVisible => $"{pad}await Expect({loc}).ToBeVisibleAsync();",
            StepAction.AssertHidden => $"{pad}await Expect({loc}).ToBeHiddenAsync();",
            StepAction.AssertText => $"{pad}await Expect({loc}).ToContainTextAsync({val});",
            StepAction.AssertValue => $"{pad}await Expect({loc}).ToHaveValueAsync({val});",
            StepAction.AssertChecked => IsFalse(step.Value)
                                            ? $"{pad}await Expect({loc}).Not.ToBeCheckedAsync();"
                                            : $"{pad}await Expect({loc}).ToBeCheckedAsync();",
            StepAction.AssertCount => $"{pad}await Expect({all}).ToHaveCountAsync({Count(step.Value)});",
            StepAction.AssertUrl => $"{pad}await Expect(Page).ToHaveURLAsync(new Regex({Q(RegexOf(step.Value))}));",
            StepAction.AssertTitle => $"{pad}await Expect(Page).ToHaveTitleAsync(new Regex({Q(RegexOf(step.Value))}));",
            StepAction.Screenshot => $"{pad}await Page.ScreenshotAsync(new() {{ Path = {Q(Slug(step.Description ?? "screenshot") + ".png")} }});",
            _ => $"{pad}// TODO: {step.Action}"
        };
    }

    // ------------------------------------------------------------------ TypeScript

    private static string TypeScript(Scenario s)
    {
        const ExportLang lang = ExportLang.TypeScript;
        var sb = new StringBuilder();
        sb.AppendLine("import { test, expect } from '@playwright/test';");
        sb.AppendLine();
        foreach (var v in s.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
            sb.AppendLine($"const {Camel(v.Name)} = {Qs(v.Value)};");
        if (s.Variables.Count > 0) sb.AppendLine();

        sb.AppendLine($"test({Qs(s.Name)}, async ({{ page }}) => {{");
        if (!string.IsNullOrWhiteSpace(s.StartUrl) &&
            s.Steps.FirstOrDefault(x => x.Enabled)?.Action != StepAction.Navigate)
            sb.AppendLine($"  await page.goto({ExportTemplate.Expression(s.StartUrl, s, lang)});");

        foreach (var step in s.Steps.Where(x => x.Enabled))
        {
            if (!string.IsNullOrWhiteSpace(step.Description) && step.Action != StepAction.Comment)
                sb.AppendLine($"  // {Flatten(step.Description)}");
            sb.AppendLine(TsStep(step, s));
        }
        sb.AppendLine("});");
        return sb.ToString();
    }

    private static string TsStep(TestStep step, Scenario s)
    {
        const ExportLang lang = ExportLang.TypeScript;
        const string pad = "  ";

        var target = "page";
        foreach (var frame in step.FrameChain)
            target += $".frameLocator({ExportTemplate.Expression(frame, s, lang)})";
        var all = $"{target}.locator({ExportTemplate.Expression(step.Selector ?? "", s, lang)})";
        var loc = all + ".first()";
        var val = ExportTemplate.Expression(step.Value, s, lang);

        return step.Action switch
        {
            StepAction.Comment => $"{pad}// {Flatten(step.Value)}",
            StepAction.Navigate => $"{pad}await page.goto({val});",
            StepAction.Click => $"{pad}await {loc}.click();",
            StepAction.DoubleClick => $"{pad}await {loc}.dblclick();",
            StepAction.RightClick => $"{pad}await {loc}.click({{ button: 'right' }});",
            StepAction.Fill => $"{pad}await {loc}.fill({val});",
            StepAction.Type => $"{pad}await {loc}.pressSequentially({val});",
            StepAction.Press => $"{pad}await {loc}.press({val});",
            StepAction.Select => $"{pad}await {loc}.selectOption([{ExportTemplate.Join(ExportTemplate.List(step.Value, ',', s, lang))}]);",
            StepAction.Check => $"{pad}await {loc}.check();",
            StepAction.Uncheck => $"{pad}await {loc}.uncheck();",
            StepAction.Hover => $"{pad}await {loc}.hover();",
            StepAction.Focus => $"{pad}await {loc}.focus();",
            StepAction.ScrollIntoView => $"{pad}await {loc}.scrollIntoViewIfNeeded();",
            StepAction.Upload => $"{pad}await {loc}.setInputFiles([{ExportTemplate.Join(ExportTemplate.List(step.Value, ';', s, lang))}]);",
            StepAction.WaitForSelector => $"{pad}await {loc}.waitFor({{ state: 'visible' }});",
            StepAction.WaitForHidden => $"{pad}await {loc}.waitFor({{ state: 'hidden' }});",
            StepAction.WaitForUrl => $"{pad}await page.waitForURL({val});",
            StepAction.WaitForTimeout => $"{pad}await page.waitForTimeout({Ms(step.Value)});",
            StepAction.AssertVisible => $"{pad}await expect({loc}).toBeVisible();",
            StepAction.AssertHidden => $"{pad}await expect({loc}).toBeHidden();",
            StepAction.AssertText => $"{pad}await expect({loc}).toContainText({val});",
            StepAction.AssertValue => $"{pad}await expect({loc}).toHaveValue({val});",
            StepAction.AssertChecked => IsFalse(step.Value)
                                            ? $"{pad}await expect({loc}).not.toBeChecked();"
                                            : $"{pad}await expect({loc}).toBeChecked();",
            StepAction.AssertCount => $"{pad}await expect({all}).toHaveCount({Count(step.Value)});",
            StepAction.AssertUrl => $"{pad}await expect(page).toHaveURL(/{RegexOf(step.Value)}/);",
            StepAction.AssertTitle => $"{pad}await expect(page).toHaveTitle(/{RegexOf(step.Value)}/);",
            StepAction.Screenshot => $"{pad}await page.screenshot({{ path: {Qs(Slug(step.Description ?? "screenshot") + ".png")} }});",
            _ => $"{pad}// TODO: {step.Action}"
        };
    }

    // ------------------------------------------------------------------ Python

    private static string Python(Scenario s)
    {
        const ExportLang lang = ExportLang.Python;
        var sb = new StringBuilder();
        sb.AppendLine("import re");
        var imports = ExportTemplate.PythonImports(s);
        if (imports.Length > 0) sb.Append(imports);
        sb.AppendLine("from playwright.sync_api import Page, expect");
        sb.AppendLine();
        foreach (var v in s.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
            sb.AppendLine($"{Snake(v.Name).ToUpperInvariant()} = {Qs(v.Value)}");
        if (s.Variables.Count > 0) sb.AppendLine();

        sb.AppendLine($"def test_{Snake(s.Name)}(page: Page):");
        var wrote = false;
        if (!string.IsNullOrWhiteSpace(s.StartUrl) &&
            s.Steps.FirstOrDefault(x => x.Enabled)?.Action != StepAction.Navigate)
        {
            sb.AppendLine($"    page.goto({ExportTemplate.Expression(s.StartUrl, s, lang)})");
            wrote = true;
        }

        foreach (var step in s.Steps.Where(x => x.Enabled))
        {
            if (!string.IsNullOrWhiteSpace(step.Description) && step.Action != StepAction.Comment)
                sb.AppendLine($"    # {Flatten(step.Description)}");
            sb.AppendLine(PyStep(step, s));
            wrote = true;
        }
        if (!wrote) sb.AppendLine("    pass");
        return sb.ToString();
    }

    private static string PyStep(TestStep step, Scenario s)
    {
        const ExportLang lang = ExportLang.Python;
        const string pad = "    ";

        var target = "page";
        foreach (var frame in step.FrameChain)
            target += $".frame_locator({ExportTemplate.Expression(frame, s, lang)})";
        var all = $"{target}.locator({ExportTemplate.Expression(step.Selector ?? "", s, lang)})";
        var loc = all + ".first";
        var val = ExportTemplate.Expression(step.Value, s, lang);

        return step.Action switch
        {
            StepAction.Comment => $"{pad}# {Flatten(step.Value)}",
            StepAction.Navigate => $"{pad}page.goto({val})",
            StepAction.Click => $"{pad}{loc}.click()",
            StepAction.DoubleClick => $"{pad}{loc}.dblclick()",
            StepAction.RightClick => $"{pad}{loc}.click(button=\"right\")",
            StepAction.Fill => $"{pad}{loc}.fill({val})",
            StepAction.Type => $"{pad}{loc}.press_sequentially({val})",
            StepAction.Press => $"{pad}{loc}.press({val})",
            StepAction.Select => $"{pad}{loc}.select_option([{ExportTemplate.Join(ExportTemplate.List(step.Value, ',', s, lang))}])",
            StepAction.Check => $"{pad}{loc}.check()",
            StepAction.Uncheck => $"{pad}{loc}.uncheck()",
            StepAction.Hover => $"{pad}{loc}.hover()",
            StepAction.Focus => $"{pad}{loc}.focus()",
            StepAction.ScrollIntoView => $"{pad}{loc}.scroll_into_view_if_needed()",
            StepAction.Upload => $"{pad}{loc}.set_input_files([{ExportTemplate.Join(ExportTemplate.List(step.Value, ';', s, lang))}])",
            StepAction.WaitForSelector => $"{pad}{loc}.wait_for(state=\"visible\")",
            StepAction.WaitForHidden => $"{pad}{loc}.wait_for(state=\"hidden\")",
            StepAction.WaitForUrl => $"{pad}page.wait_for_url({val})",
            StepAction.WaitForTimeout => $"{pad}page.wait_for_timeout({Ms(step.Value)})",
            StepAction.AssertVisible => $"{pad}expect({loc}).to_be_visible()",
            StepAction.AssertHidden => $"{pad}expect({loc}).to_be_hidden()",
            StepAction.AssertText => $"{pad}expect({loc}).to_contain_text({val})",
            StepAction.AssertValue => $"{pad}expect({loc}).to_have_value({val})",
            StepAction.AssertChecked => IsFalse(step.Value)
                                            ? $"{pad}expect({loc}).not_to_be_checked()"
                                            : $"{pad}expect({loc}).to_be_checked()",
            StepAction.AssertCount => $"{pad}expect({all}).to_have_count({Count(step.Value)})",
            StepAction.AssertUrl => $"{pad}expect(page).to_have_url(re.compile({Qs(RegexOf(step.Value))}))",
            StepAction.AssertTitle => $"{pad}expect(page).to_have_title(re.compile({Qs(RegexOf(step.Value))}))",
            StepAction.Screenshot => $"{pad}page.screenshot(path={Qs(Slug(step.Description ?? "screenshot") + ".png")})",
            _ => $"{pad}# TODO: {step.Action}"
        };
    }

    // ------------------------------------------------------------------ text helpers

    private static string Flatten(string? s) => (s ?? "").Replace("\r", "").Replace("\n", " ");

    private static bool IsFalse(string? v) => string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    private static int Ms(string? v) => int.TryParse(v, out var ms) ? ms : 1000;

    private static int Count(string? v) => int.TryParse(v, out var n) ? n : 1;

    private static string Q(string? s) =>
        "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

    private static string Qs(string? s) =>
        "'" + (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "") + "'";

    private static string RegexOf(string? expected)
    {
        var v = (expected ?? "").Trim();
        if (v.StartsWith('=')) v = v[1..];
        else if (v.Length > 1 && v.StartsWith('/') && v.EndsWith('/')) return v[1..^1];
        // Regex.Escape also escapes spaces, which reads badly in generated code.
        return System.Text.RegularExpressions.Regex.Escape(v).Replace(@"\ ", " ");
    }

    public static string Slug(string name)
    {
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "scenario" : slug;
    }

    public static string Pascal(string name)
    {
        var parts = name.Split(new[] { ' ', '-', '_', '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            var clean = new string(p.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(clean[0])).Append(clean[1..]);
        }
        var result = sb.ToString();
        if (result.Length == 0) return "Scenario";
        return char.IsDigit(result[0]) ? "S" + result : result;
    }

    public static string Camel(string name)
    {
        var p = Pascal(name);
        return char.ToLowerInvariant(p[0]) + p[1..];
    }

    public static string Snake(string name)
    {
        var slug = Slug(name).Replace('-', '_');
        return char.IsDigit(slug[0]) ? "s" + slug : slug;
    }
}
