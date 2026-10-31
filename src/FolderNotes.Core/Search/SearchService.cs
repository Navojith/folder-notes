using System.Globalization;
using System.Text;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Storage;
using Microsoft.Data.Sqlite;

namespace FolderNotes.Core.Search;

public enum SearchMatchKind
{
    /// <summary>Full-text (FTS5) match.</summary>
    FullText,

    /// <summary>Typo-tolerant fallback match.</summary>
    Fuzzy,

    /// <summary>No text terms: matched by filters only.</summary>
    Filter,
}

/// <summary>One search result. Lower <see cref="Rank"/> is better within a kind.</summary>
public sealed record SearchHit(Note Note, SearchMatchKind Kind, double Rank);

public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string rawQuery, int limit, CancellationToken ct = default);
}

/// <summary>REQ-051/052/023 search over <see cref="SqliteNoteStore"/> (03 § Search query grammar).</summary>
public sealed class SearchService : ISearchService, IDisposable
{
    internal const int FuzzyThreshold = 5;

    private readonly SqliteNoteStore _store;
    private readonly Lock _gate = new();
    private IReadOnlyList<Candidate>? _candidates;

    public SearchService(SqliteNoteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.NoteChanged += OnChanged;
        _store.LabelsChanged += OnChanged;
    }

    public void Dispose()
    {
        _store.NoteChanged -= OnChanged;
        _store.LabelsChanged -= OnChanged;
    }

