# 09 — Progress

The status page for the project. `07-tasks.md` is the backlog (what to do and when it counts as
done). This file is the record of **where we are**. Update it at the end of every task or work
session (see `CLAUDE.md`, workflow step 7).

**Last updated:** 2026-10-31 · **Current phase:** Phase 2 (App shell) · **Next task:** T2.4 Library view (after the O-15 decision)

---

## Phase overview

| Phase | Goal | Status | Tasks done | Exit criteria met |
|---|---|---|---|---|
| 0 | De-risk Explorer integration (spikes) | ✅ Done | 4 / 4 | ✅ 2026-10-31 |
| 1 | Core library (no UI) | ✅ Done | 7 / 7 | ✅ 2026-10-31 (coverage 93.0%) |
| 2 | App shell, editor, search | 🟡 In progress | 3 / 9 | — |
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
| T2.1 WinUI 3 app skeleton | ✅ | 2026-10-31 | `38116ec` | App.Tests (47); ADR-012 tray; O-11 closed by `UiNoteEvents` |
| T2.2 Theme + design tokens | ✅ | 2026-10-31 | `7e3c22f` | ADR-013; 12 colours AA-checked by test; real icon; HC visual check → O-14 |
| T2.3 Note editor + compact window | ✅ | 2026-10-31 | _pending_ | ADR-014/015; e2e via UIA on Release; REQ-080 gap → O-15 |
| T2.4 Library view | ⚪ next | | | Waits on the O-15 decision; the main window needs ADR-015-style reuse |

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
| REQ-080 idle RAM (private working set) | <60 MB | 31.7 MB tray-only; 54.2 MB after the main window was opened and closed (private bytes 46.8 / 70.7 MB), Release | T2.1 | ✅ |
| Redirected second launch | (no target) | process exits ≈120 ms after start | T2.1 | ✅ |
| REQ-005 autosave on kill | lose ≤500 ms | body typed 850 ms before kill kept; title typed 50 ms before kill lost | T2.3 | ✅ |
| REQ-080 idle RAM after using the editor | <60 MB | 65 MB private working set (83 MB private bytes), flat over 8 open/close cycles after ADR-015; was +4 MB per cycle before | T2.3 | ❌ O-15 |
| REQ-080 idle RAM after main window (token sample) | <60 MB | 60–68 MB, levels off by cycle 4 | T2.3 | ❌ O-15 |
| NFR-02 cold start | <1.5 s | 0.53–0.87 s from process start to tray + router ready (Release, warm disk, skeleton only) | T2.1 / T5.5 | 🟡 re-measure at T5.5 |

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
| O-11 | `NoteChanged` handlers run on the writer task (ADR-011); the app must marshal them to the UI thread | 02 / ADR-011 | T2.1 | **closed** 2026-10-31: `UiNoteEvents` relays via `IUiDispatcher`; UI code subscribes there (02 § App shell) |
| O-12 | Tray icon comes back after an Explorer restart (`TaskbarCreated`), and has the right size at 150% / mixed DPI | 02 § App shell / ADR-012 | T3.5 manual | open: code path written, not exercised (didn't restart the user's Explorer) |
| O-13 | Memory after the window closes stays ~23 MB above tray-only (XAML stays loaded). Under target now; recheck once real pages exist | REQ-080 | T5.5 | open |
| O-14 | High-contrast look of chips, pills and windows (tokens map to system colours; tests check the file, but nobody has seen it on screen) and a live HC toggle re-rendering open chips | 05 § Design tokens | T2.9 manual | open: switching HC changes the user's whole desktop theme, so T2.2 didn't do it |
| O-15 | REQ-080 is over target once a window has been used: ≈65 MB private working set (≈83 MB private bytes) vs <60 MB, the one-time cost of loading WinUI's controls. An idle `EmptyWorkingSet` would show 2.7 MB in Task Manager, but commit stays at 83 MB. Options: trim the working set when idle, change the target or metric, or host windows in a child process | REQ-080 / ADR-015 | before T2.4 | **open: needs a decision from the user** |
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

