using System.Globalization;
using System.Text;
using FolderNotes.Core.Search;

namespace FolderNotes.App.Search;

/// <summary>A highlighted span: <see cref="Start"/> and <see cref="Length"/> in the original text.</summary>
public readonly record struct HighlightRange(int Start, int Length);

/// <summary>
/// Which parts of a title or path to highlight (specs/05 § Search palette behaviour): every word that
/// starts with a term word, for the length of that term word, ignoring case and diacritics. Pure.
/// </summary>
public static class SearchHighlight
{
    public static IReadOnlyList<HighlightRange> Ranges(string text, IEnumerable<SearchTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terms);
        var needles = terms.SelectMany(t => FoldedWords(t.Text)).Distinct(StringComparer.Ordinal).ToList();
        if (needles.Count == 0 || text.Length == 0)
        {
            return [];
        }

        var folded = Fold(text); // same length as text
        var hits = new List<HighlightRange>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            var word = folded.Substring(start, i - start);
            var best = needles.Where(n => word.StartsWith(n, StringComparison.Ordinal)).Select(n => n.Length).DefaultIfEmpty(0).Max();
            if (best > 0)
            {
                hits.Add(new HighlightRange(start, best));
            }
        }

        return hits;
    }

    /// <summary>Lower-case, one folded char per input char (precomposed letters lose their marks).</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var d = ch.ToString().Normalize(NormalizationForm.FormD);
            sb.Append(char.ToLowerInvariant(d[0]));
        }

        return sb.ToString();
    }

    private static IEnumerable<string> FoldedWords(string term)
    {
        var sb = new StringBuilder();
        foreach (var ch in term.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }
}
