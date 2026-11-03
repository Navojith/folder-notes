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
| Explorer tracker, logic | xunit.v3 + `FakeShellWindowsGateway` + `FakeTimeProvider`; no Explorer, no threads | `TabMap`, `EvaluationScheduler`, `ComFailureClassifier`, `TrackerStateMachine`, no-op suppression, latest-wins publishing (details below) |
| Explorer tracker, COM | xunit.v3, in-process fault injection around the typed interop, plus a scratch out-of-process COM server (probe P2) | `ComRef` release, live-RCW count = 0, `Gone`/`Transient` handling, cancel and abandon |
| Explorer tracker, integration | Real Explorer (`explorer.exe <path>`, scripted keys as in S1), `[Trait("Category","Explorer")]`, skipped without an interactive desktop; local and on a CI runner with a desktop session | REQ-031 latency, REQ-034 tabs, virtual folders → null |
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

## ExplorerTracker tests (T3.2, REQ-031/034, NFR-01/06)

Names carry the requirement, e.g. `REQ031_RapidNavigation_PublishesFinalFolder`. Design: 04 § ExplorerTracker design.

**Logic (always run; fake gateway, fake clock):**
- `TabMap`: add on rebuild; miss → rebuild; `DESTROY` removes and releases; `IsWindow` false drops; `Gone` drops one entry
  only; HWND reuse with a dead object is replaced; frame with several tabs; no-tab frame → implicit tab; the 256-entry
  bound evicts the oldest non-foreground entry; a rebuild never nests.
- `EvaluationScheduler`: first signal runs at once (leading); signals inside 30 ms produce exactly one trailing run;
  a continuous stream gives ≤ 1 run per window and the last state is processed; the confirm pass at +75 ms runs only
  after a change and a differing result publishes a second state; the retry ladder stops after the last rung; no
  timer is armed when no Explorer frame is foreground; poll backs off 500 ms → 2 s after 10 s of silence.
- No-op suppression: same `(frame, path)` publishes nothing (case-insensitive path); a tab switch between tabs on the
  same folder publishes nothing; a different frame on the same path does publish.
- Publishing: latest-wins when the UI is slow; `Sequence` strictly increases; `Inactive` when the foreground window is
  not (and is not owned by) an Explorer frame.
- `ComFailureClassifier`: every HRESULT in the 04 B.5 table; unknown HRESULTs are `Other`.
- `TrackerStateMachine`: `Running → Recovering → Running`; 3 wedges in 10 min → `Disabled` (`CircuitBreaker`); the fourth
  wedge after 10 quiet minutes does not trip it; `Stop`/`Start` from `Disabled` only through an explicit Retry.
  **Retry exhaustion (Q1):** with the default limits the first breaker trip (3 abandoned) accepts one Retry; the next 3
  wedges reach 6 abandoned and the state is `Disabled` with `ThreadCap` (the permanent reason wins when both apply);
  Retry is then refused (`ThreadCapReached`) however long we wait; Retry never changes the abandoned count; the count is
  shared by two tracker instances (process-wide); wedges spread over more than 10 minutes can still reach the cap.

**Stale references, disconnected objects (fault injection):** `Gone` on one tab leaves the others working; `Gone` on the
root triggers recovery and publishes `Inactive` first; `Transient` retries on the ladder and ends; `Other` ×3 drops the
entry; an exception in any gateway call never escapes the loop; live-RCW count is 0 after `Stop` and after every
`Rebuild` that dropped entries.

**Recovery:** with the fake: kill the root mid-run, assert `Inactive` → `Recovering` → `Running`, the cache rebuilt, the
same events as a fresh start, no manual restart, and no `IShellWindows` creation attempt while `GetShellWindow()` is 0
(fake shell window). A created root that enumerates nothing while frames exist is replaced (L7). Real Explorer: see
the gated test below.

