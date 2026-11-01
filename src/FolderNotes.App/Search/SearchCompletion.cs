using System.Text.RegularExpressions;
using FolderNotes.Core.Domain;

namespace FolderNotes.App.Search;

/// <summary>One autocomplete choice: what the list shows, and the box's text and caret after accepting it.</summary>
public sealed record SearchSuggestion(string Display, string NewText, int NewCaret)
{
    public override string ToString() => Display;
}

/// <summary>
/// Filter autocomplete for the palette (specs/05 § Search palette behaviour): after
/// <c>label:</c>, <c>status:</c> or <c>due:</c> at the caret. Pure.
/// </summary>
public static partial class SearchCompletion
{
    public const int MaxSuggestions = 8;

    private static readonly string[] DueValues = ["overdue", "7d", "30d", "90d"];

    public static IReadOnlyList<SearchSuggestion> Suggest(string text, int caret, IEnumerable<string> labelNames)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(labelNames);
        caret = Math.Clamp(caret, 0, text.Length);
        var start = TokenStart(text, caret);
        var match = FilterToken().Match(text[start..caret]);
        if (!match.Success)
        {
            return [];
        }

        var key = match.Groups["key"].Value.ToLowerInvariant();
        var partial = match.Groups["value"].Value;
        IEnumerable<string> values = key switch
        {
            "label" => labelNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            "status" => NoteStatusExtensions.Keys,
            _ => DueValues,
        };

        var rest = text[caret..];
        if (rest.StartsWith(' '))
        {
            rest = rest[1..];
        }

        return [.. values
            .Where(v => v.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .Take(MaxSuggestions)
            .Select(v =>
            {
                var token = key + ":" + (v.Contains(' ', StringComparison.Ordinal) ? "\"" + v + "\"" : v);
                return new SearchSuggestion(v, text[..start] + token + " " + rest, start + token.Length + 1);
            })];
    }

    /// <summary>Start of the whitespace-separated token that ends at <paramref name="caret"/>; spaces inside quotes don't split.</summary>
    private static int TokenStart(string text, int caret)
    {
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < caret; i++)
        {
            if (text[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(text[i]) && !inQuotes)
            {
                start = i + 1;
            }
        }

        return start;
    }

    [GeneratedRegex("^(?<key>label|status|due):\"?(?<value>[^\"]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilterToken();
}
