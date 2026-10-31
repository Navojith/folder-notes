using System.Diagnostics;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Search;
using FolderNotes.Core.Tests.Storage;

namespace FolderNotes.Core.Tests.Search;

/// <summary>
/// REQ-051 / NFR-05: search returns in under 100 ms at 10,000 notes. A deterministic seeded DB;
/// each query is warmed up and then measured 20 times. The p95 must be under the budget.
/// </summary>
[Trait("Category", "Performance")]
public sealed class SearchBenchmarkTests(SearchBenchmarkTests.SeededDb db, ITestOutputHelper output) : IClassFixture<SearchBenchmarkTests.SeededDb>
{
    private const double BudgetMs = 100;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>10,000 deterministic notes, seeded once for the whole class.</summary>
    public sealed class SeededDb : IAsyncLifetime
    {
        public const int NoteCount = 10_000;

        private static readonly string[] s_words =
        [
            "client", "invoice", "contract", "archive", "backup", "photos", "family", "tax", "project", "build",
            "release", "draft", "report", "budget", "design", "meeting", "travel", "school", "medical", "insurance",
            "receipts", "music", "video", "source", "output", "cache", "legacy", "migration", "review", "retention",
            "acme", "globex", "initech", "umbrella", "stark", "wayne", "hooli", "vandelay", "pied", "piper",
        ];

        private StoreFixture _f = null!;

        public SearchService Search { get; private set; } = null!;
        public long SeedMs { get; private set; }

        public async ValueTask InitializeAsync()
        {
            _f = await StoreFixture.CreateAsync();
            Search = new SearchService(_f.Store);
            var rnd = new Random(42);
            string Words(int n) => string.Join(' ', Enumerable.Range(0, n).Select(_ => s_words[rnd.Next(s_words.Length)]));
            var labelNames = Enumerable.Range(0, 20).Select(i => $"{s_words[i]}-{i}").ToArray();
            var statuses = Enum.GetValues<NoteStatus>();

            var sw = Stopwatch.StartNew();
            await _f.Store.WriteAsync(ctx =>
            {
                for (var i = 0; i < NoteCount; i++)
                {
                    var path = $@"D:\{s_words[rnd.Next(10)]}\{s_words[rnd.Next(40)]}-{i}\{Words(1)}";
                    var note = _f.NewNote(path, $"{Words(3)} {i}") with
                    {
                        BodyMd = Words(rnd.Next(20, 300)),
                        Status = statuses[rnd.Next(statuses.Length)],
                        Labels = [.. Enumerable.Range(0, rnd.Next(0, 4)).Select(_ => Label.Create(labelNames[rnd.Next(labelNames.Length)], "blue")).DistinctBy(l => l.Name)],
                        ReviewOn = rnd.Next(4) == 0 ? new DateOnly(2026, 1, 1).AddDays(rnd.Next(730)) : null,
                        KeepUntil = rnd.Next(4) == 0 ? new DateOnly(2026, 1, 1).AddDays(rnd.Next(1500)) : null,
                    };
                    _f.Store.UpsertCore(ctx, note, preserveTimestamps: false);
                }

                return 0;
            });
            SeedMs = sw.ElapsedMilliseconds;
        }

        public async ValueTask DisposeAsync()
        {
            Search.Dispose();
            await _f.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("ac")]
    [InlineData("acme invoice")]
    [InlineData("\"tax receipts\"")]
    [InlineData("client label:client-0")]
    [InlineData(@"report path:D:\client status:keep")]
    [InlineData("due:30d")]
    [InlineData("keep:<2027 archive")]
    [InlineData("retnetion")] // typo: goes through the fuzzy fallback
    [InlineData("vandelay globex initech umbrella")]
    public async Task REQ051_Search_Under100ms_At10kNotes(string query)
    {
        for (var i = 0; i < 3; i++)
        {
            await db.Search.SearchAsync(query, 50, Ct); // warm-up (also fills the fuzzy cache)
        }

        var times = new List<double>();
        IReadOnlyList<SearchHit> hits = [];
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            hits = await db.Search.SearchAsync(query, 50, Ct);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var p50 = times[times.Count / 2];
        var p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        var line = $"seed {db.SeedMs} ms | '{query}': {hits.Count} hits, p50 {p50:F1} ms, p95 {p95:F1} ms, max {times[^1]:F1} ms";
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "perf-search.log"), line + Environment.NewLine);

        hits.ShouldNotBeEmpty();
        p95.ShouldBeLessThan(BudgetMs);
    }
}
