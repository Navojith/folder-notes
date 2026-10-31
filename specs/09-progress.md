# 09 — Progress

The status page for the project. `07-tasks.md` is the backlog (what to do and when it counts as
done). This file is the record of **where we are**. Update it at the end of every task or work
session (see `CLAUDE.md`, workflow step 7).

**Last updated:** 2026-10-31 · **Current phase:** Phase 2 (App shell) · **Next task:** T2.1 WinUI 3 app skeleton

---

## Phase overview

| Phase | Goal | Status | Tasks done | Exit criteria met |
|---|---|---|---|---|
| 0 | De-risk Explorer integration (spikes) | ✅ Done | 4 / 4 | ✅ 2026-10-31 |
| 1 | Core library (no UI) | ✅ Done | 7 / 7 | ✅ 2026-10-31 (coverage 93.0%) |
| 2 | App shell, editor, search | 🟡 Next | 0 / 9 | — |
| 3 | Context menu + companion panel (**MVP**) | ⚪ Not started | 0 / 5 | — |
| 4 | Hover tooltip + Win11 top-level menu | ⚪ Not started | 0 / 6 | — |
| 5 | Reminders, portability, polish | ⚪ Not started | 0 / 5 | — |
| 6 | Ship | ⚪ Not started | 0 / 3 | — |

Legend: ✅ done · 🟡 in progress · ⛔ blocked · ⚪ not started

## Task status

| Task | Status | Date | Commit | Notes |
|---|---|---|---|---|
| S1 Explorer tracker spike | ✅ | 2026-10-31 | `7a38a57` | 39/39 scenarios, tab switch ≤117 ms |
| S2 Docked panel spike | ✅ | 2026-10-31 | `7a38a57` | 13/13 checks, 0 px edge error, 14 ms median |
| S3 Infotip spike | ✅ | 2026-10-31 | `48a04aa` | Works on Win11 in Details/List/icon views |
| S4 Folder identity spike | ✅ | 2026-10-31 | `7a38a57` | 14/14 checks on NTFS; led to ADR-009 |
| T1.1 Solution scaffold | ✅ | 2026-10-31 | `5f0086a` | Local build + test green; CI not run yet (no remote) |
| T1.2 Domain model + validation | ✅ | 2026-10-31 | `051f46a` | 13 rule codes in 01 § Validation rules; lengths count Unicode chars |
| T1.3 Path normalization + shared vectors | ✅ | 2026-10-31 | `db1c4a0` | 57 vectors, rules in 03 § String normalization |
| T1.4 FolderIdentity service | ✅ | 2026-10-31 | `a015c2d` | 18 real-folder tests; IDs only on NTFS/ReFS; Recycle Bin detected |
| T1.5 SQLite schema + repository | ✅ | 2026-10-31 | `89202d6` | ADR-009 lookup + OrphanScanner; ADR-010 (FTS content), ADR-011 (writer) |
| T1.6 Search | ✅ | 2026-10-31 | `c0b2edf` | Grammar in 03; 10k benchmark p95 ≤54 ms |
| T1.7 Export / import JSON | ✅ | 2026-10-31 | `eb44b8e` | Lossless + byte-identical re-export |
| T2.1 WinUI 3 app skeleton | ⚪ next | | | Also adds App.Tests (deferred from T1.1) |

Add a row when a task starts. Phases 2–6 are listed in `07-tasks.md` until work on them begins.

## Verified measurements (against targets)

| Requirement | Target | Measured | Where | Status |
|---|---|---|---|---|
| REQ-031 panel update on folder change | ≤150 ms | navigate 49 ms median / 107 ms max; tab switch 100 / 117 ms | S1 | ✅ (spike) |
| REQ-033 panel follows Explorer | no visible lag | 14 ms median / 34 ms max, 0 px | S2 | ✅ (spike) |
| REQ-041 tooltip lookup | <20 ms | 0.13 ms median stub (58 ms first load) | S3 | ✅ (stub, no SQLite yet) |
| REQ-060 rename/move tracking | survives | rename, move, ancestor rename all keep the ID | S4 | ✅ NTFS |
| Tracker evaluation cost | <10 ms (from S1) | 34 ms avg with late binding | S1 | ❌ must fix in T3.2 (typed interop + cache) |
| REQ-051 search | <100 ms @ 10k | p95 8–54 ms over 10 query shapes (worst `due:30d`; typo fallback 30 ms), Debug build | T1.6 | ✅ |
| Upsert at 10k notes | (no target) | 10k upserts in one transaction ≈ 4 s (≈0.4 ms each) | T1.6 | ✅ |
| REQ-060 `Resolve` cost | <5 ms (test bound) | passes; S4 measured 0.01 ms for the ID read | T1.4 | ✅ |
| Phase 1 exit: Core coverage | ≥85% | 93.0% line / 86.6% branch (362 tests) | Phase 1 | ✅ |
| NFR-02 cold start | <1.5 s | — | T5.5 | ⚪ |

## Open items to verify

Collected from the specs so nothing gets lost. Close an item by recording the result here and in the source spec.