**Bounds and ordering (user decisions D2–D4):** a second tracker `Start` is refused; after N wedges the number of live
and abandoned threads never exceeds the cap (1 live worker, ≤ 6 abandoned workers) and at the cap `Retry` stays `Disabled`; an
abandoned thread's late result is never published (generation gate); with all hooks disabled the state still
converges within 2.5 s through the poll; an older evaluation finishing after a newer one is dropped; no publish for
an unchanged state; an elevated/denied frame is not retried until it navigates or regains foreground.

**Hook thread (Q2; 04 § B.7, J):**
- *Lifecycle:* install runs on the hook thread (thread-identity asserted); uninstall runs on the same thread; `Stop`
  uninstalls before the thread exits; install failure → poll-only with `HooksActive = false` and no breaker count;
  uninstall twice, install twice and `Quit` before `Install` are harmless; an unresponsive hook thread is abandoned after
  the bounded wait and never replaced; a stale `SetScope` is ignored.
- *Callbacks trivial:* the handler touches no gateway, COM or UI type (source guard); 100 000 events from many threads
  produce at most one outstanding wake-up per worker; no bit is lost. **The guarantee is deterministic:** the router has two
  test seams (a gap inside `Consume` and one inside `Raise`) and the tests force every interleaving (raise before the clear, raise
  between clear and take, a whole consume between a raise's record and post, two racing raisers); a mutant with the clear/take order
  swapped fails them every time. A 4 000-round concurrent stress test remains as a smoke test only.
- *Worker replacement:* detaching the old and attaching a new worker does not call install/uninstall on the hook
  layer (fake hook API call count unchanged); events during the gap are delivered to the replacement; a detached
  worker receives no further wake-ups.
- *Process-wide thread budget:* live workers ≤ 1, hook ≤ 1, watchdog ≤ 1 across two tracker instances; abandoned
  workers ≤ 6 and abandoned hook threads ≤ 1 for the whole process; a kind at its limit refuses a new thread; releasing
  a live thread frees its slot, abandoning does not.
- *Real Win32 smoke (not Explorer):* a real hook thread installs a scoped hook, receives a `NotifyWinEvent` raised by
  the test, wakes a consumer, uninstalls, and the thread exits; thread count returns to baseline.

**Composition (T3.2.4; `TrackerEngineTests`, `ExplorerTrackerTests`):** the engine on a fake Explorer and a fake clock, with no threads: first
evaluation, navigation within 150 ms, rapid navigation bounded (about one evaluation per 30 ms, final state published, sequences increasing), tab switch,
new tab = one rebuild, implicit-frame tab, virtual folder = null path, owned window keeps the folder, minimize/other app = Inactive, nothing scheduled
while another app is foreground, hook scope follows the foreground process and is never requested twice, transient failures retried without flicker,
three `Other` failures drop the entry, an exception in the gateway is survived, at most 8 evaluations per wake-up, an unreadable (elevated) frame is
not retried in a loop but a signal retries it once (L8), a dead root publishes Inactive first and recovers by itself with no `ShellWindows` creation
while the shell is down, a changed shell process and `NotifyShellRestarted` are detected, root creation retried on the back-off without limit, a
private empty root replaced (at most twice, L7 against L8), no handle or root leaks (churn of 50 tabs), and an abandoned generation can neither publish
nor re-scope. With real threads and the fake desktop: nothing is created by constructing the tracker; the first folder reaches the UI thread; a WinEvent
through the real hook thread publishes the new folder; delivery is latest-wins (one queued task however many states); `Stop` is bounded and frees
every thread, hook and handle and a restart works; a second tracker is refused without starting a hook thread; a wedged worker is replaced **without
a single hook install or uninstall** and its late result is dropped (the stuck read returns a path only it can produce); breaker, one Retry, then
permanent exhaustion at 6 with Retry refused; `Stop` bounded while wedged; stats; DI resolves one singleton that is not started; cancel latency is
measured and a slow one logged once. Note: these use real threads with scaled-down limits and generous waits, and passed repeated full runs; the
interleavings that matter (router race, generation gate) have deterministic tests of their own.

**Deterministic guarantees versus timing-based tests (T3.2.5).** Two kinds of ExplorerTracker test exist and must not be
confused. *Deterministic*: pure logic on a fake clock, the router interleavings (test seams force each one), the
generation gate, the engine on a fake desktop, source guards; a failure is a bug. *Timing-based*: anything with real
threads, real sleeps/waits, the watchdog at scaled limits, and every real-Explorer test; they prove behaviour under
realistic timing, can in principle flake, and a pass is evidence rather than proof. Each class carries
`[Trait("Kind", "Deterministic")]` or `[Trait("Kind", "Timing")]` (real-Explorer ones: `Kind=RealExplorer`).

**Real-Explorer verification protocol (T3.2.5; code in `tests/FolderNotes.App.Tests/Explorer/Real`).** Run with
`FOLDERNOTES_REAL_EXPLORER=disposable dotnet test --project tests/FolderNotes.App.Tests -c Release --filter-trait "Kind=RealExplorer"`
on an interactive **disposable** session or VM; otherwise every test is skipped. (A first sweep on the dev desktop coincided with a crash of the shell, 09 O-26, so the dev desktop is excluded until that is understood.) Safety: the lab opens its own windows and closes only those; it never
closes, hangs, suspends or terminates the shell process or a window it did not open; keys are sent only after checking
that the foreground window is the lab's; the one process it freezes (O-25) is a separate `explorer.exe` it started with
`/separate`, checked not to be the shell and to host no earlier window, guarded by a detached resume helper. Explorer's own
idea of each folder comes from an independent oracle (late-bound `Shell.Application`, the frame title), not from the tracker's
interop. The person's desktop is in use, so an attempt that fails while a foreign window took the foreground is repeated
(at most 3 times) and the attempt number is reported. Hook callbacks are counted by a decorator in the test, not in
`TrackerStats`. Results go to `artifacts/t325/` and the report `specs/reports/T3.2.5-verification.md`.

**Destructive scenarios run only in the disposable VM (user decision 2, 2026-11-03).** *Destructive* means anything that can take down,
freeze or overload Explorer: window/tab churn, freezing a process, killing the shell, P4, P7. Those tests carry
`[Trait("Destructive", "true")]` and run only when `FOLDERNOTES_REAL_EXPLORER=disposable` is set **and** the machine identifies as a
VM (SMBIOS manufacturer/model, e.g. `innotek GmbH` / `VirtualBox`). Otherwise they are skipped with that reason. The environment
variable alone is not enough. Until O-26 is understood, the non-destructive real-Explorer tests also run in the VM only. The host's
T3.2.5 numbers are kept as they are (labelled "dev desktop, 2026-11-03"); VM numbers are reported next to them, never in their
place. Run plan: `specs/reports/T3.2.5-vm-test-plan.md`.

**Evaluation cost: internal time versus COM waiting (T3.2.5, S1).** The engine times each evaluation as a whole and, separately, the
time spent inside outgoing COM calls (each gateway call: create, enumerate, read path, release). `TrackerStats` reports three
distributions (p50/p95/max, last 256): **total**, **COM wait** (time Explorer took to answer, including its UI thread being busy) and
**internal** (total minus COM wait: our own work). The < 10 ms evaluation target of 04 § C is checked against *internal*, with
*total* reported next to it. End-to-end latency (≤ 150 ms, REQ-031) is still measured and covers COM waiting. *Whether REQ-031's text
should change to say so is the user's call (O-27 S1); until then the report shows both and marks p95 of total as "over target,
COM-bound".*

**Idle CPU method (T3.2.5 re-measure, VM).** The first measurement was invalid (the calibration spin fell inside the baseline). The
method is: a quiet VM (Defender real-time and scheduled scans off for the test folder or the session, Windows Update paused, Search
indexing and SysMain stopped, no other app, 10 min settle after boot); one warm-up run of the tracker for 60 s that is not reported
(JIT tier-up); then, alternating 5 times, **baseline** (same test process, tracker constructed but not started, Explorer foreground,
untouched) and **tracker** (started, same conditions), 60 s each. CPU = `QueryProcessCycleTime` delta of the test process converted
with a cycles-per-ms factor that is measured **before** the first window and outside every window. Report median and max of
*tracker − baseline* as % of one core, plus `TrackerStats` evaluation and callback counts per window. Inside a VM, absolute numbers
are indicative. The pass criterion is the delta against the baseline taken in the same VM run.

**Wedge:** a gateway call that blocks forever → cancel at 250 ms, abandon at 1 s, `Inactive` published, **no new thread
while the block lasts** (`Quarantined`, 04 § M; changed 2026-11-03), the old thread's late result is discarded
(generation), the UI thread is never blocked, `Stop` returns in ≤ 2 s even while wedged. When the block ends (E1) a
worker serves the next signal within 1.5 s.

**Quarantine (T3.2.6, 04 § M.11):** deterministic tests for every transition Q1–Q24 on the pure machine and record with a
fake clock and a fake server probe (process exit, pid reuse, hang evidence); the same-server rule and at most one automatic
trial per server; the hold restarts on hang evidence; the 30 s minimum; the 10 s trial deadline (pass, inconclusive, wedge);
the journal (sequence-lock torn reads are indeterminate, a stale or dangling record is never active, operations nest gateway
inside engine); the cancel HRESULT never releases; the record survives Stop/Start and a new instance; and a seeded random
model test that applies event sequences and asserts the bounds of 04 § M.8 after every step (live workers ≤ 1, abandoned
≤ 6 and never decreasing, no automatic trial twice for one server). Timing tests (real threads, fake blocking gateway): no
worker while `Waiting`, release after the block ends, trial deadline, Retry refusals.

**Integration (real Explorer, interactive desktop, `Category=Explorer`; the S1 scenarios promoted to tests):** navigate
(different names, same-named folders, Unicode path), virtual folder → `null`, new tab, switch tab (both directions),
switch window, minimize/restore, the panel-owned window in the foreground keeps the folder, two windows × several
tabs. Each asserts the published state equals Explorer's own and measures latency (≤ 150 ms; record median/max) and
`TrackerStats` evaluation cost (< 10 ms).

**Reliability (real Explorer):** rapid navigation (200 Alt+Up/Backspace/path-entry steps), rapid tab switching (200
Ctrl+Tab), open/close 50 windows and 50 tabs in a loop: final published state matches Explorer after input stops;
published events ≤ a bound documented with the test; live RCWs and tab entries return to the baseline; private bytes
and handle count grow ≤ 1 MB / ≤ 10 over the loop; no `rundll32.exe` or extra `explorer.exe` started by us.

**Explorer restart (real; gated).** Killing the shell disturbs the desktop, so it runs only when
`FOLDERNOTES_ALLOW_EXPLORER_KILL=1` is set and only on a disposable session (CI runner or VM), never by default and
never on the dev desktop unless the user says so (09 decision D5): kill `explorer.exe` (Windows restarts it), assert
`Inactive` within 150 ms of the window destruction, recovery within 10 s, a correct state after it, repeat 5 times,
no extra processes. Until it has run, restart recovery is "verified by fault injection only" in 09.

**Idle CPU (`perf-check` stage, T3.5; first measured in T3.2.5):** < 0.1 % over 60 s with Explorer foreground and
untouched; 0 % with another app foreground; zero timers/hook callbacks counted in `TrackerStats` while Explorer is
in the background.

## Explorer stability (NFR-01)

- **Stress test:** a script hovers 1,000 folders through UI Automation while the app writes notes
  in a loop. Pass criteria: no Explorer crash, every `GetInfoTip` under 50 ms, and no DLL leaks
  after `DllCanUnloadNow`.
- **DB missing / locked / corrupt:** the handler returns `E_FAIL` quickly in each case.
- Run the handler under Application Verifier (heaps, handles, locks) once per release.

## Manual checklist (maintained in `tests/manual-checklist.md`)

Windows 10 22H2, Windows 11 23H2, Windows 11 24H2 × {100%, 150%, mixed-DPI multi-monitor} ×
{light, dark, high contrast}: panel docking, snapping, maximize, tabs, toasts, context menu tiers.
