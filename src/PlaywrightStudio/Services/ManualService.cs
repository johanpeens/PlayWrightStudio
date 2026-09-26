using System.Net;
using System.Text;
using Markdig;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>
/// Builds a written manual - a first draft from a run, then the document itself once you have
/// edited it. Rendered through Chromium, which the studio already has, rather than a PDF library.
/// </summary>
public sealed class ManualService
{
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()                 // the manual is generated, not a place to inject markup
        .Build();

    private readonly StudioPaths _paths;
    private readonly ManualStore _manuals;
    private readonly ILogger<ManualService> _log;

    public ManualService(StudioPaths paths, ManualStore manuals, ILogger<ManualService> log)
    {
        _paths = paths;
        _manuals = manuals;
        _log = log;
    }

    // ------------------------------------------------------------------ first draft

    /// <summary>
    /// Turns a run into a draft manual: your written instructions where you have them, a plain
    /// description where you have not, and each step's screenshot copied in.
    /// </summary>
    public Manual CreateFromRun(Scenario scenario, RunResult run)
    {
        var manual = new Manual
        {
            Title = scenario.Name,
            Intro = scenario.Description,
            ScenarioId = scenario.Id,
            RunId = run.Id
        };

        var runFolder = _paths.RunFolder(run.Id);

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            if (!step.Enabled || step.HideFromManual) continue;
            if (step.Action is StepAction.WaitForTimeout or StepAction.WaitForSelector
                            or StepAction.WaitForHidden or StepAction.ScrollIntoView) continue;

            var result = run.Steps.FirstOrDefault(r => r.StepId == step.Id)
                         ?? (i < run.Steps.Count ? run.Steps[i] : null);
            if (result?.Status == StepStatus.Skipped) continue;

            var entry = new ManualStep
            {
                SourceStepId = step.Id,
                Text = string.IsNullOrWhiteSpace(step.Instruction) ? Fallback(step) : step.Instruction!
            };

            if (!string.IsNullOrWhiteSpace(result?.ScreenshotFile))
            {
                var source = Path.Combine(runFolder, result!.ScreenshotFile!);
                if (File.Exists(source)) entry.ImageFile = _manuals.AddImage(manual.Id, source);
            }

            manual.Steps.Add(entry);
        }