| # | Item | Source | Resolve in | Status |
|---|---|---|---|---|
| O-1 | Does `GetInfoTip` returning `E_FAIL` restore Explorer's default tooltip? (REQ-041 assumes it does) | 04 § S3 | T4.3 (first step) | open |
| O-2 | Does Explorer cache tooltips so edits don't show? If so, send `SHChangeNotify(SHCNE_UPDATEITEM)` | 04 § S3 | T4.3 | open |
| O-3 | Tooltip in Tiles / Content views, nav pane, Desktop | 04 § S3 | T4.5 manual | open |
| O-4 | Panel smoothness during an interactive drag (`S2Dock.exe watch`) | 04 § S2 | T3.5 manual | open |
| O-5 | Mixed-DPI multi-monitor docking | 04 § S2 | T3.5 manual | open (one monitor here) |
| O-6 | Windows 10 22H2: tracker, panel, tooltip | 04 | T3.5 / T4.5 | open (no Win10 machine) |
| O-7 | Folder identity on exFAT/FAT32, ReFS, network shares, OneDrive Files-On-Demand, cross-volume moves | 03 § S4 | manual, before T6.3 | open: code is defensive (IDs only on NTFS/ReFS, path fallback elsewhere) but there's no hardware to test on |
| O-9 | Path sort is ordinal on `path_norm`, so `D:\ClientsOld` sorts before `D:\Clients\Acme`. Should the library sort hierarchically? | 03 / REQ-054 | T2.4 | open |
| O-10 | Run BenchmarkDotNet on Release (06 § Performance); T1.6 used an xunit timing test instead | 06 | T5.5 | open |
| O-11 | `NoteChanged` handlers run on the writer task (ADR-011); the app must marshal them to the UI thread | 02 / ADR-011 | T2.1 | open |
| O-8 | CI workflow actually runs green on GitHub `windows-latest` | 07 T1.1 | when a remote exists | open |

## Blockers

| Since | Blocker | Needed from | Status |
|---|---|---|---|
| — | none | | |

Resolved: MSVC toolset missing (2026-10-31, fixed by installing the C++ workload) · .NET 10 SDK
missing (2026-10-31, 10.0.403 installed).

## Environment

| Item | Value |
|---|---|
| Dev machine | Windows 11 23H2 (22631), 1 monitor 1536×864 logical @ 125%, NTFS C: only |
| .NET | SDK 10.0.403 (pinned), 9.0.300 also installed (spikes) |
| Windows App SDK | 2.5.1 |
| C++ | VS 2022 Community 17.14, MSVC 14.44.35207, Windows SDK 10.0.26100, bundled CMake + Ninja |
| Tests | xunit.v3 4.0.2 on Microsoft Testing Platform, Shouldly 4.3.0 |
| Repo | local git, branch `main`, **no remote** |

## Log

Newest first. One entry per session: what changed, what was learned, what's next.

### 2026-10-31 (Phase 1)
- **Phase 1 complete:** T1.2–T1.7 done, one commit per task. Core has the domain model and
  validation, the path normalizer with 57 shared vectors, `FolderIdentityService`,
  `SqliteNoteStore` (single writer, labels, ADR-009 lookup, migrations with backup),
  `OrphanScanner`, search (parser + FTS5 + typo fallback), and JSON export/import.
  362 tests are green (Debug + Release). Core coverage is 93.0%.
- **Spec changes made first:** validation rule codes (01), the string normalization rules, the hex
  ID format, storage conventions, the search grammar and the export format (03), interface
  refinements (02: `LocateResult`, `ILabelRepository`, relink/orphan/purge). Two new ADRs: ADR-010
  (the FTS5 table stores its own content instead of `content=''`) and ADR-011 (the writer model).
- **Learned:** the `path_norm = … OR (file_id …)` conflict check made SQLite scan the whole table
  on every upsert (10k-note seeding took minutes). Splitting it into two arms that each use an
  index fixed it (≈4 s). The typo fallback dropped from 80 ms to 30 ms p95 once the distance
  function stopped allocating per call. Fuzzy phrase matching first matched the words in any
  order, and now needs them consecutive.
- **Next:** T2.1 WinUI 3 app skeleton (plus App.Tests). Start with O-11 (marshalling `NoteChanged` to the UI thread).

### 2026-10-31 (Plan + Phase 0)
- **Plan:** wrote specs 00–08 and `CLAUDE.md` (spec-driven workflow). `c8d2fe1`
- **Phase 0 complete:** S1–S4 all done. Findings went into specs 03/04; ADRs 004, 006, 007, 008
  and 009 accepted; ADR-001 amended to .NET 10 + Windows App SDK 2.x. Main lessons: a
  PerMonitorV2 manifest is required, no `dynamic` COM, deleting and recreating a folder changes
  its ID, the infotip replaces the default tooltip, and Explorer needs a restart after
  (un)registering the handler. `7a38a57`, `48a04aa`
- **T1.1 done:** solution scaffold, CI workflow, README. FluentAssertions was swapped for
  Shouldly (licence). `5f0086a`
- **Next:** T1.2 domain model + validation.
