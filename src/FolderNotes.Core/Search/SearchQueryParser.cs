using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Paths;
using FolderNotes.Core.Storage;

namespace FolderNotes.Core.Search;

/// <summary>A free-text term. <see cref="IsPhrase"/> is true for quoted multi-word input.</summary>
public sealed record SearchTerm(string Text, bool IsPhrase);

public sealed record ParseError(string Token, string Message);

/// <summary>The result of parsing a raw query (03 § Search query grammar).</summary>
public sealed record ParsedQuery(IReadOnlyList<SearchTerm> Terms, NoteQuery Filters, IReadOnlyList<ParseError> Errors)
{
    public bool HasTerms => Terms.Count > 0;
}

/// <summary>Parses <c>acme label:client status:keep path:D:\Projects due:30d keep:&lt;2027</c>. Pure.</summary>
public static partial class SearchQueryParser
{
    public static ParsedQuery Parse(string? raw, DateOnly today)
    {
        var terms = new List<SearchTerm>();
        var errors = new List<ParseError>();
        var labels = new List<string>();
        var statuses = new List<NoteStatus>();
        var paths = new List<string>();
        DateRange? due = null;
        DateRange? keep = null;

        foreach (var (token, quotedValue) in Tokenize(raw ?? ""))
        {
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            var key = colon > 0 ? token[..colon].ToUpperInvariant() : "";
            var value = colon > 0 ? token[(colon + 1)..] : "";

            switch (key)
            {
                case "LABEL" when value.Length > 0:
                    labels.Add(value);
                    break;
                case "STATUS" when value.Length > 0:
                    if (NoteStatusExtensions.TryParseKey(value, out var st))
                    {
                        statuses.Add(st);
                    }
                    else
                    {
                        errors.Add(new(token, $"Unknown status '{value}'. Use one of: {string.Join(", ", NoteStatusExtensions.Keys)}."));
                    }

                    break;
                case "PATH" when value.Length > 0:
                    if (PathNormalizer.TryNormalize(value, out var norm))
                    {
                        paths.Add(norm);
                    }
                    else
                    {
                        errors.Add(new(token, $"'{value}' isn't a folder path."));
                    }

                    break;
                case "DUE" when value.Length > 0:
                    if (TryParseDateFilter(value, today, allowOverdue: true, out var d))
                    {
                        due = d;
                    }
                    else
                    {
                        errors.Add(new(token, $"Can't read the date filter '{value}'. Try due:overdue, due:30d or due:<2027."));
                    }

                    break;
                case "KEEP" when value.Length > 0:
                    if (TryParseDateFilter(value, today, allowOverdue: false, out var k))
                    {
                        keep = k;
                    }
                    else
                    {
                        errors.Add(new(token, $"Can't read the date filter '{value}'. Try keep:<2027 or keep:2027-06."));
                    }

                    break;
                default:
                    var text = token.Trim();
                    if (text.Length > 0)
                    {
                        terms.Add(new SearchTerm(text, quotedValue && text.Contains(' ', StringComparison.Ordinal)));
                    }

                    break;
            }
        }

        var filters = new NoteQuery { Labels = labels, Statuses = statuses, PathsUnder = paths, Due = due, Keep = keep };
        return new ParsedQuery(terms, filters, errors);
    }

    /// <summary>Splits on whitespace. Quotes group words and are removed. Returns (token, containedQuotes).</summary>
    internal static IEnumerable<(string Token, bool Quoted)> Tokenize(string raw)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                quoted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    yield return (sb.ToString(), quoted);
                }

                sb.Clear();
                quoted = false;
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0)
        {
            yield return (sb.ToString(), quoted);
        }
    }

    internal static bool TryParseDateFilter(string value, DateOnly today, bool allowOverdue, out DateRange? range)
    {
        range = null;
        var v = value.Trim().ToLowerInvariant();

        if (v == "overdue")
        {
            if (allowOverdue)
            {
                range = new DateRange(null, today.AddDays(-1));
            }

            return range is not null;
        }

        if (v == "today")
        {
            range = new DateRange(today, today);
            return true;
        }

        var rel = RelativeRegex().Match(v);
        if (rel.Success)
        {
            if (!int.TryParse(rel.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 100_000)
            {
                return false;
            }

            var end = rel.Groups[2].Value switch
            {
                "d" => today.AddDays(n),
                "w" => today.AddDays(n * 7),
                "m" => today.AddMonths(Math.Min(n, 12 * 1000)),
                _ => today.AddYears(Math.Min(n, 1000)),
            };
            range = new DateRange(today, end);
            return true;
        }

        var cmp = ComparisonRegex().Match(v);
        if (!cmp.Success || !TryParsePeriod(cmp.Groups[2].Value, out var start, out var last))
        {
            return false;
        }

        range = cmp.Groups[1].Value switch
        {
            "<" => new DateRange(null, start.AddDays(-1)),
            "<=" => new DateRange(null, last),
            ">" => new DateRange(last.AddDays(1), null),
            ">=" => new DateRange(start, null),
            _ => new DateRange(start, last),
        };
        return true;
    }

    /// <summary><c>yyyy</c>, <c>yyyy-mm</c> or <c>yyyy-mm-dd</c> → first and last day.</summary>
    internal static bool TryParsePeriod(string s, out DateOnly first, out DateOnly last)
    {
        first = last = default;
        var parts = s.Split('-');
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
            {
                return false;
            }
        }

        try
        {
            switch (nums.Length)
            {
                case 1 when parts[0].Length == 4:
                    first = new DateOnly(nums[0], 1, 1);
                    last = new DateOnly(nums[0], 12, 31);
                    return true;
                case 2 when parts[0].Length == 4:
                    first = new DateOnly(nums[0], nums[1], 1);
                    last = first.AddMonths(1).AddDays(-1);
                    return true;
                case 3 when parts[0].Length == 4:
                    first = last = new DateOnly(nums[0], nums[1], nums[2]);
                    return true;
                default:
                    return false;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(\d{1,6})([dwmy])$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"^(<=|>=|<|>|)(\d{4}(?:-\d{1,2}(?:-\d{1,2})?)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();
}