        _manuals.Save(manual);
        return manual;
    }

    // ------------------------------------------------------------------ the document


    /// <summary>The same document as html - used for the on-screen preview and for the pdf.</summary>
    public string BuildHtml(Manual manual)
    {
        var folder = _manuals.FolderFor(manual.Id);
        var sb = new StringBuilder();

        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(manual.Title)).Append("</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        sb.Append("<header class=\"cover\"><p class=\"eyebrow\">User guide</p>");
        sb.Append("<h1>").Append(WebUtility.HtmlEncode(manual.Title)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(manual.Intro))
            sb.Append("<div class=\"lede\">").Append(Markdig.Markdown.ToHtml(manual.Intro!, Markdown)).Append("</div>");
        sb.Append("<p class=\"meta\">").Append(manual.UpdatedUtc.ToLocalTime().ToString("d MMMM yyyy")).Append("</p>");
        sb.Append("</header>");

        var number = 0;
        foreach (var step in manual.Steps.Where(s => s.Include))
        {
            number++;
            sb.Append("<section class=\"step\"><div class=\"num\">").Append(number).Append("</div><div class=\"body\">");
            sb.Append("<div class=\"instruction\">")
              .Append(string.IsNullOrWhiteSpace(step.Text)
                  ? "<p class=\"todo\">(nothing written for this step yet)</p>"
                  : Markdig.Markdown.ToHtml(step.Text, Markdown))
              .Append("</div>");

            if (!string.IsNullOrWhiteSpace(step.ImageFile))
            {
                var file = Path.Combine(folder, step.ImageFile!);
                if (File.Exists(file))
                {
                    // Embedded, so the pdf stands alone once it leaves this machine.
                    var data = Convert.ToBase64String(File.ReadAllBytes(file));
                    var type = MimeFor(file);
                    sb.Append("<figure><img alt=\"Step ").Append(number)
                      .Append("\" src=\"data:image/").Append(type).Append(";base64,").Append(data).Append("\"></figure>");
                }
            }

            sb.Append("</div></section>");
        }

        if (number == 0) sb.Append("<p class=\"empty\">This manual has no steps in it yet.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// A data uri is taken at its word - there is no sniffing to fall back on - so a picture the
    /// reader swapped in has to be labelled with its real type or it will not render in the pdf.
    /// </summary>
    private static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "jpeg",
        ".gif" => "gif",
        ".webp" => "webp",
        ".bmp" => "bmp",
        _ => "png"
    };

    // ------------------------------------------------------------------ wording

    /// <summary>When nothing was written for a step, say something usable rather than nothing.</summary>
    private static string Fallback(TestStep step) => step.Action switch
    {
        StepAction.Navigate when IsSignInUrl(step.Value ?? "") =>
            $"Click the sign-in link below. It takes you to {Host(step.Value)} to sign in.\n\n{Link(step.Value)}",
        StepAction.Navigate => $"Go to {FriendlyUrl(step.Value ?? "")} using the link below.\n\n{Link(step.Value)}",
        StepAction.Fill or StepAction.Type => $"Enter \"{step.Value}\" in {Target(step)}.",
        StepAction.Select => $"Choose \"{step.Value}\" in {Target(step)}.",
        StepAction.Check => $"Tick {Target(step)}.",
        StepAction.Uncheck => $"Untick {Target(step)}.",
        StepAction.Press => $"Press {step.Value}.",
        StepAction.Upload => $"Attach a file in {Target(step)}.",
        StepAction.Comment => step.Value ?? "",
        _ when step.Action.IsAssertion() => $"Check that {Target(step)} is as expected.",
        _ => $"Click {Target(step)}."
    };

    /// <summary>A markdown link with a readable label rather than a wall of query string.</summary>
    private static string Link(string? url) =>
        string.IsNullOrWhiteSpace(url) ? "" : $"[{FriendlyUrl(url)}]({url})";

    private static string FriendlyUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var text = uri.Host + (uri.AbsolutePath == "/" ? "" : uri.AbsolutePath);
            return text.Length > 70 ? text[..70] + "…" : text;
        }
        catch
        {
            return url.Length > 70 ? url[..70] + "…" : url;
        }
    }

    /// <summary>
    /// A sign-in redirect carries one-time nonces and challenges. Telling a reader to type it
    /// out is nonsense - they click the link, or the button that produced it.
    /// </summary>
    private static bool IsSignInUrl(string url) =>
        url.Length > 300
        || url.Contains("code_challenge", StringComparison.OrdinalIgnoreCase)
        || url.Contains("client_id", StringComparison.OrdinalIgnoreCase)
        || url.Contains("nonce", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/signin", StringComparison.OrdinalIgnoreCase);

    private static string Host(string? url)
    {
        try { return new Uri(url ?? "").Host; } catch { return "the sign-in page"; }
    }

    /// <summary>The friendly name the recorder gave the element, or the selector as a last resort.</summary>
    private static string Target(TestStep step)
    {
        var description = step.Description ?? "";
        var open = description.IndexOf('"');
        var close = description.LastIndexOf('"');
        if (open >= 0 && close > open) return description[open..(close + 1)];
        return string.IsNullOrWhiteSpace(step.Selector) ? "the control" : $"\"{step.Selector}\"";
    }

    private const string Css = """
      * { box-sizing: border-box; }
      body { margin: 0; color: #171a20; background: #fff;
             font: 11pt/1.55 "Segoe UI", system-ui, -apple-system, sans-serif; }
      .cover { border-bottom: 2px solid #171a20; padding-bottom: 18px; margin-bottom: 26px; }
      .eyebrow { margin: 0 0 6px; font: 600 8.5pt/1 ui-monospace, Consolas, monospace;
                 letter-spacing: .16em; text-transform: uppercase; color: #6b7484; }
      h1 { margin: 0 0 8px; font-size: 25pt; line-height: 1.1; letter-spacing: -.02em; }
      .lede { font-size: 11.5pt; color: #444c59; max-width: 52em; }
      .lede p { margin: 0 0 6px; }
      .meta { margin: 6px 0 0; font-size: 9pt; color: #6b7484; }

      .step { display: grid; grid-template-columns: 34px 1fr; gap: 14px; margin: 0 0 22px;
              page-break-inside: avoid; break-inside: avoid; }
      .num { width: 30px; height: 30px; border-radius: 50%; background: #171a20; color: #fff;
             font: 600 11pt/30px "Segoe UI", sans-serif; text-align: center; }
      .body { min-width: 0; }
      .instruction > :first-child { margin-top: 2px; }
      .instruction p { margin: 0 0 8px; }
      .instruction ul, .instruction ol { margin: 0 0 8px; padding-left: 20px; }
      .instruction a { color: #1c4fa1; word-break: break-all; }
      .instruction code { background: #f1f3f7; border: 1px solid #e2e6ee; border-radius: 3px;
                          padding: 0 4px; font: 9.5pt ui-monospace, Consolas, monospace; }
      .instruction blockquote { margin: 0 0 8px; padding: 6px 12px; border-left: 3px solid #c9d2e0;
                                background: #f6f8fb; color: #46505f; }
      .todo { color: #a2331f; }

      figure { margin: 8px 0 0; }
      /* Screenshots are wide, so fill the column - but never upscale a small picture. */
      figure img { display: block; max-width: 100%; width: auto; border: 1px solid #d8dde6;
                   border-radius: 5px; }
      .empty { color: #6b7484; }
      """;
}
