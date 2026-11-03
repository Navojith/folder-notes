# 09 — Progress

The status page for the project. `07-tasks.md` is the backlog (what to do and when it counts as
done). This file is the record of **where we are**. Update it at the end of every task or work
session (see `CLAUDE.md`, workflow step 7).

**Last updated:** 2026-11-03 · **Current phase:** Phase 2 (App shell) · **Next task:** T3.2.5 (VM harness changes, then the VM runs). T3.2.6 frozen-server quarantine is done (04 § M). T3.2.5 stays PARTIAL until the VM runs (`specs/reports/T3.2.5-vm-test-plan.md`; blocked on disk space), the performance re-measurements and the manual checks. Report: `specs/reports/T3.2.5-verification.md`

---

## Phase overview

| Phase | Goal | Status | Tasks done | Exit criteria met |
|---|---|---|---|---|
| 0 | De-risk Explorer integration (spikes) | ✅ Done | 4 / 4 | ✅ 2026-10-31 |
| 1 | Core library (no UI) | ✅ Done | 7 / 7 | ✅ 2026-10-31 (coverage 93.0%) |
| 2 | App shell, editor, search | 🟡 In progress | 8 / 9 | — |
| 3 | Context menu + companion panel (**MVP**) | 🟡 In progress | 1 / 5 | — |
| 4 | Hover tooltip + Win11 top-level menu | ⚪ Not started | 0 / 6 | — |
| 5 | Reminders, portability, polish | ⚪ Not started | 0 / 7 | — |
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
| T2.3 Note editor + compact window | ✅ | 2026-10-31 | `bdcdb4b` | ADR-014/015; e2e via UIA on Release; REQ-080 gap → O-15 |
| T2.4 Library view | ✅ | 2026-11-01 | `0453133` | ADR-017; main window reused; O-9 and O-17 closed; live UIA check + `perf-check` on Release |
| T2.5 Labels manager | ✅ | 2026-11-01 | `a5a4487` | ADR-018; REQ-012 "Settings → Labels" resolved as its own page; live UIA check + `perf-check` |
| T2.6 Search palette + global hotkey | ✅ | 2026-11-01 | `5a26e3f` | ADR-019; "windows used" budget 90 → 100 MB (user, ADR-016 amendment); live UIA check with the real hotkey |
| T2.7 Orphaned view + relink | ✅ | 2026-11-01 | `1665fe1` | ADR-020 (integrity rules), ADR-021 (pages built in code; fixes a T2.6 crash); live UIA check with the real folder picker + `perf-check` |
| T2.8 Settings page | ✅ | 2026-11-01 | `e2ce199` | ADR-022 (settings in the DB; Run entry is the truth, installer defaults it on); live UIA check with real key presses + `perf-check` |
| T2.9 Accessibility pass | 🟡 partial | 2026-11-01 | `f3aeffa` | Static UIA-name test added (324 App tests green); Accessibility Insights, screen reader and HC passes still manual (O-14) |

| T3.2.6 Frozen-server quarantine | ✅ | 2026-11-03 | spec `5f0e050`, code: this commit | `CallJournal` (sequence lock, per-operation target pid, cancel HRESULT kept); `QuarantineRecord` (suspects by pid + creation time, E1/E2, 10 s hold / 30 s minimum, one automatic trial per server); state machine `Quarantined` (Waiting/Trial), Q1–Q24; monitor on the watchdog thread (no new thread); `Win32ServerProbe`; no replacement on a wedge; hang pre-screen; `Availability` + stats. App 619 → 701 tests (687 run, 14 gated), Core 396; timing classes 3× green. Real check: VM stage D |
| T3.2.5 Real-Explorer verification | 🟡 partial | 2026-11-03 | this commit | Harness + 14 gated tests (`Explorer/Real`); path resolution, tabs, windows, panel focus, latency (p95 52/27/22 ms), lost events (≤ 2 s), hook scoping all PASS; **shell crashed during churn test (O-26)**; **CoCancelCall ineffective on a frozen real Explorer (O-25)**; BS-character guard bug fixed. Not ticked. Report: `specs/reports/T3.2.5-verification.md` |
| T3.2.4 ExplorerTracker composition | ✅ | 2026-11-03 | this commit | `TrackerEngine` (evaluation, cache, recovery; no threads), `ExplorerTracker` (composition), `Win32Shell`, `TrackerStats`, DI extension (not started, not called by `App`); watchdog cancel latency measured and logged (probe P6); App 561 → 605. Spec 04 § L |
| T3.2.3 COM layer and thread hosts | ✅ | 2026-11-03 | `16d4f2b` (steps 1-2) + this commit | Q1 (cap 6, `ThreadCap` permanent, process-wide budget), Q2 (`SignalRouter`, `HookThread`), typed COM interfaces, `ComRef`, `ComShellWindowsGateway`, STA `StaWorkerHost`, `WatchdogThread`, `WorkerSupervisor`; App 480 → 561, Core 396. Interpretations in 04 § K |
| T3.2.2 Tracker pure logic | ✅ | 2026-11-01 | this commit | `TabMap`, `EvaluationScheduler`, `ComFailureClassifier`, `TrackerStateMachine` (+ publisher, watchdog, resolvers, root validator); 146 new tests, App 334 → 480, Core 396; no COM/Win32 (guard test); 4 mutation checks caught. Spec 04 § I records the interpretations; Q1/Q2 open |
| T3.2.1 Tracker probes (S5) | ✅ | 2026-11-01 | this commit | P1 interop validated vs SDK headers, P2 `CoCancelCall` works on an out-of-process proxy, P3 one path route (0.36 ms), P5 scoped hooks remove foreign callbacks; P4 deferred (D5). Spec corrected (release API, `Gone` via `IsWindow`, `LocationURL` dropped) |
| T3.1 Context menu verbs | ✅ | 2026-11-01 | `2772509` + this commit | ADR-024: installer-only registration, manual toggle + Repair in Settings, never automatic. Both live checks passed: Settings card by UIA (Off/On/Stale/Repair/forced failure) and the real Explorer menu (folder and background verbs, special-character path, unregister removes both) |

Add a row when a task starts. Phases 2–6 are listed in `07-tasks.md` until work on them begins.

## Verified measurements (against targets)