    /// <summary>Parses with today's local date from the store's clock.</summary>
    public ParsedQuery Parse(string rawQuery) => SearchQueryParser.Parse(rawQuery, Today());

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string rawQuery, int limit, CancellationToken ct = default) =>
        SearchAsync(Parse(rawQuery), limit, ct);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(ParsedQuery query, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        if (!query.HasTerms)
        {
            var notes = await _store.ListAsync(query.Filters with { Sort = NoteSort.UpdatedDesc, Limit = limit }, ct).ConfigureAwait(false);
            return [.. notes.Select((n, i) => new SearchHit(n, SearchMatchKind.Filter, i))];
        }

        return await _store.ReadAsync(c => Run(c, query, limit), ct).ConfigureAwait(false);
    }

    private IReadOnlyList<SearchHit> Run(SqliteConnection c, ParsedQuery query, int limit)
    {
        var labels = query.Filters.Labels.Count > 0 ? SqliteNoteStore.LoadLabels(c, null) : [];
        var where = NoteFilterSql.Where(query.Filters with { State = NoteListState.Active }, labels, out var bind);

        // 1. Full text, ranked by bm25 (title 10, body 1, labels 5, path 2).
        var ranked = new List<(Guid Id, double Rank, SearchMatchKind Kind)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT n.id, bm25(notes_fts, 10.0, 1.0, 5.0, 2.0) AS rank
                FROM notes_fts JOIN notes n ON n.rowid = notes_fts.rowid
                WHERE notes_fts MATCH $match AND {where}
                ORDER BY rank, n.updated_at DESC
                LIMIT $limit
                """;
            bind(cmd);
            cmd.Parameters.AddWithValue("$match", ToFtsQuery(query.Terms));
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ranked.Add((Guid.ParseExact(r.GetString(0), "D"), r.GetDouble(1), SearchMatchKind.FullText));
            }
        }

        // 2. Typo fallback.
        if (ranked.Count < FuzzyThreshold && ranked.Count < limit)
        {
            var filtered = query.Filters.Labels.Count > 0 || query.Filters.Statuses.Count > 0 || query.Filters.PathsUnder.Count > 0
                || query.Filters.Due is not null || query.Filters.Keep is not null;
            var allowed = filtered ? AllowedIds(c, where, bind) : null; // candidates are already live notes only
            var seen = ranked.Select(h => h.Id).ToHashSet();
            var fuzzy = Candidates(c)
                .Where(cand => (allowed is null || allowed.Contains(cand.Id)) && !seen.Contains(cand.Id))
                .Select(cand => (cand.Id, Distance: FuzzyDistance(cand, query.Terms)))
                .Where(x => x.Distance >= 0)
                .OrderBy(x => x.Distance)
                .Take(limit - ranked.Count);
            ranked.AddRange(fuzzy.Select(x => (x.Id, (double)x.Distance, SearchMatchKind.Fuzzy)));
        }

        if (ranked.Count == 0)
        {
            return [];
        }

        var notes = SqliteNoteStore.QueryNotesForSearch(c, ranked.Select(h => h.Id).ToList());
        return [.. ranked.Where(h => notes.ContainsKey(h.Id)).Select(h => new SearchHit(notes[h.Id], h.Kind, h.Rank))];
    }

    /// <summary>Each term becomes a quoted FTS5 string with a prefix star, so user input can't inject FTS syntax.</summary>
    internal static string ToFtsQuery(IEnumerable<SearchTerm> terms) =>
        string.Join(' ', terms.Select(t => "\"" + t.Text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));

    private static HashSet<Guid> AllowedIds(SqliteConnection c, string where, Action<SqliteCommand> bind)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT n.id FROM notes n WHERE {where}";
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var set = new HashSet<Guid>();
        while (r.Read())
        {
            set.Add(Guid.ParseExact(r.GetString(0), "D"));
        }

        return set;
    }

    // ───────────── typo tolerance ─────────────

    internal sealed record Candidate(Guid Id, string[] Words);

    private IReadOnlyList<Candidate> Candidates(SqliteConnection c)
    {
        lock (_gate)
        {
            if (_candidates is not null)
            {
                return _candidates;
            }
        }

        var list = new List<Candidate>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT n.id, n.title,
                       coalesce((SELECT group_concat(l.name, ' ') FROM note_labels nl JOIN labels l ON l.id = nl.label_id WHERE nl.note_id = n.id), '')
                FROM notes n WHERE n.deleted_at IS NULL
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Candidate(Guid.ParseExact(r.GetString(0), "D"), Words(r.GetString(1) + " " + r.GetString(2))));
            }
        }

        lock (_gate)
        {
            _candidates = list;
        }

        return list;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _candidates = null;
        }
    }

    /// <summary>
    /// Total distance if every term matches, else -1. A term's words must match consecutive candidate
    /// words (one word for a plain term, the whole run for a phrase).
    /// </summary>
    internal static int FuzzyDistance(Candidate cand, IReadOnlyList<SearchTerm> terms)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var words = Words(term.Text);
            if (words.Length == 0)
            {
                continue;
            }

            var best = int.MaxValue;
            for (var start = 0; start + words.Length <= cand.Words.Length; start++)
            {
                var sum = 0;
                for (var j = 0; j < words.Length && sum != int.MaxValue; j++)
                {
                    var d = WordDistance(words[j], cand.Words[start + j]);
                    sum = d < 0 ? int.MaxValue : sum + d;
                }

                best = Math.Min(best, sum);
                if (best == 0)
                {
                    break;
                }
            }

            if (best == int.MaxValue)
            {
                return -1;
            }

            total += best;
        }

        return total;
    }

    /// <summary>0 for a prefix match, else the typo distance within the length-based budget, else -1.</summary>
    private static int WordDistance(string t, string w)
    {
        if (w.StartsWith(t, StringComparison.Ordinal))
        {
            return 0;
        }

        var max = t.Length >= 8 ? 2 : t.Length >= 4 ? 1 : 0;
        if (max == 0)
        {
            return -1;
        }

        // Compare against the whole word and against its prefix of the term's length (typed so far).
        var d = Math.Min(Damerau(t, w, max), w.Length > t.Length ? Damerau(t, w[..t.Length], max) : int.MaxValue);
        return d <= max ? d : -1;
    }

    /// <summary>Lower-cased, diacritics removed, split on non-letters/digits.</summary>
    internal static string[] Words(string s)
    {
        var folded = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Optimal-string-alignment distance, or <c>max + 1</c> as soon as it must exceed <paramref name="max"/>.</summary>
    internal static int Damerau(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        if (b.Length > 128)
        {
            return max + 1; // no real word is this long; keeps the row buffers on the stack
        }

        // Three rolling rows (i-2, i-1, i) instead of a full matrix: no allocation per call.
        Span<int> prev2 = stackalloc int[b.Length + 1];
        Span<int> prev = stackalloc int[b.Length + 1];
        Span<int> cur = stackalloc int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, prev2[j - 2] + 1);
                }

                cur[j] = v;
                rowMin = Math.Min(rowMin, v);
            }

            if (rowMin > max)
            {
                return max + 1;
            }

            var tmp = prev2;
            prev2 = prev;
            prev = cur;
            cur = tmp;
        }

        return Math.Min(prev[b.Length], max + 1);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_store.Clock.GetLocalNow().DateTime);
}
