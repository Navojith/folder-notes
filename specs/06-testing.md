# 06 — Testing Strategy

Rule: every **M** requirement has at least one automated test or a documented manual check
(listed in `tests/manual-checklist.md`) before its phase can exit. Test names include the
requirement ID, e.g. `REQ060_Rename_KeepsNoteAttached`.

## Layers

| Layer | Tooling | Covers |
|---|---|---|
| Core unit | xunit.v3 + Shouldly, in-memory/temp-file SQLite | repository, validation (REQ-002), labels, search parsing & ranking (REQ-051/052/023), path normalization (REQ-063), migrations |
| Identity integration | xunit.v3 on real temp folders (NTFS), plus a fake identity for what one disk can't show (another volume at the same letter, an unmounted volume) | rename/move tracking (REQ-060), orphan detection and relink rules (REQ-061, 03 § Orphan detection / § Relink rules, ADR-020): deleted, moved, recreated, Recycle Bin, drive unavailable, same-named folders, conflicts, concurrent changes, content unchanged |
| App services | xunit.v3 + fakes | ReminderService due/snooze/once logic (REQ-021/024), autosave debounce (REQ-005), undo (REQ-006), single-instance arg routing |
| Explorer tracker | Integration tests driving real Explorer (`explorer.exe <path>`, `IShellWindows` navigate) — run locally and on a Windows CI runner with a desktop session | REQ-031 latency, REQ-034 tabs, virtual folders → null |
| Shell extension | GoogleTest on the DLL's pure functions (path normalize, tooltip formatting, query) + a harness that `CoCreateInstance`s the handler out of Explorer and measures `GetInfoTip` latency | REQ-040/041/042 |
| UI smoke | FlaUI | add note via `--edit`, search & open, panel shows note |
| Performance | BenchmarkDotNet + a seeded DB of 10k notes | REQ-041 (<20 ms), REQ-051 (<100 ms), NFR-05 |
| Resources | `eng/perf-check.ps1` against a Release build (below) | REQ-080 budgets, NFR-06 stability and idle CPU |

## Resource budgets (REQ-080, NFR-06)

`eng/perf-check.ps1` is the repeatable regression check (ADR-016). It starts a fresh tray process
and records **private working set and private bytes separately** at each stage:

| Stage | Measured | Budget |
|---|---|---|
| Tray-only, 12 s after start | working set, private bytes, idle CPU over 60 s | WS < 50 MB, CPU < 0.1 % |
| Editor open (each of 8 cycles, 2 s after it appears) | working set, private bytes, time to window | WS < 100 MB |
| Editor closed (each cycle, 10 s after close) | working set, private bytes | WS < 100 MB |
| Main window (8 cycles since T2.4: open on All notes, 2 s; closed, 10 s) | working set, private bytes, time to window | WS < 100 MB, growth ≤ 4 MB |
| Search palette (8 cycles since T2.6: `--search`, 2 s; closed, 10 s) | working set, private bytes, time to window | WS < 100 MB, growth ≤ 4 MB |
| Across cycles | private bytes after close, cycle 2 → last | growth ≤ 4 MB (8 cycles), ≤ 8 MB (soak) |
| After use | idle CPU over 60 s | < 0.1 % |
| Panel active *(added in T3.5)* | working set, private bytes with Explorer open on a noted folder | WS < 110 MB |
| Explorer navigation *(added in T3.5)* | CPU and tracker cost during scripted navigation; REQ-031 latency; idle CPU with Explorer open | < 10 ms per evaluation, ≤ 150 ms, < 0.1 % |

- The editor is opened with `--edit` on an empty scratch folder and closed without typing, so the
  check leaves nothing in the notes database (drafts are only saved on the first edit, ADR-014).
- **When to run:** before ticking any task that adds or changes a window or a long-lived service
  (record the numbers in 09), at T3.5 (with the panel stages), and at T5.5 / T6.3.
- **Extended session (soak):** `-Cycles 120 -GrowthBudgetMB 8` (about 30 minutes), at T5.5 and
  before each release.
- Not allowed as a way to meet a budget: working-set trimming (`EmptyWorkingSet`,
  `SetProcessWorkingSetSize`) or moving UI into another process (ADR-016).

## Shared test vectors

`tests/shared/path-normalization-vectors.json` holds input → expected `path_norm` pairs. **Both**
the C# and C++ normalizers must pass every vector. This keeps the app and the shell extension
agreeing on lookups.

## Explorer stability (NFR-01)

- **Stress test:** a script hovers 1,000 folders through UI Automation while the app writes notes
  in a loop. Pass criteria: no Explorer crash, every `GetInfoTip` under 50 ms, and no DLL leaks
  after `DllCanUnloadNow`.
- **DB missing / locked / corrupt:** the handler returns `E_FAIL` quickly in each case.
- Run the handler under Application Verifier (heaps, handles, locks) once per release.

## Manual checklist (maintained in `tests/manual-checklist.md`)

Windows 10 22H2, Windows 11 23H2, Windows 11 24H2 × {100%, 150%, mixed-DPI multi-monitor} ×
{light, dark, high contrast}: panel docking, snapping, maximize, tabs, toasts, context menu tiers.