| Requirement | Target | Measured | Where | Status |
|---|---|---|---|---|
| REQ-031 panel update on folder change | ≤150 ms | navigate 49 ms median / 107 ms max; tab switch 100 / 117 ms | S1 | ✅ (spike) |
| REQ-033 panel follows Explorer | no visible lag | 14 ms median / 34 ms max, 0 px | S2 | ✅ (spike) |
| REQ-041 tooltip lookup | <20 ms | 0.13 ms median stub (58 ms first load) | S3 | ✅ (stub, no SQLite yet) |
| REQ-060 rename/move tracking | survives | rename, move, ancestor rename all keep the ID | S4 | ✅ NTFS |
| Tracker evaluation cost | <10 ms (from S1) | 34 ms avg with late binding (S1). Typed probe: path read 0.36 ms median, full enumeration of all tabs 0.37 ms | S1 / S5 | 🟡 components well under target; end-to-end re-measure in T3.2.5 |
| REQ-051 search | <100 ms @ 10k | p95 8–54 ms over 10 query shapes (worst `due:30d`; typo fallback 30 ms), Debug build | T1.6 | ✅ |
| REQ-051 search, Release | <100 ms @ 10k | p95 8.7–38.3 ms over the same 10 shapes (worst `due:30d`) | T2.6 | ✅ |
| REQ-050 hotkey → palette | (no target) | ≈200 ms from the key press to the palette visible and focused (first open 243 ms, later 110–130 ms window time) | T2.6 | ✅ |
| Upsert at 10k notes | (no target) | 10k upserts in one transaction ≈ 4 s (≈0.4 ms each) | T1.6 | ✅ |
| REQ-060 `Resolve` cost | <5 ms (test bound) | passes; S4 measured 0.01 ms for the ID read | T1.4 | ✅ |
| Phase 1 exit: Core coverage | ≥85% | 93.0% line / 86.6% branch (362 tests) | Phase 1 | ✅ |
| REQ-080 idle RAM (private working set) | <60 MB (old target) | 31.7 MB tray-only; 54.2 MB after the main window was opened and closed (private bytes 46.8 / 70.7 MB), Release | T2.1 | ✅ |
| Redirected second launch | (no target) | process exits ≈120 ms after start | T2.1 | ✅ |
| REQ-005 autosave on kill | lose ≤500 ms | body typed 850 ms before kill kept; title typed 50 ms before kill lost | T2.3 | ✅ |
| REQ-080 idle RAM after using the editor | <60 MB (old target) | 65 MB private working set (83 MB private bytes), flat over 8 open/close cycles after ADR-015; was +4 MB per cycle before | T2.3 | superseded by ADR-016 budgets |
| REQ-080 idle RAM after main window (token sample) | <60 MB (old target) | 60–68 MB, levels off by cycle 4 | T2.3 | superseded by ADR-016 budgets |
| REQ-080 tray-only (private working set) | <50 MB | 32.2 MB (47.3 MB private bytes) | `perf-check` 2026-11-01 | ✅ |
| REQ-080 editor open | <90 MB | max 66.6 MB (83.6 MB private bytes) over 8 cycles; window in 110–230 ms when reused, 501 ms first time | `perf-check` 2026-11-01 | ✅ |
| REQ-080 after the editor closed | <90 MB | max 67.1 MB (83.9 MB private bytes) | `perf-check` 2026-11-01 | ✅ |
| REQ-080 panel active | <110 MB | not built yet | T3.5 | ⚪ |
| NFR-06 growth over 8 cycles (private bytes) | ≤4 MB | +0.4 MB, cycle 2 → 8 | `perf-check` 2026-11-01 | ✅ |
| NFR-06 idle CPU | <0.1 % of one core | 0 % tray-only; 0.052 % after use (60 s windows) | `perf-check` 2026-11-01 | ✅ |
| NFR-06 soak (120 cycles) | ≤8 MB growth | not run yet | T5.5 | ⚪ |
| REQ-080 main window used (library, 8 notes) | <90 MB | max 81.8 MB open, 81.4 MB after close (≈100 MB private bytes); reopen 109–118 ms, first 242 ms | `perf-check` T2.4 | ✅ (8 MB headroom) |
| NFR-06 growth, 8 main-window cycles | ≤4 MB | -2.8 MB | `perf-check` T2.4 | ✅ |
| NFR-06 idle CPU after using the library | <0.1 % | 0 % | `perf-check` T2.4 | ✅ |
| REQ-080 all windows used (editor + main + palette) | <100 MB (was 90) | max 94.2 MB open, 93.3 MB after close (≈116 MB private bytes); the palette adds ≈10 MB once | `perf-check` T2.6 | ✅ |
| NFR-06 growth, 8 palette cycles | ≤4 MB | +3.3 MB by the rule (cycle 2's after-close reading dipped to 87.9 MB); open readings flat at 92–94 MB, so no growth | `perf-check` T2.6 | ✅ (watch) |
| NFR-06 growth, 8 palette cycles (re-run) | ≤4 MB | +0.5 MB; open readings flat at 77–79 MB. The T2.6 +3.3 MB didn't reproduce | `perf-check` T2.7 | ✅ (still watched) |
| REQ-080 all windows used (re-run) | <100 MB | max 78.8 MB open, 78.5 MB closed (≈98 MB private bytes); tray-only 34 MB; editor ≤ 68.4 MB; main ≤ 77 MB. About 15 MB under T2.6's 94.2 MB; not investigated (no change aimed at memory), so treat it as run-to-run variation until O-16 re-baselines | `perf-check` T2.7 | ✅ |
| REQ-080 main window, Orphaned page visited | <100 MB | 73.2 MB vs 70.5 MB on All notes only (+2.7 MB once); 77.4 MB with Labels visited too | T2.7 (manual) | ✅ |
| REQ-080 main window, Settings page visited | <100 MB | 74.7 MB vs 71.3 MB on All notes only (+3.4 MB once) | T2.8 (manual) | ✅ |
| NFR-06 growth, 8 palette cycles (T2.8 run) | ≤4 MB | -0.1 MB; open readings flat at 77–78.5 MB | `perf-check` T2.8 | ✅ (+3.3 MB of T2.6 not seen again in two runs) |
| Orphan scan cost | (no target) | 5 notes in 21 ms (startup scan, Release); 10k-note cost measured at T5.5 (ADR-020) | T2.7 | ✅ |
| REQ-080 main window, Labels page visited | <90 MB | 81.8 MB open (102 MB private bytes) vs 77.5 MB on All notes only; 79.2 MB after close | T2.5 (manual) | ✅ (about 8 MB headroom) |
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
| O-9 | Path sort is ordinal on `path_norm`, so `D:\ClientsOld` sorts before `D:\Clients\Acme`. Should the library sort hierarchically? | 03 / REQ-054 | T2.4 | **closed** 2026-11-01: yes; `\` sorts as `char(1)` (03 § Repository rules) |
| O-10 | Run BenchmarkDotNet on Release (06 § Performance); T1.6 used an xunit timing test instead | 06 | T5.5 | open |
| O-11 | `NoteChanged` handlers run on the writer task (ADR-011); the app must marshal them to the UI thread | 02 / ADR-011 | T2.1 | **closed** 2026-10-31: `UiNoteEvents` relays via `IUiDispatcher`; UI code subscribes there (02 § App shell) |
| O-12 | Tray icon comes back after an Explorer restart (`TaskbarCreated`), and has the right size at 150% / mixed DPI | 02 § App shell / ADR-012 | T3.5 manual | open: code path written, not exercised (didn't restart the user's Explorer) |
| O-13 | Memory after the window closes stays ~23 MB above tray-only (XAML stays loaded). Under target now; recheck once real pages exist | REQ-080 | T5.5 | **closed** 2026-11-01: expected and now budgeted (ADR-016, "windows used < 90 MB"); `perf-check` watches it |
| O-14 | High-contrast look of chips, pills and windows (tokens map to system colours; tests check the file, but nobody has seen it on screen) and a live HC toggle re-rendering open chips | 05 § Design tokens | T2.9 manual | open: switching HC changes the user's whole desktop theme, so T2.2 didn't do it |
| O-15 | REQ-080 is over target once a window has been used: ≈65 MB private working set (≈83 MB private bytes) vs <60 MB, the one-time cost of loading WinUI's controls. An idle `EmptyWorkingSet` would show 2.7 MB in Task Manager, but commit stays at 83 MB. Options: trim the working set when idle, change the target or metric, or host windows in a child process | REQ-080 / ADR-015 | before T2.4 | **closed** 2026-11-01: the user chose per-state budgets (ADR-016); no trimming, no UI process |
| O-16 | Re-baseline the REQ-080 budgets (50 / 90 / 110 MB) once the companion panel and Acrylic exist, and add the panel and Explorer-navigation stages to `perf-check` | REQ-080 / NFR-06 / ADR-016 | T3.5 | open |
| O-18 | "Windows used" over the 90 MB budget once the search palette exists: 91–93 MB with editor + main window + palette used, flat over 8 cycles each; the palette costs ≈10 MB | REQ-080 / ADR-016 | T2.6 | **closed** 2026-11-01: the user raised the budget to 100 MB (ADR-016 amendment); `perf-check` passes against it |
| O-17 | `--edit` on a folder that doesn't exist opens a new "Can't open this note" window every time instead of reusing the open one (dedupe needs a folder key; found while writing `perf-check`) | 02 § Editor | T2.4 | **closed** 2026-11-01: deduped by path, ignoring case (05 § Editor behaviour); checked live |
| O-8 | CI workflow actually runs green on GitHub `windows-latest` | 07 T1.1 | when a remote exists | open |
| O-19 | Orphan scan reads full notes (`ListAsync`) to classify them; measure at 10k notes and switch to a binding-only query if it costs (memory or time) | ADR-020 | T5.5 | open |
| O-21 | JSON export/import (REQ-070, Core done in T1.7) had no UI and no task. **Option 1 chosen 2026-11-01 (ADR-023): preview-then-apply with a verified snapshot, format stays v1; spec written (01 AC, 03 § Import with preview and snapshot incl. restore steps); tasks T5.6 (Core) and T5.7 (UI) in Phase 5, after T5.3.** **User decision 2026-11-01:** keep as a planned feature (backup/recovery matters). Must cover: all notes + metadata, import of earlier exports, labels/colours/statuses/dates preserved, duplicate/conflict detection, no silent overwrite or data loss. | 03 § Import with preview / 07 | T5.6, T5.7 | open (specified, not built) |
| O-23 | Elevated (run-as-administrator) Explorer windows: unsupported in v1 by user decision (null path, no crash, no retry loop); confirm that behaviour by hand | 04 § G L8 | T3.2.5 / T3.5 manual | open |
| O-25 | `CoCancelCall` against a genuinely frozen Explorer process (a lab-owned `/separate` explorer.exe): **returns in 0.01 ms but does not release the blocked call**; the worker is abandoned, the replacement wedges too, the breaker trips after ~3 s and the tracker is `Disabled` until Retry. UI never blocked, Stop 2 ms, zombie published nothing. The 50 ms contingency would not help. Needs a design decision (skip/quarantine the wedged tab or process, per-tab time-box). **User decision 1 (2026-11-03):** keep the COM architecture; abandonment = bounded waiting on our side, not server recovery (accepted, 04 § B.6, ADR-025); server-aware quarantine **approved 2026-11-03** with safeguards (04 § M, ADR-025): global quarantine, release on evidence, trial workers budgeted per server; cancel HRESULT and blocked site recorded | 04 § H P6, § M / ADR-025 / report F2 | T3.2.6, then VM stage D | **approved, being built** |
| O-24 | Real Explorer restart recovery and P4 (`rundll32` private ShellWindows server) need Explorer killed, so only a disposable session can run them (D5). Until then: verified by fault injection only | 04 § F / 06 | T3.2.5 | open |
| O-26 | **The shell (explorer.exe) crashed during the T3.2.5 sweep** (2026-11-03 13:52:48, SHELL32.dll `0xc0000005`, offset `0x1e04ea`, PID 10940), inside the 50 windows/50 tabs churn test; cause unknown (tracker typed interop, the test's late-bound oracle, or a Windows bug with 46+ tabs). Real-Explorer suite now gated on `FOLDERNOTES_REAL_EXPLORER=disposable`. Needs: VM run, dump analysis (WER folder is admin-only), churn with tracker off. **User decision 2 (2026-11-03):** destructive tests only in a disposable Windows 11 VM with snapshots; isolation sequence A1–A4 (+ A3b proposed) in `specs/reports/T3.2.5-vm-test-plan.md`; a clean VM run does not close this item | NFR-01 / report F1 / VM plan | before T3.2.5 closes | **open, blocking** (VM needs disk space) |
| O-27 | T3.2.5 specification issues S1-S5 (evaluation p95 vs < 10 ms, idle CPU 0.096 % vs < 0.1 %, frozen-process availability, plain `explorer.exe` windows live outside the shell process, Explorer stops at ~46 tabs) | report | user decision | open |
| O-22 | T2.9 outstanding acceptance criteria (**user decision 2026-11-01**): Accessibility Insights FastPass on every app window; manual keyboard walk; screen-reader pass; high-contrast look (O-14). Controls having names is not enough: real usability with assistive tech counts. Does not block other development; T2.9 stays unticked until all are done | 07 T2.9 / 05 § Accessibility | before Phase 6 at the latest | open |
| O-20 | Relinking many notes after a parent folder moved to another drive is one *Relink…* per note; a "relink the others under the same parent" helper could come later (not in v1 scope) | 05 § Orphaned page | post-v1 / user's call | open |

## Decisions on the T3.2 design (ADR-025 accepted 2026-11-01)

| # | Decision | Recommendation | Trade-off |
|---|---|---|---|
| D1 | COM interop source: hand-written `[GeneratedComInterface]` (about 7 small interfaces) vs the CsWin32 package | Hand-written, proven by probe P1 first | No new dependency and it matches the existing `LibraryImport` code; but a wrong vtable slot is a silent crash risk. CsWin32 takes the layout from metadata, at the cost of a build-time package and its generated surface. If P1 fails we switch to CsWin32 | **DECIDED: A**, probes validate every signature; switch to CsWin32 on unreliability
| D2 | Cache invalidation by WinEvents + validate-on-failure (no sinks) vs `DShellWindowsEvents`/`DWebBrowserEvents2` sinks | WinEvents only | Sinks need hand-written `IDispatch` sinks over `VARIANT` and die with Explorer; WinEvents are not a documented contract (L1/L2), covered by the poll and 30 s reconciliation. Revisit only if T3.2.5 sees missed changes | **DECIDED: A**, poll and reconciliation stay; no permanent staleness
| D3 | 30 ms throttle (leading + trailing) + confirm pass, replacing the old "debounce to 50 ms" | Throttle | Can publish twice for one navigation (the confirm pass may correct a transient) so the consumer must tolerate a quick second state; a debounce would add up to 50 ms to S1's 107 ms worst case, over REQ-031 | **DECIDED: accept**; ordering, no flicker, no redundant updates required
| D4 | Time-boxing by `CoCancelCall` then abandon-and-replace the thread, with a 3-wedge circuit breaker | As specified | An abandoned thread and its COM objects are leaked on purpose (bounded by the breaker). If P2 shows cancel doesn't work against Explorer proxies, only the abandon step remains and its 1 s becomes the effective limit | **DECIDED: accept with hard bounds** (04 B.6); no safe-cancel claim without P2 evidence
| D5 | May the real "kill explorer.exe" test (and P4) run on this dev desktop? | No: disposable session or VM only; one supervised manual run at T3.5 if you agree | Killing the shell closes every Explorer window you have open (it restarts by itself). Without it, restart recovery is verified by fault injection only (O-24) | **DECIDED: no**; VM only; O-24 open

## Decisions on the T3.2.5 findings (2026-11-03)

| # | Decision |
|---|---|
| D6 | **Frozen-server quarantine approved** (04 § M, ADR-025): hold 10 s, minimum quarantine 30 s, check interval 1 s, trial deadline 10 s; call journal with generation/operation/pid/start time and torn-read protection; cancel HRESULT kept but never evidence; one trial at a time, per-server automatic trial budget, a new full hold before each automatic trial; status distinguishes temporarily unavailable / quarantined / recovery attempt / paused / permanently off; breaker, cap and budget unchanged; record process-wide. The health hold is evidence for *attempting* recovery, not proof of health |
| D7 | **Global quarantine limitation accepted:** tracking is suspended for all Explorer windows while any entry is unresolved; no undocumented `IShellWindows` ordering |
| D8 | **Destructive Explorer tests only in a disposable Windows 11 VM**; isolation matrix A1 (off/off), A2 (oracle), A3b (tracker), A3 (both), then A4; gradual load; crash evidence; a clean VM run proves nothing about the original crash |
| D9 | Lab leftover drives `U:`/`W:` removed (done, verified) |

## Questions raised by T3.2.2 (both **answered and approved 2026-11-03**, specs updated: 04 § B.6/B.7/J, 06, 07, ADR-025 amendments)

**Q1 → lifetime abandoned-worker cap 6**, breaker stays 3 wedges / 10 min, Retry allowed once more, never resets the counter, process-wide, UI shows permanent exhaustion (no Retry button). **Q2 → dedicated COM-free hook thread**, owns hook lifecycle, trivial callbacks, worker replacement never reinstalls hooks, thread budget ≤ 3 live + ≤ 7 abandoned over the process lifetime. The original questions are kept below for the record.

| # | Question | Why it matters | Recommendation |
|---|---|---|---|
| Q1 | **The spec contradicts itself on Retry.** 04 B.6 says the breaker trips at 3 wedges in 10 min *and* that at most 3 threads are ever abandoned ("at 3 the tracker stays Disabled until the app restarts"). Every wedge abandons a thread, so a breaker trip always uses up the whole cap: the Settings **Retry** can never succeed in that process (test `NFR01_With_the_default_limits_Retry_after_a_trip_is_refused_see_question_Q1` documents it). The code takes both numbers as limits and ships the literal values. | The Retry button and the thread supervisor in T3.2.3/T3.4 | Keep the breaker at 3 wedges / 10 min and raise the lifetime cap to **6** (two breaker episodes, so one Retry works, then it needs an app restart). Still strictly bounded. One constant plus a spec edit. |
| Q2 | **Where do the WinEvent hooks live?** The spec has them on the single STA worker. If that worker wedges and is abandoned, its hooks stay installed on a thread that never pumps (hooks are installed by, and delivered to, one thread; whether another thread can unhook them is unverified, and the zombie's queue keeps filling). S5/P2 also showed callbacks run *inside* the worker's COM waits. | Thread design of T3.2.3/T3.2.4; the bound "abandoned threads cannot do anything" | Put the hooks on a tiny separate **hook thread** that never makes COM calls and only posts to the worker. A wedge then strands nothing, replacement does not reinstall hooks, and callbacks never run inside a COM wait. Costs one more (idle) thread; the COM thread rules (one STA, at most 1 live, at most cap abandoned) are unchanged. A short probe (P6: cross-thread `UnhookWinEvent`, zombie queue growth) is only needed if you prefer to keep the hooks on the worker. |

## Blockers

| Since | Blocker | Needed from | Status |
|---|---|---|---|
| 2026-11-03 | Disposable VM for T3.2.5 cannot be created: C: has 10 GB free (≥ 60 GB needed, or an external NTFS SSD); Windows 11 Home has no Hyper-V, so VirtualBox 7.2.22 (installed) is used; the Windows 11 evaluation ISO must be downloaded by the user | user (disk space, ISO) | open |

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

### 2026-11-03 (T3.2.6 built: frozen-server quarantine)
- **Increments, each with deterministic tests:** (1) `CallJournal` per worker host: operations opened before the first outgoing call and closed after the last returned; sequence lock (torn reads are `Indeterminate`), generation and start-time checks (`Stale`), dangling operations closed and counted; engine and gateway operations with target pids; watchdog keeps `CoCancelCall`'s HRESULT per worker. (2) `Quarantine.cs`: `ServerIdentity` (pid + creation time), `Suspect` (known/candidates/unknown, conservative same-server rule), `QuarantineEntry`, `QuarantineRecord` (E1/E2, hold, minimum, narrowing, per-server trial budget) in `TrackerProcessGuard`; `TrackerStateMachine` with `Quarantined` (Waiting/Trial), `OnWedge(entry)` never replaces, `OnQuarantineTick`, `TrialStopped`, Retry Q19–Q24, `Availability`. (3) `Win32ServerProbe`, monitor on the watchdog thread (1 s, only while something is unresolved), supervisor rewrite (no replacement, no start-retry loop, trials, Stop/Start respect the record). (4) Stats: availability, entries, releases by evidence, trials, last cancel HRESULT, last wedge site, journal defects, pre-screened. (5) Hang pre-screen before path reads, enumerations and a worker's first connection.
- **Tests:** App 619 → 701 (687 run, 14 gated real-Explorer), Core 396, Release build 0 warnings. New: `CallJournalTests` (incl. a concurrent torn-read hammer), `QuarantineRecordTests`, `QuarantineTransitionTests` (Q1–Q24 by number), `QuarantineBoundsModelTests` (5 seeds × 3000 random steps; M.8 asserted after every step), supervisor/tracker timing tests (no worker while blocked, E1 resume ≤ 1.5 s, E2, trial pass/inconclusive/wedge, Retry refusals, Stop/Start). Rewritten where § M changed the expectation: replacement → quarantine/trial in the state-machine, budget, simulation, engine, supervisor and tracker tests; the breaker/cap/budget assertions are unchanged. Timing classes ran 3× green.
- **Slips caught:** the backslash-escape problem (backslash-a, backslash-b and backslash-n read as control characters) struck twice more while generating tests (BEL, backspace and a split literal); all found by the control-character scan and repaired with script files; final scan of `src`, `tests`, `specs` is clean. The control-character check should become a test (done: `ControlCharacterGuardTests` now scans src, tests, specs, spikes and eng; it caught this very entry on its first run).
- **Not done here (T3.2.5 work):** the COM-wait vs internal evaluation split (06), the VM harness switches and gate, P7. `AppShell` still does not start the tracker (guard test passes).

### 2026-11-03 (D6–D9: quarantine approved; spec commit before T3.2.6)
- **Spec:** 04 § M rewritten as approved (journal M.2, global quarantine M.3, evidence M.4, trial workers M.5, transitions Q1–Q24 M.6 covering repeated trial failures, process replacement, delayed worker completion, Stop/Start and Retry, availability M.7, bounds M.8); § A, B.6 and L4 aligned; ADR-025 point 5 superseded; 06 wedge expectations and quarantine tests; 07 T3.2.6; VM plan matrix A1/A2/A3b/A3/A4.
- **Bounds review (M.8):** no path found that exceeds 1 live worker, 6 abandoned workers or 10 owned threads; every abandonment goes through the cap; zombie completion never refunds; the record and cap are process-wide. No architectural contradiction found.
- **Ambiguities resolved in the spec (please check):** (1) "another full hold before another automatic trial" + "no repeated automatic creation against the same server" → at most one automatic trial **per suspect server**, compared conservatively (unknown matches everything); a further automatic trial needs a new suspect *and* a new hold. (2) "Trial timeout 10 s" → a trial passes at its 10 s deadline if it completed one full enumeration and was not abandoned; otherwise it is inconclusive (stopped, back to waiting). (3) The journal tracks *operations*, not single vtable calls, because casts and releases on a proxy are remote calls too; an operation is opened before its first call and closed after its last returns. (4) A trial worker publishes normally, generation-gated. (5) Stop during a trial spends it. (6) A suspect with no window passes the hold on time alone. (7) Explicit Retry trials need no hold and no 30 s minimum, only the absence of hang evidence. (8) Without P7, hang evidence and the pre-screen are unverified (holds then complete on time alone, still bounded).
- **Mapped drives:** `U:`/`W:` → `\\localhost\C$` confirmed as non-persistent lab leftovers and removed; `net use` lists none.
- **Next:** T3.2.6 increments 1–5.

### 2026-11-03 (T3.2.5 follow-up: user decisions 1 and 2; specs only, no code)
- **Decision 1 (frozen Explorer):** spec 04 § B.6, L4 and H P6 and ADR-025 now say that cancellation is best-effort, that cancel *latency* and *effectiveness* are different properties, and that abandonment bounds our waiting, not the server's recovery (accepted). The server-aware quarantine is written as a **proposal** in 04 § M: per-call breadcrumb, a new `Quarantined` state with no live worker, release on E1/E2 evidence, at most one probation from a Win32 health hold, Retry/Start respecting a process-wide record, breaker/cap/budget unchanged, monitor on the existing watchdog thread. **Not implemented; waiting for review.**
- **Found while reading the code:** the watchdog discards `CoCancelCall`'s HRESULT and nothing records the blocked call site, so F2 cannot distinguish "Explorer ignored the cancel" from "the call was not cancellable". The proposal records both; P7 (VM) measures them. The first sweep's `hung-explorer.md` is no longer in `artifacts/t325/` (overwritten by the second sweep), so the report's figures are the only record.
- **Decision 2 (VM):** 06 now requires a VM fingerprint as well as the environment variable for destructive tests, and splits evaluation cost into total / COM wait / internal; the idle-CPU method is rewritten (baseline in the same run, calibration outside every window, warm-up discarded). The VM plan has the isolation sequence A1–A4 plus a proposed A3b (tracker on, oracle off), a load ladder, evidence capture and an outcome table.
- **Blocker:** 10 GB free on C:. **Host housekeeping:** `U:` and `W:` → `\\localhost\C$` (non-persistent) are still mapped, probably left over from the aborted sweep; not removed without asking.
- **Next:** your review of 04 § M and the VM plan; disk space and the ISO; then the harness changes (VM plan § 7) and stage A1.

### 2026-11-03 (T3.2.5 real-Explorer verification, PARTIAL)
- **Harness (test code only, no tracker change):** `tests/FolderNotes.App.Tests/Explorer/Real` (lab that opens/closes only its own windows, independent `Shell.Application` oracle, hook-counting decorator, UI-heartbeat stand-in, foreground-theft detection with repeat). 14 gated tests; the full App suite is 619 tests, 0 failed, 14 skipped.
- **Passed on real Explorer:** path resolution for every kind obtainable (incl. UNC, loopback `net use` drive, `.zip`, virtual folders), tabs, three windows, panel-owned window keeps the folder, minimize/other-app = Inactive, latency far inside 150 ms, all-events-lost and 50 %-lost convergence, identical hook scopes never reinstalled, Start/Stop bounded with no handle growth, UI heartbeat max < 0.7 ms.
- **Stopped and reported (no architecture change):** (1) the dev shell crashed during the churn test (O-26); (2) `CoCancelCall` does not unblock a frozen real Explorer, tracker disables itself in ~3 s (O-25). Details and the full matrix: `specs/reports/T3.2.5-verification.md`.
- **Cleanup items:** the "BEL" characters were backspace characters in `ExplorerLogicSourceGuardTests.cs` that made three guards vacuous since T3.2.3; fixed, mutation-checked, one false positive fixed. Latest-wins documented as intentional (04 § D.8). Tests tagged Deterministic/Timing; protocol in 06.
- **Harness bugs found on the way:** a Python escape slip again (a trait script wrote `\1` as 0x01; reverted and redone), `Navigate2` to a UNC path blocks the caller (now a window per network case), Explorer keeps processing queued keys after the sender stops, so its title is a moving oracle.
- **Not done:** rows 3, 15, 21, 24 of the report, elevated Explorer, restart/P4, Windows 10. T3.2.5 stays unticked.
- **Housekeeping:** lab explorer.exe `/factory` processes, `subst`/`net use` drives and windows were removed; the user's shell was not touched by any test, but it crashed (O-26) and was restarted by Windows.
- **Next:** your decision on O-25/O-26; a VM for the suite.

### 2026-11-03 (watchdog investigation + T3.2.4 composition; T3.2.4 done)
- **Watchdog reliability (asked first).** Docs (Learn, `CoCancelCall`/`ICancelMethodCalls::Cancel`): `ulTimeout` is how long the *call* waits for the server after the cancel request; nothing bounds the request's own latency. New spike probe P6 (`spikes/S5-tracker-probes/P6.cs`, never shipped; scratch server, never Explorer) measured `CoCancelCall(tid, 0)` at 0.00-1.09 ms in: idle thread, nonexistent and exited thread ids, target in a non-COM wait, hung server, **fully frozen server process** (`NtSuspendProcess`), 150 random-time cancels (max 0.17 ms), three concurrent cancellers, MTA vs uninitialised caller. With `ulTimeout` = 3 s the cancel still returned in 0 ms and the *cancelled call* returned after 3002 ms. **Conclusion: no evidence it can block, so no architectural problem and no design change.** Uncertainty recorded (04 § H P6, ADR-025, O-25): never run against a really hung Explorer; undocumented internals. Added: every cancel is timed (`MaxCancelCallMs`, in stats), a cancel over 50 ms is logged once, and the contingency design is written down for you to approve if T3.2.5 ever measures one. No probe process or registry key left behind.
- **T3.2.4 built (spec 04 § L written first):** `Win32Shell`/`IWin32Shell`; `TrackerEngine` (synchronous, worker-only: signals to scheduler kinds, the evaluation steps, hook-scope policy, L8 unresolved memory, recovery with the machine's back-off and no `ShellWindows` creation while the shell is down, root validation); `GenerationScope`; `TrackerStats`/`TrackerCounters`; `IExplorerTracker`; `ExplorerTracker` (machine, publisher, router, hook thread, watchdog, supervisor, one engine per worker, a pool timer that only posts a wake-up, latest-wins hand-off to `IUiDispatcher`); `AddExplorerTracker` (singleton, not started). Supervisor gained two callbacks so the order is hook thread, watchdog, worker, and on stop detach, hooks off, worker, watchdog. The state machine's mutating methods are now synchronized (UI, worker and watchdog threads all call it).
- **Found by the tests, fixed and specified:** (1) L7 against L8: replacing an "empty" root forever is the retry loop L8 forbids when the only windows are elevated; a suspect root is now replaced at most twice in a row, then accepted (frames become unresolved). (2) A replacement worker's engine re-requested the same scope and the hook thread uninstalled/reinstalled four hooks: identical scope requests are now no-ops, and each engine's scope channel is generation-gated so a zombie cannot re-scope. (3) Latest-wins really does coalesce the `Inactive` of an abandon with the replacement's state when the UI is not pumping; tests were corrected, not the design.
- **Tests:** App 561 → 605 (engine 24 on a fake clock; tracker 15 with real threads; plus hook-thread, watchdog and guard tests). Mutation checks: removing the per-generation scope gate, the L7/L8 limit or the identical-scope no-op each break tests (two earlier mutants did not compile and were redone). A stray BEL control character from a Python escape slip was found in two test files and fixed.
- **Not done / limits:** nothing is wired into `AppShell`; no real Explorer was touched by T3.2.4 (that is T3.2.5). Real-thread tests are timing-based with scaled limits (stable over repeated full runs, not deterministic by construction). The event for an `Inactive` followed quickly by the same folder can reach the UI as a single "new sequence, same folder" event.
- **Next:** T3.2.5 real-Explorer verification.

### 2026-11-03 (T3.2.3 step 3: COM layer, worker host, watchdog, supervisor; T3.2.3 done)
- **Checkpoint first:** the router race is now tested deterministically (test seams `GapInConsume`/`GapInRaise`, four forced interleavings; the swapped-order mutant fails every time instead of sometimes); diff reviewed against the specs; committed as `16d4f2b`.
- **Built (`Explorer/Com/`, `Explorer/Native/`):** the S5-validated `[GeneratedComInterface]` set (order is the ABI; not reordered), `ComRef<T>` (`ComObject.FinalRelease`, idempotent) with `RcwTracker` (live count, cross-thread releases), `IShellWindowsGateway` + `ComShellWindowsGateway` (typed, HRESULT-returning, never throws) and factory, `StaWorkerHost` (STA, `CoEnableCallCancellation`, loop, everything runs on it, exceptions swallowed), `WatchdogThread` (event-wait; `CoCancelCall` then wedge report), `WorkerSupervisor` (detach → generation bump + `Inactive` → one replacement → attach; bounded start/stop; breaker/cap through the state machine).
- **Tests:** App 537 → 561 (24 new: ComRef, fake gateway through the real `TabMap`, real `IShellWindows` on a real STA worker with the live-RCW count at 0, host/watchdog/supervisor with real threads at a 60/120 ms scale: cancel-then-abandon, one replacement, zombie cannot publish or take signals, idle = no cancel, breaker, one Retry, 6-abandoned permanent exhaustion with Retry refused, bounded Stop while wedged, a second tracker refused, a worker that never starts, source guards). Core 396. Full App suite run 3 times, stable. Mutations: not detaching the old worker survived at first (only matters when no replacement follows), so the breaker and cap tests now assert the router ends detached and it fails; skipping `MarkAbandoned` fails 2 tests; both reverted.
- **Changes to the state machine made on the way (spec 04 § K):** `Stop(workerAbandoned)` counts a worker that would not end against the lifetime cap (not the breaker); `Start` is refused when the cap is used up, even for a new object.
- **Limits and open concerns:**
  - The real-COM tests only read `IShellWindows` on this desktop; they prove typed interop, release bookkeeping and no crash, not path parity or latency (T3.2.5).
  - `CoCancelCall` against a worker that is *not* in a COM call is a harmless no-op here; the real cancel path was proven only in S5/P2 on a scratch server, never on Explorer (D4). In these tests the stuck worker waits on a plain event, so only abandonment is exercised.
  - `FinalRelease` is counted, not observed from Explorer's side.
  - Time-based tests use real threads with short limits; they passed 3 of 3 full runs but are timing-dependent in principle (waits are generous, 5 s).
  - The watchdog thread itself blocking in `CoCancelCall` is not handled (pre-existing risk; undocumented behaviour of the API).
  - Nothing is wired into `AppShell`; no evaluation logic exists yet.
- **Next:** T3.2.4 (composition: evaluation on the worker, hook-scope policy, recovery, poll, publishing, DI).

### 2026-11-03 (Q1/Q2 specified, T3.2.3 started)
- **Specs first, no contradiction found:** 04 § B.6 (cap 6, Retry semantics, `ThreadCap` wins when both limits hit, plain wording for permanent exhaustion, process thread budget), new § B.7 (hook thread), § J (failure modes H1–H11), L3, D.2, F, A; 06 (retry exhaustion, hook thread, worker replacement, thread budget tests); 07 (T3.2.3 scope: hook lifecycle moves from T3.2.4 into it); ADR-025 amendments with the reasoning.
- **Thread-bound guarantees clarified (04 § B.7):** COM objects stay on the one worker; hooks only on the hook thread; the watchdog touches neither; the old "watchdog removes the abandoned worker's hooks" is withdrawn; callbacks no longer run inside COM waits.
- **Spec changes beyond the two decisions (flagging, not silent):** the callback's window-class allow-list moved to the worker (a class lookup is a cross-process call, not "trivial"); a precedence rule (`ThreadCap` over `CircuitBreaker`) so the UI can tell permanent from retryable; the watchdog is counted in the thread budget.
- **Code done (steps 1-2 of T3.2.3):**
  - *Q1:* `MaxAbandonedThreads` default 6 (breaker unchanged at 3 in 10 min); `ThreadCap` now wins when both limits are reached by one wedge; `IsPermanentlyDisabled` and `CanRetry` let Settings (T3.4) show "off for this session" without a Retry button; Retry never touches the lifetime count; the count is process-wide (`TrackerProcessGuard`).
  - *Thread budget:* `TrackerProcessGuard` counts live threads per kind (worker, hook, watchdog; 1 each) and abandoned workers (≤ 6) and hook threads (≤ 1, never replaced); the state machine reserves and releases the worker slot, so the budget is enforced, not just documented. Max 3 live, 10 owned at once.
  - *Q2:* `SignalRouter` (pending bits + current worker target; clear-then-read protocol so no wake-up is lost; one outstanding wake-up; detached/replaced workers get nothing), `HookThread` (own thread and Win32 message loop, no COM; install, re-scope and uninstall only on it via posted commands; stale scope requests ignored; bounded start/stop with abandonment; poll-only fallback), `IWinEventApi` seam + `Win32WinEventApi`, `ThreadMessageWakeTarget` (for the worker host, not used yet).
- **Tests:** App 480 → 537 (57 new: retry exhaustion 5, router 13, hook thread 24 incl. lifecycle/failure/worker replacement/real Win32 smoke, thread budget 9, source guard 1, plus changed state-machine cases); Core 396. Full App suite run 3 times in Release, no flakes. Mutation checks: letting a stale worker consume failed 3 tests. Swapping the router's clear/take order first only failed the stress test sometimes, so the race is now tested deterministically (test seams `GapInConsume`/`GapInRaise`, four forced interleavings, 5 new tests); the swapped-order mutant now fails deterministically. The stress test stays as a smoke test. All mutants reverted.
- **Finding (real Win32):** hook scoping follows the process owning the event's *window*, not the process that raised it (04 § J).
- **Not done yet in T3.2.3 (T3.2.3 stays unticked):** step 3: typed COM interfaces, `ComRef<T>`, `ComShellWindowsGateway` (+ fake), the STA worker host with message loop and its `IWakeTarget`, the watchdog thread; the COM/stale/wedge tests and live-RCW check. No COM code exists yet. Nothing is wired into `AppShell`.
- **Next:** step 3 of T3.2.3. No commit made this session (none was requested).

### 2026-11-01 (T3.2.2 pure logic)
- **Done:** `src/FolderNotes.App/Explorer/`: `TabMap<THandle>` (+ `ActiveTabResolver`, `ForegroundResolver`), `EvaluationScheduler` (throttle with leading/trailing edge, confirm, retry ladder, poll, reconcile; no timers, driven by a `TimeProvider`), `ComFailureClassifier`, `TrackerStateMachine` (+ `TrackerProcessGuard`, `TrackerLimits`, recovery back-off, `RootValidator`), `StatePublisher` (the single gate: generations, newest-seen sequence, no-op suppression), `EvaluationWatchdog` (cancel at 250 ms, abandon 1 s after the cancel, inclusive boundaries). No COM, Win32 or UI; a source-guard test enforces it, and that `AppShell`/`App` do not reference the tracker.
- **Tests:** 146 new (`tests/FolderNotes.App.Tests/Explorer/`): classifier table; map add/replace/remove/evict/dispose-once (a 1000-rebuild churn leaves 0 live handles); scheduler coalescing, boundaries, ladder, poll back-off, reconcile; publisher ordering (shuffled arrival, older-after-newer, older-after-no-op, older-after-failure) and abandoned-generation silence including a 4-thread concurrency test; watchdog boundaries to the tick; breaker activation/expiry/boundary/recovery, process-wide thread cap, one tracker per process, one live worker; a fake-world simulation (rapid navigation ends on the final folder with about one evaluation per 30 ms, lost events converge within 2 s, transient/gone failures, tab switches, restart, late-registered window, wedge plus zombie result). Full suites in Release: App 480 passed (was 334), Core 396 passed. Four deliberate breakages (stale-sequence guard, generation guard, throttle window, breaker) each made tests fail; all reverted.
- **Found while implementing:** (1) a failing poll would restart the retry ladder every interval and pre-empt rung 4; polls now leave the ladder alone (04 § I). (2) The spec's own limits contradict each other (Q1). (3) Hooks on the worker thread are an unexamined risk once the worker is abandoned (Q2).
- **Deviations from the written spec:** none that change behaviour; clarifications are in 04 § I. `FakeShellWindowsGateway` moved to T3.2.3; the per-frame "unresolved" memory (L8) is left to T3.2.4 (the scheduler already reports `LadderExhausted`). No COM code was started.
- **Next:** answers to Q1 and Q2, then T3.2.3.

### 2026-11-01 (T3.2.1 probes, spike S5)
- **Decisions D1–D5 recorded and committed (`596f97f`), ADR-025 accepted.** Added the user's safeguards to 04: validate every signature (CsWin32 on unreliability, no workarounds), bounded staleness without events (≤ 2 s via the poll), ordering/no-flicker/no-redundant-update rules, hard thread bounds (one tracker, ≤ 1 live + ≤ 3 abandoned threads, generation-gated publishing), elevated Explorer = no path and no retry loop.
- **Probes (`spikes/S5-tracker-probes`, never shipped), results in 04 § H:**
  - **P1** all 8 interfaces match the SDK 26100 vtables and arities (mechanically parsed from the headers), live calls and `VARIANT` work, casts behave. **Corrections found:** `Marshal.(Final)ReleaseComObject` throws on generated RCWs, the release is `ComObject.FinalRelease()`; a closed window's objects return `E_FAIL`, not a disconnect code; `Item` out of range is `S_FALSE`. CsWin32 switch not needed.
  - **P2** `CoCancelCall` returned a blocked out-of-process call in 0.2–2.4 ms once `CoEnableCallCancellation` was on (the call otherwise ran its full 6 s). Shown on a scratch server, **not on Explorer**. WinEvent callbacks are dispatched to the thread while it waits in the call (7 in 257 ms), so re-entrancy is real; callbacks stay trivial.
  - **P3** one route (`GetCurFolder`) matches the S1 late-bound result on every case obtained; virtual folders give `null`; a `.zip` reports the file path; `LocationURL` breaks UNC and was dropped. Cost 0.36 ms per read. Not obtained: search results, mapped drive, MTP.
  - **P5** a global `NAMECHANGE` hook delivered 172 (20/s source) and 633 (200/s) callbacks per 10 s, the process-scoped hook 0. CPU could not be resolved (timer tick 15.6 ms). The delivery rates also show events being dropped under load (L1).
  - **P4** not run (needs Explorer killed; D5). O-24 stays open.
- **Probe-code bugs, not findings:** the cancel server first hung because `[STAThread]` Main made the "MTA" server STA; `IOleWindow` is input-synchronous and unusable in the scratch server; `SetWindowText` on a hidden STATIC raises no `NAMECHANGE` (used `NotifyWinEvent`). A timeout once left a scratch server and its HKCU CLSID key behind; both were removed and the probe now cleans up in `finally`.
- **Housekeeping:** the probes opened Explorer windows and closed only their own (one pre-existing window of yours was not touched); no FolderNotes process, registry key or `rundll32` left.
- **Next:** T3.2.2 pure logic and tests *(done, see the entry above)*.

### 2026-11-01 (T3.2 specification, no code)
- **Specs written first, no implementation:** 04 § ExplorerTracker design (A–G: structure, COM interop, tab cache, coalescing, path reading, reliability/recovery, Windows/COM limits L1–L12; replaces the old step list and the "debounced to 50 ms" line); 04 § 4.4 and 02 (`IExplorerTracker` now has `Current`, `Start`/`Stop`, `NotifyShellRestarted`, `GetStats`; event is `{FrameHwnd, TabHwnd, Path?, Sequence}`, no `RECT`); 06 § ExplorerTracker tests; 07 T3.2 split into T3.2.1–T3.2.5; ADR-025 (*proposed*); ADR-004 marked partly superseded; O-23, O-24 and decisions D1–D5 above.
- **Inconsistencies in the old specs, now resolved:** 04 listed `DWebBrowserEvents2`/`DShellWindowsEvents` subscriptions while ADR-004 (S1) said sinks are not needed; poll interval was 250 ms (ADR-004) vs 500 ms (04); the event carried a `RECT` in 04 but not in 02.
- **Findings from reading the code and this machine (not assumptions):** (1) `CLSID_ShellWindows` is `LocalServer32 = rundll32.exe shell32.dll,SHCreateLocalServerRunDll {…}`, so creating it while Explorer is down can hand back a private empty server; recovery is gated on `GetShellWindow()` and validates the enumeration. (2) COM has no call timeout; the design needs cancel-then-abandon. (3) A global `NAMECHANGE` hook wakes the thread for every title change in the system; hooks are scoped to the foreground Explorer's process. (4) A 50 ms debounce plus S1's 107 ms worst case would break REQ-031. (5) `TaskbarCreated` only reaches top-level windows, so the tray window forwards it.
- **Review against ADR-007 and Phase 3:** typed interop, no `dynamic`, no reflection, with a source guard test. REQ-031 (150 ms, evaluation < 10 ms) and REQ-034 (tabs) are covered by §C/§D and the tests; REQ-033/038 are untouched (PanelDocker/T3.3; the foreground-owned-window rule in §D.7 keeps the folder while the panel has focus). NFR-01 (never harm Explorer): read-only, prompt release of references, no private server. NFR-06 idle CPU: no timers in the background, scoped hooks. Windows 10 and elevated Explorer remain unverified (O-6, O-23).
- **Next:** the user answers D1–D5; then T3.2.1 (probes). No implementation has started.

### 2026-11-01 (T3.1 live checks, done)
- **Settings card (UIA, real HKCU, Release build):** Off shows "Not in the right-click menu."; toggling On writes both keys; changing both commands to another exe path and revisiting the page shows Stale with the old path and a Repair button, and Repair restores On; deleting one key shows Stale again and Repair fixes it; with Delete denied on a `command` key, switching Off shows "Couldn't change this: Attempted to perform an unauthorized operation.", the toggle stays On and the registry is unchanged; after the ACL is removed, Off works and the error clears. Registry left with no keys.
- **Real Explorer menu (Windows 11 → Show more options):** "Folder note…" appears for a selected folder and for empty space inside a folder. Invoking it started the app with the right folder: `C:\Temp\FolderNotesE2E\T31\My Folder & Co (…) #1 [x]` (spaces, `&`, parentheses, `#`, brackets, non-ASCII letters) in the editor's path line and title box; the empty-space verb opened `C:\Temp\FolderNotesE2E\T31`. After `--unregister-shell`, neither menu has the entry.
- **Limits:** UIA can't see classic menu items, so the menu was checked from screenshots and driven with keys. The invoke step used arrow keys; one earlier attempt pressed F, which is the accelerator of Explorer's own "Customize this folder…", and that made Explorer write a `desktop.ini` into my scratch folder (not the app; the scratch folder is deleted). Windows 10 (O-6) isn't checked.
- **Cleanup:** no FolderNotes process, no registry keys, dev database byte-identical to its backup, scratch folder removed. Stale text reworded to "Incomplete, or registered for a different copy…" because a missing key shows the same exe path.
- **Next:** T3.2 ExplorerTracker.

### 2026-11-01 (T3.1 + O-21 decisions)
- **Specs first:** ADR-023 (import is preview-then-apply, snapshot, format stays v1), ADR-024 (verbs are registered by the installer only); 01 AC and 03 § Import with preview and snapshot (with the manual restore steps); T5.6/T5.7 in 07; 04 § 4.1, 02 § Explorer context-menu verbs, 05 § Settings page.
- **T3.1 code:** `IShellVerbRegistrar` / `RegistryShellVerbRegistrar` (Off/On/Stale read from the registry each time), `ShellVerbCommand` (`--register-shell`, `--unregister-shell`, handled first in `Main`), the Settings *Explorer* card (toggle, state text, Repair, errors). Nothing registers by itself; a test guards `App.xaml.cs` and `AppShell.cs`. 334 App tests green (Core 396).
- **Checked on the real HKCU:** before: no keys; `--register-shell` wrote both with the right command and icon (exit 0); `--unregister-shell` removed both (exit 0); no FolderNotes process left. Not checked: the Settings card on screen, a real right-click.
- **Found:** two test classes sharing one scratch registry root made a parallel test fail once; each class has its own root now.
- **Next:** live-check the Settings card and a real right-click, then tick T3.1.

### 2026-11-01 (T2.9, partial)
- **Done:** `UiaNamesTests` checks every interactive XAML element has a name; it found one gap (the
  new-label colour button had a name only after code ran) and that is fixed. 05 § Accessibility updated first.
  324/324 App tests green in Release.
- **Not done (needs a person):** Accessibility Insights isn't installed here, so the "no critical
  issues" criterion, the Tab-order walk, a screen reader pass and the HC look (O-14) are open. T2.9 stays unticked.

### 2026-11-01 (T2.8)
- **T2.8 done:** the **Settings** page: theme, the search shortcut (capture with real keys, Reset,
  a warning when the saved one couldn't be registered), *Start with Windows*, a link to Labels, and
  About (version, data folder). The theme and the library's sort/view/filters now survive restarts;
  tray *Settings* opens the page. 718/718 tests green in Release (Core 392 → 396, App 291 → 322).
- **Spec changes made first:** REQ-081 now says which task brings each setting (panel T3.4, tooltip
  T4.3, reminders T5.1; no placeholder switches), acceptance criteria (01), 05 § Settings page, 02 §
  Settings, 03 § Settings keys, the T2.8 *Done when*, T6.1 now owns "default on", ADR-022.
- **Decision to confirm (ADR-022):** REQ-080 says start with Windows is "default on". The app never
  writes the Run entry by itself (it would register every dev and `perf-check` build); the toggle
  reads and writes the real entry, and the installer (T6.1) turns it on by default. Easy to change
  if the user prefers first-run registration.
- **Found live:** a shortcut another app has registered never reaches the capture box (Windows
  sends it to that app), so the "used by another app" refusal appears through Reset and at startup
  rather than while capturing; noted in 05. Also: PowerShell turns `$null` into `""` for string
  P/Invoke arguments (UIA helper only).
- **Live-check state:** the dev database was backed up and restored (byte-identical); the Run key
  had no FolderNotes entry before and has none after; the helper processes that held hotkeys were
  stopped; the scratch registry key used by tests is removed by the tests.
- **Next:** T2.9 accessibility pass (O-14 high-contrast check is part of it). O-21 (export/import
  has no UI) is the user's call.

### 2026-11-01 (T2.7)
- **T2.7 done:** the **Orphaned** page (reasons, *Check again*, *Relink…* through the system folder
  picker with Undo, Delete with Undo, Open note, a count badge on the nav item) and a scheduled
  orphan scan (10 s after start, every 6 h). 683/683 tests green in Release (Core 366 → 392, App 264 → 291).
- **Spec changes made first:** REQ-060/061 acceptance criteria (01), 03 § Orphan detection (the
  classification table) and § Relink rules, the Upsert binding rule, 05 § Orphaned page, 02 § Orphaned
  page and orphan scan + interface changes, the T2.7 *Done when*, ADR-020 (amends ADR-009).
- **Data-integrity fixes found while reviewing the T1.5 code (ADR-020):** (1) the "recreated at the
  same path" rule didn't check the volume, so another drive mounted at the same letter could take a
  note and overwrite its file ID; (2) an editor autosave wrote back its old folder binding, which
  would silently undo a relink; (3) a scan decision could overwrite a relink made in between;
  (4) a re-bind blocked by another note left the note pointing at a stale path, neither attached
  nor orphaned. Now: same volume only, conditional writes everywhere, Upsert keeps the binding,
  conflicts orphan the moving note and change neither. Import keeps its documented restore
  behaviour (the one Core test that relied on it).
- **Found live:** opening **Labels or Settings crashed the app since T2.6** (`5a26e3f`): `FindName`
  returns null for `x:Load` elements declared in a Window (confirmed on a HEAD build). T2.6's live
  check didn't open those pages. Fixed by creating pages in code on first visit (ADR-021); all three
  pages checked live. Also: `CommitButtonText` works on the WinAppSDK picker; the picker's Folder box
  is a legacy Edit (UIA needs `WM_SETTEXT`).
- **Memory:** `perf-check` passes; the T2.6 palette growth reading (+3.3 MB) didn't reproduce
  (+0.5 MB), so no investigation, as agreed; still watched. Readings this run were ≈15 MB lower than
  T2.6 across the main window and palette with no memory-related change; not chased. O-16 stays open.
- **Live-check data:** the dev database was backed up before seeding and restored afterwards
  (byte-identical). One folder went through the user's Recycle Bin and was restored from it.
  `C:\Temp\FolderNotesE2E\T27` is left on disk (folders only, no notes reference it).
- **Next:** T2.8 Settings page (it replaces `SettingsStubPage`).

### 2026-11-01 (T2.6)
- **T2.6 done:** the search palette. `Ctrl+Alt+N` from anywhere (also the tray and `--search`)
  opens a 640 px Acrylic popup: recent notes when empty, results 120 ms after typing with matched
  words in bold, parse errors under the box, `label:` / `status:` / `due:` autocomplete (Tab),
  Enter = open folder in Explorer, Ctrl+Enter = open note in the library, Ctrl+C = copy path. It
  closes on Esc or when it loses focus and is reused. 630/630 tests green in Release (Core 366, App 241 → 264).
- **Spec changes made first:** 05 § Search palette behaviour, 02 § Search palette and
  `ISearchService.Parse`, the T2.6 *Done when*, a palette stage in 06 § Resource budgets, ADR-019.
- **Found live:** `Activate()` lost to the Windows foreground lock, so the palette opened behind the
  current app and typing went there; and focus set before the window was really active was lost.
  Fixed by joining the foreground window's input thread for the activation and focusing the box on
  the window's Activated event (ADR-019 addendum).
- **Memory (O-18):** with all three windows used the process sat at 91–93 MB, over the 90 MB budget
  but flat (no leak); the palette costs ≈10 MB once. Deferring the Labels and Settings pages
  (kept) and dropping Acrylic (reverted) made no measurable difference. **The user raised "windows
  used" to 100 MB** and asked to prefer stable, predictable resource use over chasing small
  increases, without rewrites unless profiling shows a real saving. O-16 (re-baseline after
  Phase 3) stays. Full `perf-check` passes against the new budget (palette ≤ 94.2 MB). Its palette growth reads +3.3 MB only because cycle 2's after-close sample dipped; the open samples are flat.
- **Live-check data:** the dev database was backed up before seeding and restored afterwards.
- **Next:** T2.7 orphaned view + relink.

### 2026-11-01 (T2.5)
- **T2.5 done:** a **Labels** page in the main window: every label with its usage ("Used by 3
  notes"), a new-label form preset to the least-used colour, inline rename (Enter / leaving the box
  saves, Esc cancels), a colour flyout, delete with a confirmation that says how many notes lose
  the label, and *Show notes* (All notes filtered to that label). 607/607 tests green in Release (Core 363 → 366, App 221 → 241).
- **Spec changes made first:** REQ-012 now says the Labels page (it said "Settings → Labels"; 05
  already had Labels in the navigation rail, Settings will link to it in T2.8), 05 § Labels page,
  02 § Labels page and `CountNotesByLabelAsync`, 03 label usage, the T2.5 *Done when*, ADR-018.
- **Found while designing:** an editor that stays open across a label delete would re-create the
  label on its next autosave (unknown names create labels). Open editors now refresh their labels
  on every label change and drop deleted ones without counting it as an edit (ADR-018); checked
  live.
- **Resources:** `perf-check` passes (main window ≤ 82.5 MB after the editor was used). The Labels page costs about 4 MB more once visited; it isn't part of `perf-check`'s cycle, so it was measured by hand. With the editor, the library and the Labels page all used, the process should be around 86 MB (estimated: 82.5 + 4, not measured together) against the 90 MB budget: T2.6 (search palette, a second window) is likely to cross it. If it does, the options are loading pages only when first opened, or raising the budget (the user's call, ADR-016).
- **Live-check data:** the dev database was backed up before seeding and restored afterwards.
- **Next:** T2.6 search palette + global hotkey.

### 2026-11-01 (T2.4)
- **T2.4 done:** the main window is a NavigationView with **All notes**: list or cards, sort
  (last updated / title / due date / hierarchical path), label (all-of) and status (any-of)
  filters with removable chips, empty states, and master/detail with the same editor as the
  compact window (narrow layout: Back / Alt+Left). `--open-note` now selects the note in the
  library. The main window is hidden and reused like the editor (ADR-015). Settings holds only
  the theme until T2.8; the T2.2 token sample is gone. 584/584 tests green in Release (Core 363, App 179 → 221).
- **Spec changes made first:** 05 § Library behaviour, 02 § Library and the main-window lifetime,
  03 hierarchical path sort, the T2.4 *Done when*, ADR-017 (edits update rows in place; structural
  changes reload, coalesced). *Due & reminders* is now part of T5.1 (it had no task).
- **Adjusted after seeing it live:** list rows became three lines (title | next date, path |
  edited, status + chips) because a right-hand date column squeezed the path and chips; the list
  pane is 45 % / at least 400 px (was 40 % / 360); the row path budget is 36 characters (was 60)
  so the middle trim, not the end ellipsis, decides what's hidden.
- **Resources:** `perf-check` now also cycles the main window. All checks pass, but the library costs about 14 MB more than the editor alone (81.8 MB with both used vs 68 MB), leaving 8 MB under the 90 MB "windows used" budget. T2.5–T2.8 add pages to the same window, so watch it with `perf-check` at each task; O-16 re-baselines after Phase 3.
- **Live-check data:** the dev database was backed up before seeding 7 notes under
  `C:\Temp\FolderNotesE2E` with a scratch tool, and restored afterwards, so this task left no
  notes behind. (The older residue from T2.3 is still there.)
- **Next:** T2.5 labels manager.

### 2026-11-01 (O-15 decision)
- **Decided (user):** REQ-080 gets per-state budgets instead of one "< 60 MB idle": tray-only
  < 50 MB, editor open or windows used < 90 MB, panel active < 110 MB, all in private working set,
  with private bytes recorded next to them. These are initial targets, re-baselined after Phase 3
  (O-16). No working-set trimming and no separate UI process; still one process with one writer
  (ADR-011). Recorded as ADR-016. New NFR-06 covers idle CPU, growth over cycles and extended
  sessions, and Explorer responsiveness. O-15 and O-13 closed.
- **New:** `eng/perf-check.ps1`, the repeatable resource check (06 § Resource budgets). First run on
  Release passes every check (see measurements). T3.5 adds the panel and navigation stages; T5.5
  runs the 120-cycle soak.
- **Found:** `--edit` on a missing folder opens a new window each time (O-17). A scratch folder
  under `%TEMP%` got cleaned up mid-run here, so the check uses `C:\Temp\FolderNotesPerf`.
- **Next:** T2.4 library view.

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