### 2026-10-31 (T2.3)
- **T2.3 done:** a compact editor window for `--edit` and `--open-note`. `NoteEditorViewModel`
  covers drafts (saved on the first edit), the 500 ms debounced autosave, flush on close (invalid
  fields go back to their last saved value), delete with a 10 s Undo, and label suggestions with
  inline create. Also new: Markdown preview through an app-owned model (Markdig) and a
  `RichTextBlock` renderer. CommunityToolkit.Mvvm 8.4.2 and Markdig 1.4.0 added. 541/541 tests
  green in Release (App.Tests 131 → 179).
- **Spec changes made first:** 05 § Editor behaviour, 02 § Editor and § Markdown, a T2.3
  *Done when* in 07, ADR-014. Added during the task: ADR-015 and 02's window-reuse and idle-trim
  notes.
- **Learned (live runs):**
  - WinUI's TextBox reports bare CR line breaks, which would have reached the database.
  - Destroying a WinUI window leaks about 4 MB of native memory per cycle, even with no UI
    Automation attached and after a forced GC, so editor windows are now reused (ADR-015).
  - Using any real window costs about 33 MB once, which puts REQ-080 over target (O-15).
  - The starter labels (REQ-013) are already seeded by `Schema.cs`.
- **Test residue on this machine:** two soft-deleted "Client X" notes (purged later) and a label
  `e2e-label` in the dev database. The label can be deleted from the Labels page once T2.5 exists.
- **Next:** decide O-15, then T2.4 library view.

### 2026-10-31 (T2.2)
- **T2.2 done:** `Themes/Tokens.xaml` holds every colour. For each of the 12 palette keys there's
  an accent, a chip fill and chip text, in Light/Dark/HC, plus the status icon geometry (Fluent
  System Icons 20 px, MIT) and spacing/shape. Also added: `ThemeTokens` (lookup for run-time
  keys), `StatusVisuals`, `ThemeService` (System/Light/Dark applied to registered windows and
  title bars, HC tracking), the `LabelChip` / `StatusPill` controls, and a token sample on the
  placeholder window. New icon drawn by `eng/make-icon.ps1` (8 sizes, PNG-in-ICO).
  493/493 tests green in Release (App.Tests 47 → 131).
- **Spec changes made first:** 05 § Design tokens (keys, how the colours are derived, contrast
  rules, HC mapping, theme, backdrops, spacing), a T2.2 *Done when* in 07, and ADR-013.
- **Learned (both found only by running the app):** `AccessibilitySettings.HighContrastChanged`
  throws `0x80070490` in a desktop app (it needs a CoreWindow), and it took startup down with it.
  Now HC state comes from `SPI_GETHIGHCONTRAST`, changes come from `UISettings.ColorValuesChanged`,
  and a failed subscription is logged instead of fatal. Separately, `{StaticResource SpacingXL}`
  (a Double) can't go into `Padding` (a Thickness); the XAML compiler doesn't catch that, and it
  only fails when the window loads.
- **Next:** T2.3 note editor page + compact editor window (use `LabelChip`/`StatusPill` and the tokens).

### 2026-10-31 (T2.1)
- **T2.1 done:** custom `Program.Main` with `AppInstance` single-instance redirection,
  `ActivationParser` + `CommandRouter`, `AppShell` (main window created on demand, destroyed on
  close, `OnExplicitShutdown`), `TrayIcon` via `Shell_NotifyIcon` (ADR-012), MS DI, Serilog
  rolling file logs, `UiNoteEvents` (O-11), a placeholder icon. New `FolderNotes.App.Tests`
  (47 tests); 409/409 green in Release.
- **Spec changes made first:** 02 gained "Resolved in T2.1" (no args shows the window, parsing
  rules) and § App shell; ADR-012; REQ-080 now says which RAM metric counts.
- **Learned:** drive roots break naive quoting: `--edit "D:\"` arrives as `D:"`, and the string
  `AppInstance` forwards also ends in a space. The parser repairs both (found only by running it
  for real). A test project that references the WinUI exe needs the WinAppSDK auto-initializers
  turned off, or the generated types collide through `InternalsVisibleTo`.
- **Next:** T2.2 theme + design tokens (and the real icon).

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
