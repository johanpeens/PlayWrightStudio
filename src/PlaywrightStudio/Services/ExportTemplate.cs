using System.Text;
using System.Text.RegularExpressions;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

public enum ExportLang { CSharp, TypeScript, Python }

/// <summary>
/// Turns a value or selector that contains {{placeholders}} into a runnable expression in the
/// target language, so exported tests behave like a run inside the studio rather than shipping
/// a literal "{{random:4}}".
/// </summary>
public static partial class ExportTemplate
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_:\.]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex Placeholder();

    /// <summary>True when anything in the scenario needs a built-in helper at run time.</summary>
    public static bool UsesBuiltIns(Scenario scenario) =>
        scenario.Steps.SelectMany(s => new[] { s.Value, s.Selector })
            .Where(t => !string.IsNullOrEmpty(t))
            .SelectMany(t => Placeholder().Matches(t!).Cast<Match>())
            .Any(m => !scenario.Variables.Any(v => string.Equals(v.Name, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)));

    /// <summary>An expression - a plain quoted string when there is nothing to expand.</summary>
    public static string Expression(string? template, Scenario scenario, ExportLang lang)
    {
        template ??= "";
        if (!template.Contains("{{")) return Quote(template, lang);

        var parts = new List<string>();
        var last = 0;
        foreach (Match m in Placeholder().Matches(template))
        {
            if (m.Index > last) parts.Add(Quote(template[last..m.Index], lang));
            parts.Add(Piece(m.Groups[1].Value, scenario, lang, m.Value));
            last = m.Index + m.Length;
        }
        if (last < template.Length) parts.Add(Quote(template[last..], lang));

        return parts.Count switch
        {
            0 => Quote("", lang),
            1 => parts[0],
            _ => string.Join(" + ", parts)
        };
    }

    private static string Piece(string key, Scenario scenario, ExportLang lang, string original)
    {
        var variable = scenario.Variables.FirstOrDefault(v => string.Equals(v.Name, key, StringComparison.OrdinalIgnoreCase));
        if (variable is not null)
        {
            return lang switch
            {
                ExportLang.TypeScript => CodeExporter.Camel(variable.Name),
                ExportLang.Python => CodeExporter.Snake(variable.Name).ToUpperInvariant(),
                _ => CodeExporter.Pascal(variable.Name)
            };
        }

        var (name, arg) = key.Contains(':')
            ? (key[..key.IndexOf(':')].ToLowerInvariant(), key[(key.IndexOf(':') + 1)..])
            : (key.ToLowerInvariant(), "");
        var size = int.TryParse(arg, out var n) ? Math.Clamp(n, 1, 32) : 6;

        return (name, lang) switch
        {
            ("guid", ExportLang.CSharp) => "Guid.NewGuid().ToString()",
            ("guid", ExportLang.TypeScript) => "crypto.randomUUID()",
            ("guid", ExportLang.Python) => "str(uuid.uuid4())",

            ("timestamp", ExportLang.CSharp) => "DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString()",
            ("timestamp", ExportLang.TypeScript) => "String(Date.now())",
            ("timestamp", ExportLang.Python) => "str(int(time.time() * 1000))",

            ("now", ExportLang.CSharp) => $"DateTime.Now.ToString(\"{(arg.Length == 0 ? "yyyy-MM-dd HH:mm:ss" : arg)}\")",
            ("now", ExportLang.TypeScript) => "new Date().toISOString()",
            ("now", ExportLang.Python) => "datetime.now().isoformat(timespec='seconds')",

            ("today", ExportLang.CSharp) => $"DateTime.Today.ToString(\"{(arg.Length == 0 ? "yyyy-MM-dd" : arg)}\")",
            ("today", ExportLang.TypeScript) => "new Date().toISOString().slice(0, 10)",
            ("today", ExportLang.Python) => "date.today().isoformat()",

            ("random", ExportLang.CSharp) => $"Random.Shared.Next(0, {Pow10(size)}).ToString(\"{new string('0', size)}\")",
            ("random", ExportLang.TypeScript) => $"String(Math.floor(Math.random() * 1e{size})).padStart({size}, '0')",
            ("random", ExportLang.Python) => $"str(random.randrange(10 ** {size})).zfill({size})",

            ("randomtext", ExportLang.CSharp) => $"Guid.NewGuid().ToString(\"n\")[..{size}]",
            ("randomtext", ExportLang.TypeScript) => $"Math.random().toString(36).slice(2, {2 + size})",
            ("randomtext", ExportLang.Python) => $"''.join(random.choices(string.ascii_lowercase, k={size}))",

            ("randomemail", ExportLang.CSharp) => "$\"test+{Random.Shared.Next(100000, 999999)}@example.com\"",
            ("randomemail", ExportLang.TypeScript) => "`test+${Math.floor(100000 + Math.random() * 899999)}@example.com`",
            ("randomemail", ExportLang.Python) => "f\"test+{random.randrange(100000, 999999)}@example.com\"",

            // Unknown name: leave it visible rather than silently inventing a value.
            _ => Quote(original, lang)
        };
    }

    private static int Pow10(int size) => size >= 9 ? 1_000_000_000 : (int)Math.Pow(10, size);

    private static string Quote(string s, ExportLang lang) => lang switch
    {
        ExportLang.CSharp => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"",
        _ => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "") + "'"
    };

    /// <summary>Splits a comma/semicolon list, keeping each item as its own expression.</summary>
    public static IEnumerable<string> List(string? value, char separator, Scenario scenario, ExportLang lang) =>
        (value ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(item => Expression(item, scenario, lang));

    public static string Join(IEnumerable<string> items) => string.Join(", ", items);

    /// <summary>Python needs its helper imports declared up front.</summary>
    public static string PythonImports(Scenario scenario)
    {
        if (!UsesBuiltIns(scenario)) return "";
        var sb = new StringBuilder();
        sb.AppendLine("import random");
        sb.AppendLine("import string");
        sb.AppendLine("import time");
        sb.AppendLine("import uuid");
        sb.AppendLine("from datetime import date, datetime");
        return sb.ToString();
    }
}
