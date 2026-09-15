using System.Text.RegularExpressions;
using PlaywrightStudio.Models;

namespace PlaywrightStudio.Services;

/// <summary>Expands {{placeholders}} in step values from scenario variables plus a few built-ins.</summary>
public static partial class ValueResolver
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_:\.]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex Placeholder();

    public static string? Resolve(string? input, IEnumerable<ScenarioVariable> variables)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("{{")) return input;

        var map = variables.Where(v => !string.IsNullOrWhiteSpace(v.Name))
                           .ToDictionary(v => v.Name.Trim(), v => v.Value ?? "", StringComparer.OrdinalIgnoreCase);

        return Placeholder().Replace(input, m =>
        {
            var key = m.Groups[1].Value;
            if (map.TryGetValue(key, out var val)) return val;
            return BuiltIn(key) ?? m.Value;
        });
    }

    private static string? BuiltIn(string key)
    {
        var (name, arg) = key.Contains(':')
            ? (key[..key.IndexOf(':')], key[(key.IndexOf(':') + 1)..])
            : (key, "");

        switch (name.ToLowerInvariant())
        {
            case "guid": return Guid.NewGuid().ToString();
            case "timestamp": return DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            case "now": return DateTime.Now.ToString(string.IsNullOrEmpty(arg) ? "yyyy-MM-dd HH:mm:ss" : arg);
            case "today": return DateTime.Today.ToString(string.IsNullOrEmpty(arg) ? "yyyy-MM-dd" : arg);
            case "random":
                var len = int.TryParse(arg, out var n) ? Math.Clamp(n, 1, 64) : 6;
                return Random.Shared.Next(0, (int)Math.Pow(10, Math.Min(len, 9))).ToString(new string('0', len));
            case "randomtext":
                var size = int.TryParse(arg, out var t) ? Math.Clamp(t, 1, 64) : 8;
                const string alphabet = "abcdefghijklmnopqrstuvwxyz";
                return new string(Enumerable.Range(0, size).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
            case "randomemail":
                return $"test+{Random.Shared.Next(100000, 999999)}@example.com";
            default: return null;
        }
    }

    /// <summary>Names referenced by a scenario that have no variable defined for them.</summary>
    public static IReadOnlyList<string> MissingVariables(Scenario scenario)
    {
        var defined = scenario.Variables.Select(v => v.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var step in scenario.Steps)
        {
            foreach (var text in new[] { step.Value, step.Selector })
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (Match m in Placeholder().Matches(text))
                {
                    var key = m.Groups[1].Value;
                    if (defined.Contains(key)) continue;
                    if (BuiltIn(key) is not null) continue;
                    if (!missing.Contains(key, StringComparer.OrdinalIgnoreCase)) missing.Add(key);
                }
            }
        }
        return missing;
    }
}
