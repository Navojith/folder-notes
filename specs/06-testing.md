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
`[Trait("Destructive", "true")]` and run only when **all** of the following hold (ADR-026, § Lab harness safety below): `FOLDERNOTES_REAL_EXPLORER=disposable` is set; both identity
controls pass (the VirtualBox SMBIOS fingerprint including the VM's UUID, **and** the deliberately provisioned guest marker); and the explicit opt-in
`FOLDERNOTES_LAB_DESTRUCTIVE=1` is set. Otherwise they are skipped with every missing condition listed. No single variable is enough, and the gate fails closed. Until O-26 is understood, the non-destructive real-Explorer tests also run only where both identity checks pass (the VM); the host never runs them. The host's
T3.2.5 numbers are kept as they are (labelled "dev desktop, 2026-11-03"); VM numbers are reported next to them, never in their
place. Run plan: `specs/reports/T3.2.5-vm-test-plan.md`.

**Evaluation cost: internal time versus COM waiting (T3.2.5, S1).** The engine times each evaluation as a whole and, separately, the
time spent inside outgoing COM calls (each gateway call: create, enumerate, read path, release). `TrackerCounters` (the thread-safe
accumulator shared by every worker generation) records the samples, and `TrackerStats` (the immutable snapshot returned by `GetStats()`)
reports three
distributions (p50/p95/max, last 256): **total**, **COM wait** (time Explorer took to answer, including its UI thread being busy) and
**internal** (total minus COM wait: our own work). The < 10 ms evaluation target of 04 § C is checked against *internal*, with
*total* reported next to it. The exact timing boundaries (what is inside COM wait, what internal means, what neither covers) are defined in § Lab harness safety H7. End-to-end latency (≤ 150 ms, REQ-031) is still measured and covers COM waiting. *Whether REQ-031's text
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
`FOLDERNOTES_ALLOW_EXPLORER_KILL=1` is set and only on a disposable session that passes the full lab gate (below; today only the T3.2.5 VM), never by default and
never on the dev desktop unless the user says so (09 decision D5): kill `explorer.exe` (Windows restarts it), assert
`Inactive` within 150 ms of the window destruction, recovery within 10 s, a correct state after it, repeat 5 times,
no extra processes. Until it has run, restart recovery is "verified by fault injection only" in 09.

**Idle CPU (`perf-check` stage, T3.5; first measured in T3.2.5):** < 0.1 % over 60 s with Explorer foreground and
untouched; 0 % with another app foreground; zero timers/hook callbacks counted in `TrackerStats` while Explorer is
in the background.

## Lab harness safety and evidence (T3.2.5; ADR-026)

Normative requirements for the real-Explorer lab (`tests/FolderNotes.App.Tests/Explorer/Real`) and the VM runner scripts. Test and
script code only: none of it touches `AppShell`, and the tracker stays unwired. The execution procedure and evidence layout are in
`specs/reports/T3.2.5-vm-test-plan.md`. These gates guard against **accidents** (a command typed on the wrong machine); they are
not a defence against a person who sets variables on purpose.

### H1. Identity gate (fail closed)

Two **complementary identity controls**, evaluated in the guest at run time. They are not independent proofs of identity: the marker
records the SMBIOS UUID, so it is checked against the same firmware data. SMBIOS says which VM this is; the marker says that the user
deliberately provisioned that VM for the lab. Every real-Explorer test (destructive or not) requires **all** of: (1) the SMBIOS
check, (2) the marker check, (3) the interactive-session check (below), (4) `FOLDERNOTES_REAL_EXPLORER` equal to `disposable`
(exact comparison, below); a destructive test also requires (5) `FOLDERNOTES_LAB_DESTRUCTIVE` equal to `1` (H2). **The marker is
not a substitute for the environment gate** (nor the reverse): each condition is evaluated separately and none short-circuits another,
a passing marker never enables a run whose other conditions fail, and the skip or failure message lists every condition that failed,
not only the first.

| Check | Source | Passes only when |
|---|---|---|
| 1. SMBIOS fingerprint | Raw SMBIOS table (`GetSystemFirmwareTable('RSMB')`), no WMI | manufacturer is exactly `innotek GmbH`, product is exactly `VirtualBox`, **and** the system UUID equals the expected UUID of the T3.2.5 VM (`5537d76c-7c7a-4d5f-8f04-67d97d1f1763`, the VirtualBox `hardwareuuid` of `SchedShutdownTest`) |
| 2. Provisioning marker | Registry `HKLM\SOFTWARE\FolderNotesLab`, 64-bit view | the key holds exactly the values, registry types and formats listed under "Marker contents", `MarkerId` is `FN-T325-DISPOSABLE-VM-v1`, **and** `SmbiosUuidRaw`, `SmbiosVersion`, `UuidConversion` and `SmbiosUuid` agree with the table read in check 1 |

- **SMBIOS UUID.** VirtualBox derives it from the machine UUID (host: `VBoxManage showvminfo --machinereadable`, key `hardwareuuid`),
  so it is stable across snapshots and restores. It changes if the VM is cloned or re-registered; the gate then fails until this
  spec and ADR-026 are updated.
- **UUID interpretation (prescribed by the SMBIOS version, never inferred from the data).** The gate reads the 16 bytes `b0..b15` of
  the Type 1 UUID field and the SMBIOS version from the table header. For SMBIOS versions **from 2.6 up to, but not including, 4.0** the first three fields are
  little-endian, so the interpretation is `mixed-endian`: canonical lowercase `8-4-4-4-12` string `b3 b2 b1 b0 - b5 b4 - b7 b6 - b8 b9
  - b10..b15`. The supported range is exactly `2.6 <= version < 4.0`, compared numerically. **Outside it the check fails closed**: below 2.6
  (2.1-2.5) the layout is historically ambiguous, and from 4.0 onward it is not known to this spec. Accepting such a table would
  need an amendment to this spec and ADR-026 on the user's decision. There is **no per-minor whitelist**: every minor of major 3 and
  every minor of major 2 from 6 upward is inside the range (2.255 and 3.255 pass the version rule). A version that cannot be read
  (header shorter than 8 bytes, or a `major` of 0) fails. The
  comparison with the host's `hardwareuuid` (passed to provisioning as the expected value) **validates the identity; it never selects
  or overrides the interpretation.** If the prescribed interpretation does not reproduce the expected UUID, the check fails. As a
  diagnostic only, provisioning also computes the other (`straight`) form; if that one matches, it reports a **discrepancy** ("the
  bytes contradict the prescribed interpretation for SMBIOS x.y"), **fails provisioning (exit 1), writes nothing and does not switch
  interpretation.** A degenerate UUID for which both forms give the same string also fails (the check could not tell a wrong
  interpretation from a right one). `UuidConversion` in the marker is `mixed-endian` and must equal what the version prescribes at
  every evaluation. The provisioning log records the SMBIOS version, the raw bytes (hex), the prescribed interpretation, the canonical
  string, the expected value, the diagnostic form and the verdict. The conversion is unit-tested on synthetic tables first and against
  a fixture of the real bytes from the actual VM later (reading them needs the VM booted, which needs separate approval). **Risk to
  settle at the verify-only run:** If the actual VM reports an SMBIOS version outside the supported range (below 2.6, 4.0 or later, or unreadable), provisioning (including the verify-only mode) exits 1 with an **incompatibility report** (version, raw bytes, both forms, every diagnostic), writes nothing and **waits for the user's decision** on an amendment to this spec and ADR-026. It never changes the interpretation itself.
- **Defensive SMBIOS parsing (fail closed).** The gate takes the table from `GetSystemFirmwareTable('RSMB')` and parses it with explicit
  bounds checks, so malformed firmware data cannot satisfy the identity check by accident. Every condition below failing means the
  check fails; any exception also fails; nothing is read past a bound:
  - *Header and version:* the call succeeds and returns at least the 8-byte `RawSMBIOSData` header. The version is read as the integer
    `major` and `minor` header fields and compared as a **numeric pair**; it is never converted to a decimal, float or string
    (or parsed back from one) for the comparison, so 2.10 is later than 2.6: accepted only if `major == 3`, or
    `major == 2` and `minor >= 6`; everything else (0.x, 1.x, 2.0-2.5, 4 and later) fails. That is the range `2.6 <= version < 4.0`, and nothing else.
  - *Length:* the reported table `Length` is greater than 0, does not exceed the bytes returned after the header, and is at most
    1 MiB (1,048,576).
  - *Traversal stays inside the reported table:* each structure has a 4-byte header (type, formatted length, handle) with a formatted
    length of at least 4 that fits in the remaining bytes; its string set ends with the double-NUL terminator **within** the table; the
    cursor always advances. **Structure-count cap:** at most 4096 structures **and** at most `Length / 6` (a structure needs at least
    its 4-byte header plus a 2-byte empty string set); exceeding either fails.
  - *End of table:* the traversal stops at the **first** type 127 (end-of-table) structure, which must be found inside the table and
    is itself checked like any structure. Bytes after its terminator, up to `Length`, may only be zero padding; **any non-zero
    trailing byte fails**, and nothing after the end-of-table structure is interpreted (so a second Type 1 cannot hide there).
  - *Exactly one Type 1 (System Information) structure* before the end of table: **none, or two or more (even identical ones),
    fail**, as does a Type 1 followed by a malformed remainder. The whole table is traversed, so a duplicate is seen after the first.
  - *The Type 1 structure is long enough:* its declared formatted length is at least `0x18`, so the complete 16-byte UUID field
    (offsets `0x08`-`0x17`) is inside it.
  - *Strings and decoding:* a string set is a sequence of NUL-terminated byte strings ended by one extra NUL (a structure with no
    strings has `00 00`). String numbers are 1-based. The manufacturer and product indices (offsets `0x04`, `0x05`) must be 1 or more
    and at most the number of strings; 0 or out of range fails. Only these two strings are decoded: **strict ASCII** (bytes
    `0x20`-`0x7E` only; a control byte or a byte of `0x80` or above fails; no code page, no replacement characters), 1 to 255 bytes
    (empty or longer fails), compared **ordinally, case-sensitively, with no trimming**. Other strings are skipped without decoding;
    only their boundaries matter.
  - *UUID values:* all-zero (not set) and all-`0xFF` (not set) fail.
  - *Tests (synthetic SMBIOS tables, no VM needed):* a valid table passes; header shorter than 8 bytes; `Length` zero, larger than the
    buffer, exactly 1 MiB (passes the size rule) and 1 MiB + 1 (fails); versions 1.9, 2.0, 2.5 (fail), 2.6 (passes), 2.10 (passes,
    numeric compare), 3.0, 3.9, 2.255 and 3.255 (pass), 4.0, 4.1 and 255.255 (fail), major 0 (fails), and the string-ordering traps (2.10 against 2.6 and 2.9, 3.0 against 2.10); a structure formatted length 3 (fails) and 4 (passes, for the
    end-of-table structure); a structure running past the table; an unterminated string set; no end-of-table structure; **a valid
    table truncated at every possible length fails except the complete table**; structure count at the cap (passes) and cap + 1
    (fails), and the `Length / 6` bound; zero padding after the end of table (passes) and one non-zero trailing byte (fails); a second
    Type 1 hidden after the end of table (not interpreted, trailing bytes fail); no Type 1; two Type 1 (identical, and different);
    Type 1 length `0x17` (fails) and `0x18` (boundary, passes); string index 0, 1, N (passes) and N + 1 (fails); a 255-byte string
    (passes) and 256-byte (fails); a string with byte `0x80`, `0x1F` or `0x7F` (fails); wrong manufacturer; wrong product;
    all-zero and all-`0xFF` UUID; the prescribed interpretation matching (passes), not matching (fails), only `straight` matching
    (fails as a discrepancy, no switch), both forms equal (fails). The real-VM fixture is added later, after separate boot approval.
- **Interactive session (exact check).** All of these hold, each read afresh at every evaluation, or the condition fails: (a) the
  harness process's session id (`ProcessIdToSessionId`) is not 0 (Session 0 is excluded); (b) it equals the session id of the shell
  process (`GetShellWindow`, then `GetWindowThreadProcessId`, then `ProcessIdToSessionId`); no shell window or an unreadable
  session fails; (c) the process's window station is `WinSta0` (`GetUserObjectInformation`, `UOI_NAME`); (d) the harness runs
  **unelevated**: its token's `TokenElevation` is 0, like the host user's, and an elevated harness fails. `Environment.UserInteractive`
  alone is not sufficient. The session ids and the elevation state go into the result row.
- **Environment variables.** `FOLDERNOTES_REAL_EXPLORER` is read from the process environment at each evaluation and compared
  **ordinally and case-sensitively** to the exact string `disposable`: no trimming, no case folding, no other accepted value. Unset,
  empty, `Disposable`, `disposable ` (trailing space) and `1` all count as not set. `FOLDERNOTES_LAB_DESTRUCTIVE` likewise must equal
  exactly `1`. (The switches in H3 are case-insensitive by contrast.)
- **Marker contents (exact registry types and formats).** The key has **exactly** these values, no others and no subkeys. A wrong
  registry type (`REG_EXPAND_SZ`, `REG_MULTI_SZ`, `REG_DWORD`, ...), a wrong format or an extra value or subkey fails. Values are read
  without environment-variable expansion. Informational values are checked for type and format only. **The gate and the
  provisioning script both use the 64-bit registry view** (`RegistryView.Registry64`), so they cannot read different keys. **The unnamed (default) value
  is prohibited too:** the set of value names the key reports must equal the eight names below exactly, so a default value of any
  content, even an empty string, makes the check fail.

  | Value | Type | Exact format | Role |
  |---|---|---|---|
  | `MarkerId` | `REG_SZ` | `FN-T325-DISPOSABLE-VM-v1` | checked |
  | `SmbiosUuid` | `REG_SZ` | 36 characters, lowercase canonical `8-4-4-4-12` (`[0-9a-f]` groups of 8, 4, 4, 4, 12 joined by `-`) | checked against the table |
  | `UuidConversion` | `REG_SZ` | `mixed-endian` | checked against the version rule |
  | `SmbiosUuidRaw` | `REG_BINARY` | exactly 16 bytes, in table order | checked against the table |
  | `SmbiosVersion` | `REG_SZ` | `<major>.<minor>`, decimal integers without leading zeros (for example `2.7`) | checked against the table header |
  | `VmName` | `REG_SZ` | `SchedShutdownTest` | informational |
  | `ProvisionedOn` | `REG_SZ` | ISO 8601 local time with offset, `yyyy-MM-ddTHH:mm:sszzz` (for example `2026-11-05T10:15:00+05:30`) | informational |
  | `PreservationSnapshot` | `REG_SZ` | `pre-T325-preserve-2026-11-03` | informational |
- **Provisioned deliberately, never by the harness.** Only a new elevated script, `drop/provision-marker.ps1`, creates it, run by the
  user in the guest. It follows the discipline of `prep-admin.ps1` (preflight; original state recorded, here "key absent"; VERIFY;
  log to `out`; host-side tests against a fake system, mutation-checked), and it has a **verify-only mode** that performs the UUID
  validation and logs the representations without writing anything. **Refuses if the key exists at all.** If `HKLM\SOFTWARE\FolderNotesLab` exists, whatever it contains (even contents identical to what would be written), provisioning reports its values and types and stops (exit 1). It never adopts, repairs or overwrites an existing marker: the user investigates, so the script never decides that a marker it did not just write is trustworthy. Verify-only mode reports an existing key
  too; judging its contents is the gate's job, not the script's. `tester` (filtered token) cannot write under `HKLM\SOFTWARE`.
  No code under `tests/` or `eng/` may write that key (a source-guard test). **Adding the marker is an additional VM preparation
  change and needs its own approval** (plan § 2 item 8, § 8 change log); this spec does not apply it.
- **Removal.** Deleting the key is on the restore list, on the user's word after T3.2.5: `restore-admin.ps1` (extended) or
  `provision-marker.ps1 -Remove` (the only operation that touches an existing key) deletes it only if it holds **exactly the eight values provisioning wrote, with those
  contents, and no subkeys**; any unexpected value or subkey makes it refuse and report; or return to
  `pre-T325-preserve-2026-11-03`.
- **Fail closed.** A missing key or value, a wrong value, an extra mismatch, an access error, an unreadable SMBIOS table or any
  exception makes the gate **false**, never "assume yes". The skip or failure message names each failed check and why. The gate is
  evaluated at the start of every test run and again before every churn cycle and every freeze/kill/P4/P7 operation.
- **Testable on the host.** The gate takes its sources as injectable readers: the SMBIOS table bytes, the marker key, the session
  information (session ids, window station, elevation) and the environment. Unit tests cover: all pass; each condition fails alone
  (SMBIOS, marker, session, `REAL_EXPLORER`, and for destructive the opt-in), with the message listing it; a passing marker with a
  failing environment gate still fails; each source unreadable; UUID mismatch between check 1 and the marker; wrong manufacturer,
  product or UUID; wrong marker id, type, format, extra value or subkey or a set default value; the interpretation rules and the parser tests above; session
  0, a harness session different from the shell's, a window station other than `WinSta0`, no shell window, an elevated token; the
  exact `disposable` and `1` comparisons (`Disposable`, trailing space, `true`, empty). On the host both identity controls fail (no
  marker, SMBIOS is not VirtualBox) so every real-Explorer test is skipped.

### H2. Destructive-test protection

*Destructive* means anything that can take down, freeze or overload Explorer: window/tab churn, freezing a process, killing the shell,
P4, P7. A destructive test runs only when **all** of these hold, and none replaces another:

1. the full H1 identity gate passes and `FOLDERNOTES_REAL_EXPLORER=disposable` is set (the normal disposable-VM gate);
2. the test carries `[Trait("Destructive", "true")]` (**every churn test must**), through a `DestructiveFact` attribute (the one mechanism: `DestructiveFact` applies that exact trait itself, the test filter and the source guard look for that exact key and value, and a churn test using a plain `Fact` fails the guard);
3. the explicit opt-in `FOLDERNOTES_LAB_DESTRUCTIVE=1` is set (exactly `1`; any other value, including `true`, counts as not set).

**The complete gate is evaluated at three kinds of points, and no result is reused from another:** (a) before test setup, i.e. before
the lab creates any window, process or tracker rig; (b) at the start of every cycle; and (c) **immediately before every destructive
operation: `DestructiveGate.Require()` is called directly before each individual operation, with nothing in between.** A destructive
operation is each freeze, each kill, each P4 action and each P7 action, and for churn each **phase** of a cycle (opening the windows,
opening the tabs, closing them). A check before the first action, or once per cycle, is **not sufficient by itself**; (a) and (b)
stay as additional protection. **No Explorer churn, freeze or kill starts until the complete gate has succeeded at that point.**
Enforcement is layered: the `DestructiveFact` attribute skips with every missing condition listed; the destructive primitives
(window/tab churn, freeze, kill, P4, P7) are reachable **only through one wrapper**, `DestructiveOps.Run(operation, action)`, whose
first statement is `Require()` (defence in depth, so a mis-tagged test still cannot run); a source-scan guard test fails if a
primitive is called from anywhere else or if a test that reaches one is not tagged. A driver test with a fake gate that turns false
between two operations shows that the second does not start and that the failure is classified as a safety-gate failure. The opt-in
variable is set by the person, or by the guest runner after an explicit `-AllowDestructive` parameter; never by default.

### H3. Switch matrix

Read once by a single `LabSwitches` reader and validated together before any Explorer action.

| Variable | Accepted values (case-insensitive) | Default (unset) | Meaning |
|---|---|---|---|
| `FOLDERNOTES_LAB_ORACLE` | `on`, `off` | `on` | `on`: late-bound `Shell.Application` oracle used; `off`: never called |
| `FOLDERNOTES_LAB_TRACKER` | `on`, `off` | `on` | `on`: tracker rig started; `off`: not created |
| `FOLDERNOTES_LAB_LOAD` | `0`, `L1`, `L2`, `L3`, `L4`, `L5`, `L6` | `0` | `0`: no churn; `L1`-`L6`: the load ladder (plan § 4) |

**Invalid values fail loudly:** a set value that is empty, misspelt or outside the list (including `1`, `true`, `L7`, `l`) fails the
run with a message naming the variable and the value, **before** any Explorer action. It is never defaulted, ignored or downgraded to
`0`. **A nonzero load requires the destructive opt-in:** `LOAD` other than `0` with the H2 conditions not all met is a configuration
error (the run fails, no Explorer action), not a silent skip and not a silent drop to `0`.

| Oracle | Tracker | Scenario | Final-state check |
|---|---|---|---|
| off | off | A1 baseline | Frame title only (weaker: leaf name). No oracle call, no tracker. Lab windows all gone, shell pid unchanged |
| on | off | A2 oracle-only | Oracle path against the frame title (the leaf name); no tracker comparison |
| off | on | A3b tracker-only | Tracker's reported folder against the frame title at **leaf-name level** (weaker; same-named folders are indistinguishable, see below). Full-path agreement is **not** asserted (no oracle), and the report says so |
| on | on | A3 combined | Tracker path against oracle path (full path), plus the frame title. This is the original check |

**A4 is a stage, not a fifth switch combination.** The four combinations above are the only oracle/tracker settings and map one-to-one to A1, A2, A3b and A3. A4 uses the **A3 switches** (oracle on, tracker on) with `FOLDERNOTES_LAB_LOAD=L6` for 10 runs, to reproduce the original 50-window / 50-tab scenario. A3 asks whether the combination misbehaves **below** the original load (L1-L4, a reduced ladder); A4 asks whether the original load reproduces the crash. **Sequencing (explicit).** A4 is started only after the **complete A3 reduced ladder (L1-L4) has been run and reported** and the user has approved I(A4) (plan § 10). Like every stage it begins with **the same baseline restoration**: restore `S2-<commit>`, cold boot, 10 min settle, so it never continues from A3's end state. Its final-state check is A3's.

**The title-based checks are weaker assertions than a full-path comparison, and reports label them so** ("title only" for A1,
"leaf-name match" for A3b). A frame title shows the folder's leaf name only (and what it shows depends on Explorer's title-bar
setting), so a leaf-name match **cannot show that the parent path is right**, and two different folders that share a name
(`C:\a\Docs` and `C:\b\Docs`) match each other. In A3b a tracker that returned the wrong folder with the same leaf name would
pass undetected. Mitigation, required: every folder the lab opens gets a **unique leaf name** (a run-and-sequence token), the driver
asserts uniqueness, and a duplicate leaf name makes the comparison **ambiguous**, which is recorded as inconclusive and never as a
pass. Full-path agreement is asserted only in A3 and A4.

**Title availability (A1, A2, A3b).** A title check has three outcomes: *match* (the title exposes the expected leaf name in a
recognised format), *mismatch* (it exposes a different leaf name: a failure), and *unavailable* (the title is empty, generic such as
"File Explorer", or in a format the lab does not recognise because of the title-bar setting or the view). **Unavailable is
inconclusive**, never a pass and never a failure. Before any churn the lab opens one folder with a known unique leaf name and checks
that the title can be read this way; if it cannot, the title-based comparison is unavailable for the whole run, the run is recorded
as inconclusive for A1, A2 and A3b, and it stops with a report instead of climbing the ladder.

In every combination the crash and resource checks are unchanged: the shell pid and the set of `explorer.exe` processes are as before
the run, no `explorer.exe` exit or WER/Application-Error event in the window, handle and memory deltas within the existing limits,
and every lab-opened window closed. With the defaults (`on`/`on`/`0`) the existing tests behave as they do today.

### H4. Churn ladder and repetition

The ladder L1-L6 (plan § 4) with **3 initial cycles per level** is **screening, not proof of stability**: a clean pass lowers the
suspicion of a crash and bounds the per-run rate (plan § 5); it does not exclude one. A failed run is **classified**, because a
failed test, a broken safety mechanism and missing evidence are not the same outcome. A run can fall into several categories; all are
recorded and the **most restrictive** action applies.

**Stage run counts (explicit; one table, never inferred from the stage name).** The approved matrix (plan § 4) gives:

| Stage | Switches (oracle / tracker) | Runs | Maximum runs |
|---|---|---|---|
| A1, A2, A3b (each) | off/off, on/off, off/on | L1-L6 with 3 cycles per level (18), then 5 further runs at L6 | **23** |
| A3 | on/on | L1-L4 with 3 cycles per level | **12** |
| A4 | on/on | L6 only | **10** |

"L6 x 5" in the plan's matrix is read as five **additional** L6 runs after the ladder (this reading is recorded for the user to
confirm; a change is a spec edit that changes this table, and the driver, the I(stage) summary and the tests all read the counts
from one table). The additional L6 runs use the same switches and the same restored baseline as the stage.

**Terms (one meaning each, used in every document).** A **run** is one cycle at one level. A **restore-and-run unit** is one snapshot restore plus one run, and is what an **R** approval (or each unit of a named batch) authorizes after a failure. An **initial stage run** is the deliberate exception, authorized by **I(stage)**: one restore plus **all** the runs of that stage's ladder (table above), up to its first failure of any category. A **repetition** is a run repeated after a failure and exists only under R. The one-unit and named-batch rules therefore govern recovery (R), not the initial stage run.

**Approval boundaries.** On any failure, in every category: stop, collect evidence, preserve the current VM state, report the
classification and the evidence status, and wait for the user's approval. Nothing is restored and no further run starts without it,
even where the plan names the restore and the repetitions as the next step. **One approval authorizes exactly one snapshot restore
and the one run that starts from it** (a "restore-and-run unit"); the next repetition needs its own approval. The user may instead
approve a **named batch** in explicit words (for example "restore and run the 3 repetitions at L4"). That authorizes exactly that
many units, at exactly that level and stage; it **ends at the first failure of any category**, when a safety-gate or disk condition
changes, or when the count is reached, and it never carries over to another level, stage or day. Without such wording the single-unit
rule applies. Resulting sequence for a functional or crash failure: stop, collect, report, approval, restore, run (repetition 1),
report, then approval again for repetition 2 (or a named batch covering it), and so on. Safety-gate, disk-guard and
evidence-collection failures never trigger automatic repetitions: their cause is resolved first, and only then does the approval
sequence above start.

**Approval units (plan § 10; each is a separate permission and none implies another).** V: the verify-only session (boot, read-only run,
capture the raw SMBIOS bytes, clean shutdown). P: the marker-provisioning session (boot, provision, VERIFY, clean shutdown). M: taking
`S1b-marker`. S: the S2 session (boot, non-destructive preflight, clean shutdown, taking `S2-<commit>`). **I(stage): the initial run of one
named stage.** Its scope is exactly: restore `S2-<commit>` once; cold boot; the 10 min settle; that stage's whole ladder (every
listed level, 3 initial cycles per level, plus for A1, A2 and A3b the five further L6 runs; maximum 23 / 12 / 10 per the table above); evidence collection; and it **ends at the first failure of any
category** or at completion. It authorizes no second restore, no repetition, no other stage and nothing after a failure. **Before the
approval is consumed, the runner prints and logs the exact stage, the three switch values, the levels, the snapshot to restore (name
and UUID), and the planned maximum number of runs (cycles x levels, or the 10 A4 runs; stated explicitly, never inferred from the stage
name)**, requires the person's **explicit confirmation of that summary**, and the request for approval shows the same text; a run that would exceed that maximum is refused. Approval to create S2 is **not** approval to run any stage. R: one recovery restore-and-run unit, or a named batch (the approval boundaries above).

| Category | Examples | Action |
|---|---|---|
| Functional test failure | An assertion fails with Explorer alive and the shell pid unchanged | Stop climbing the ladder. Collect evidence, preserve the VM state, report. After approval (per the boundaries above): restore the documented baseline and run **3 additional repetitions at the failing level**, each from the same restored baseline |
| Crash or Explorer process failure | The shell or an `explorer.exe` crashes, exits, hangs or restarts | Stop immediately; preserve evidence and VM state; report. Restore and repeat (3 times, same level) only after approval, per the boundaries above. An NFR-01 signal; never downgraded by a later category |
| Safety-gate or disk-guard failure | The gate is false or unreadable at any evaluation point; free space <= 15 GB; a stop flag for this run; no fresh monitor heartbeat or free-space sample; a destructive condition no longer holds | Stop **all** test activity. **No repetitions**, automatic or otherwise, until the cause is resolved and the user approves. Report |
| Evidence collection failure | A required item could not be collected or its hash does not match | The run is **inconclusive** (neither pass nor fail); stop progression, **no automatic repetition**, until the evidence problem is resolved and the user approves. A crash is still reported as a crash, with the evidence marked incomplete |
| Infrastructure failure | VM will not boot, share unavailable, runner or script error, snapshot restore fails | Stop the current stage and report it **separately** (no restore or retry without approval); it is not interpreted as a tracker failure and is not counted in the failure statistics |

The starting snapshot is the same **documented clean baseline** (`S2-<commit>`, cold boot, 10 min settle) for the initial run and
for every approved repetition, and is recorded for each run. After the approved repetitions, stop and report.

**Load delivered (the L6 tab limit).** Explorer is known to stop accepting tabs at about 46 (09 O-27), so L6's 50 tabs cannot all open.
Every window records `tabs_requested` and `tabs_opened` (and the run records `windows_requested` and `windows_opened`). The driver
opens tabs until the request is met or Explorer stops accepting them (a further open that does not raise the count after 3 attempts
over 2 s), then continues with the tabs it has; **reaching the limit is expected and is not a failure.** A level counts as **delivered**
when `windows_opened == windows_requested` and, **for every window**, `tabs_opened == tabs_requested` for L1-L5, and for L6 when `tabs_opened >= 44` per window
(the known limit of about 46 less a margin of 2; tunable, and changed only by a spec edit after the first VM measurement). If the load
is **not delivered**, the level result is **inconclusive (load not delivered)** with the counts: not a pass, and not a failure of any
category. Any other result is judged only on a delivered level.

Every result row records: OS build and UBR; `explorer.exe` and `SHELL32.dll` versions; level, cycle and repetition number; `windows_requested`/`windows_opened` and `tabs_requested`/`tabs_opened`; whether the level was delivered;
display configuration `1024×768 at 125%`; start and end timestamps with the time zone (ISO 8601 with UTC offset, plus the zone id);
the starting snapshot name and UUID; the three switch values; the harness and shell session ids and the harness's elevation state; result; and failure details (exception, event ids, dump paths).

### H5. Process monitoring and evidence

- **Process monitoring is the shell-process handle plus a supplementary 250 ms poll; nothing more in the first implementation.**
  The harness holds a handle to the shell process (`SYNCHRONIZE` plus `PROCESS_QUERY_LIMITED_INFORMATION`): that gives a reliable
  termination notification for **that process only**, without polling, read-only. The poll identifies newly observed `explorer.exe`
  PIDs. **Neither is a guarantee that every short-lived `explorer.exe` is captured** (a process that lives less than 250 ms can be
  missed), so results say "observed", never "complete". **No elevated listener (ETW/WMI trace) is added at this stage** (user
  decision 2026-11-04); it is reconsidered separately only if the evidence proves insufficient. The Application log (1000/1001/1002)
  and the WER reports are the cross-check.
- **Observed time and actual exit time are different facts and are recorded apart.** `observed_at` is when the watcher saw the
  termination (the wait returned, or the poll noticed): UTC plus a monotonic reading. `exit_time` is the process's **actual** exit
  time, read from the terminated process's handle with `GetProcessTimes` (`lpExitTime`, UTC); it is recorded only if that call
  succeeds, otherwise `null` with the reason. `observed_delay = observed_at - exit_time` is recorded when both exist. The creation
  time comes from the same call, and `exit_code` from `GetExitCodeProcess`. An exit seen only by the poll (no held handle) has
  `observed_at` only and `exit_time: unknown`: a PID present in one poll and absent in the next is reported as exactly that, never
  given an invented exit time.
- **Shell-handle lifecycle.** The watcher holds a handle for each `explorer.exe` it knows, starting with the current shell, and
  keeps every handle open until the run ends, so exit time and exit code can still be read after the fact. When the shell's handle
  signals termination it records the exit data, then looks for the replacement: on every 250 ms poll it reads the shell window's
  process (`GetShellWindow`), and when a new shell process appears it **opens a handle for it** with the same access, increments
  `shell_generation`, and records its pid and creation time. A newly observed `explorer.exe` that is alive is opened the same way when
  first seen. If a pid is seen to change without a termination signal (a missed exit), the old handle, still held, supplies the exit
  data, and the event is marked "termination not directly observed". An `OpenProcess` failure (already exited, access denied) is
  recorded with the pid and the error, never ignored. Tests with a fake process API cover a shell restart, a second restart, a failed
  open, a pid change without a signal, and the handles all closed at the end of the run.
- **Evidence collection is read-only with respect to system configuration:** it copies, it does not move or delete (apart from the aborted-copy cleanup in H6), and it changes no
  setting, service, policy or registry value. For every item the manifest records the source path, collection time, size and
  **SHA-256**; the copy goes to `out`. Collected: event-log entries for the test window (`wevtutil epl` with a time-window query),
  WER `ReportArchive`/`ReportQueue` entries, the Explorer dumps in `C:\dumps`, the lab journal and the version table.
  Originals stay where they are. A collection failure (missing, locked, unreadable, copy mismatch) is recorded as an explicit
  `FAILED: <reason>` manifest entry; it is never omitted silently, and a run with a failed collection is marked incomplete. Copies obey the evidence-space rules in H6.

### H6. Disk safeguards

**Units and thresholds.** In this spec **GB means GiB** (1,073,741,824 bytes), the unit Windows and PowerShell report and in which the
38.5 and 35.00 GB figures of 09 were measured. The implementation works in bytes and compares **inclusively** as stated:

| Threshold | GiB | Bytes | Rule |
|---|---|---|---|
| Start gate | 25 | 26,843,545,600 | A run, stage or snapshot may start **only if** host free space is **greater than** this (the 15 GB stop plus the 10 GB evidence budget) |
| Warning | 20 | 21,474,836,480 | Free space **at or below** this: prominent warning, no stop |
| Hard stop | 15 | 16,106,127,360 | Free space **at or below** this: stop (below) |
| Evidence floor | 5 | 5,368,709,120 | An evidence copy proceeds only if free space **after** the copy would be **greater than** this |
| Evidence budget | 10 | 10,737,418,240 | The most evidence copied for one run (about 3 full dumps of up to 2 GiB, 1 GiB of logs and WER reports, and a margin; a planning value, confirmed by the first real dump size and changed only by a spec edit) |

Boundary tests use exactly the threshold, one byte above and one byte below each.

At or below the hard stop: start no new test activity (no new action, cycle, level or stage), take no further snapshot, preserve
existing evidence, report, and wait for the user's approval. **Never delete files or snapshots to recover space** (the only deletion anywhere in the harness is the narrow cleanup of an aborted evidence copy's own incomplete destination file, below). At or below the
warning threshold the harness logs a **prominent warning** with the remaining space in the console, the run log and the result row; it
does **not** stop a run merely for crossing it. Host C: free space is checked **before each stage and each snapshot, periodically
during execution, and after each stage**, by a host-side read-only monitor, not by the guest (plan § 8).

- **Hard-stop exception (exact).** After a hard stop **no new test action, snapshot, restore, boot or discretionary operation may start.** **Evidence copying is the sole exception, in two distinct cases:** a **new** evidence-copy operation may start only under the emergency rule below (cumulative budget and floor, checked immediately before it); a copy **already in progress** when the stop occurs continues only while the floor and the budget remain satisfied, checked before every chunk, and aborts otherwise (below).
- **Emergency evidence copy: the precedence rule.** The hard stop and the evidence rules govern **different things and do not
  conflict.** The hard stop forbids **test work and discretionary operations**: no new action, cycle, level or stage, no snapshot, no
  restore, no non-essential copy. **Evidence preservation is not a test operation and may continue at or below the hard stop** under
  the rules here, which are the only permission it has. The copy is read-only with respect to the guest and creates files only under
  `out`. It is allowed only while **all** hold: (1) `completed_evidence_bytes + next_file_size <= 10 GiB`: the budget is **cumulative for the run** (never checked per file alone), and `completed_evidence_bytes` counts successfully retained evidence only, never the bytes of an aborted, deleted partial copy;
  (2) `free - size` is **greater than** the 5 GiB floor, using a fresh sample taken immediately before the file; (3) the copy is of an
  item the manifest names (no new kinds of copy). Example: free 14 GiB (hard stop active), a 7 GiB dump: `14 - 7 = 7 > 5` and the
  budget holds, so it **is** copied; a 10 GiB dump would leave 4 GiB, so it is not. If the floor cannot be kept the collector **does
  not start another copy**: files are processed smallest first, so a refusal means every remaining file is at least as large.
- **A copy already started is chunked and watches the floor.** Another process can consume host space during a copy, so a copy
  proceeds in chunks of at most 64 MiB and, before each chunk, takes a fresh free-space sample and requires `free - chunk` to stay
  **greater than** the floor. If it would not, the collector **aborts that copy**, closes the destination, and **removes only the
  incomplete destination file created by that specific copy operation** (the one narrow exception to "never delete files"; it
  never deletes or modifies the source, completed evidence, other run files or anything else, and it identifies the file by the
  path and handle it created, never by pattern), records `FAILED: host free space fell to floor during copy (file,
  bytes copied, free, floor)`, starts no further copy, and marks the run incomplete. The record carries the bytes copied, the free space at the abort, and the
  **deletion outcome** (deleted, or the error). **If the deletion fails**, the collector reports the remaining disk usage and the
  path, does **not** retry automatically, and the run stays stopped awaiting the user. The evidence collector must never be the reason
  the host runs out of disk. Tests: the free-space function is faked to drop at a chosen chunk; the partial removal is asserted to
  touch only that copy's own destination file (source, completed evidence and a sibling file are byte-identical afterwards); a
  failing delete is recorded, not retried, and leaves the run stopped; the record has all three fields.
- **Collection order and failures.** Files are copied smallest first (event logs, WER reports, the lab journal, then dumps). Before
  each file the checks above run. A file that fails them is **not copied**: the manifest records `FAILED: insufficient host space
  (size, free, floor)` or `FAILED: evidence budget exceeded`, the run is marked incomplete, and both the evidence-collection and the
  disk-guard categories apply (H4). Nothing is lost: the VM state is preserved awaiting approval, so the original stays in the guest.
  Free space is re-sampled after each file.
- **Snapshots and host disk growth.** The start gate (>25 GiB) is a precondition, **not** a bound on what a snapshot operation or a
  later run consumes: a VirtualBox differencing disk grows as the VM writes. So: (a) snapshots are taken only with the VM **powered
  off** (V/P/M/S sessions), when creation adds a new empty differencing image and the guest is not writing, so the operation's own
  growth is small; the growth that matters happens in the runs after it and is covered by the periodic monitor and the hard stop;
  (b) the **disk monitor stays active during** a snapshot take or restore, and its readings are logged with the operation; (c) a
  snapshot take or restore is **never cancelled or interrupted by the harness** (no killing `VBoxManage` or the VM process): an
  interrupted operation can leave the VM or its disk chain inconsistent, so it is allowed to finish or to fail by itself; (d) if the
  hard stop is crossed during it, the operation is left to end, then: no further snapshot, restore or boot, report with the readings;
  (e) if it **fails for lack of space** (or any reason), it is an infrastructure failure plus a disk-guard failure: nothing is
  retried, no snapshot or file is deleted to make room, the VM and snapshot state are inspected read-only (`showvminfo`, `snapshot
  list`) and reported, and the new snapshot is treated as **not established** until the user decides.
- **Stop flags belong to a run.** The guest runner creates a run id at start (`yyyyMMddTHHmmssZ-<4 hex>`) and writes
  `out/runs/<run-id>/run.json`; the monitor writes `out/runs/<run-id>/stop-disk.json` (run id, UTC time, free bytes, threshold) into
  **that run's folder only**. The runner honours a parseable flag in its own folder that carries its own run id; a parseable flag with
  another run id is ignored and logged as stale. A `stop-disk.json` that exists in the run's own folder but cannot be parsed is treated
  as a **stop** (fail closed) and logged. Flags are never deleted or reused (nothing is deleted), and a new run never reads an older
  run's folder.
- **Two different intervals, both required.** While a run is active the monitor (a) **samples host C: free space at least every
  30 s** and (b) writes `out/runs/<run-id>/monitor-heartbeat.json` **every 15 s**. A heartbeat alone proves only that the monitor
  process is alive, so each heartbeat carries `seq`, `heartbeat_utc`, `sample_seq`, `last_free_space_sample_utc` and `free_bytes` from
  the most recent sample.
- **Files are never trusted half-written.** The monitor writes each file to a temporary file in the same folder and renames it into
  place. Atomic rename on the VirtualBox shared-folder provider is **not established** (as for `prep-admin.ps1`), so nothing relies on
  it: the runner parses the whole document and validates its schema, and a partial, unparsable or invalid heartbeat counts as "no new
  heartbeat". The runner keeps the last valid one, re-reads up to 3 times 100 ms apart, and fails closed only when the freshness limits
  below are exceeded.
- **Freshness has one authority, the runner's monotonic clock; timestamps are consistency checks, not freshness.** The heartbeat
  carries `seq` (strictly increasing) and the sample carries `sample_seq`. The heartbeat is fresh if `seq` has increased within the last
  60 s of the runner's monotonic time, and the sample is fresh if `sample_seq` has increased within the last 120 s. This needs no
  agreement between the host and guest clocks. A valid monotonic result is never overridden by a wall-clock comparison **except for a
  defined reason, `CLOCK_INCONSISTENT`, which invalidates the heartbeat**.
- **Timestamp checks that do not assume synchronized clocks.** All three are testable on a fake monotonic clock and a fake UTC clock.
  (1) *Ordering, host clock only, by field:* **`heartbeat_utc`** must be **strictly greater** than the previous valid heartbeat's.
  **`last_free_space_sample_utc` (the sample time)** must **not move backwards**, and while `sample_seq` is unchanged it **must be
  identical** (the same sample is legitimately repeated by the 15 s heartbeats of a 30 s sampling interval; it is never required to
  increase per heartbeat); when `sample_seq` increases it must be **strictly greater** than the previous sample time (a new sample is a new measurement at least one clock tick later; unchanged `sample_seq` identical; decreased `sample_seq` invalid); if `sample_seq`
  is unchanged but the time differs, or `sample_seq` goes backwards, the heartbeat is invalid. Also, the sample time is not later than
  `heartbeat_utc`, and `heartbeat_utc - last_free_space_sample_utc` is at most 120 s. (2) *Rate consistency:* at the
  first valid heartbeat the runner records the baseline (its monotonic reading, the heartbeat's `heartbeat_utc`, and the guest-minus-host
  UTC **offset**, which is only recorded in `run.json` and never required to be near zero, since the machines may legitimately differ;
  an offset beyond 5 min is logged as a warning). For every later valid heartbeat the elapsed host time
  (`heartbeat_utc - baseline_utc`) must equal the elapsed runner monotonic time within +/-30 s (the 15 s write interval plus slack).
  A timestamp that runs ahead of that (a "future" one), lags behind it (an "old" one), or a clock step on either machine, for example
  after a snapshot restore, breaks the equality and is `CLOCK_INCONSISTENT`. (2b) *Guest wall-clock steps.* At the first valid heartbeat the runner also records its own **guest UTC** reading (the baseline). At every later valid heartbeat (one whose `seq` increased) it reads guest UTC again, and the **elapsed guest UTC** (`guest_utc_now - baseline_guest_utc`) must equal the elapsed runner monotonic time within +/-30 s; otherwise `CLOCK_INCONSISTENT`. Only elapsed values are compared, so a **constant** guest-host offset of any size passes (it is only recorded), while a guest clock step (as after a restore or a time sync) of more than 30 s in either direction fails. A step smaller than the tolerance is below the detection limit and is accepted; the spec makes no stronger claim. (3) Absolute comparison with the guest's own UTC clock is
  **not** used to decide freshness. A `CLOCK_INCONSISTENT` result is a disk-guard failure reported with both readings; it is
  resolved (clock sync, a fresh run) before the user approves a restart.
- **Disk-guard failure** is any of: no valid heartbeat yet, an expired freshness limit, `CLOCK_INCONSISTENT`, a stop flag for this run
  (or an unparsable one in its folder), or `free_bytes` at or below the hard stop.
- **Stop behaviour for work in progress.** The runner evaluates the disk guard before **every individual test action** (each window or
  tab open or close, each key send, each freeze, kill, P4 or P7 step) and at least once per second during any wait or settle period,
  so waits are interruptible; it does not wait for the next cycle or phase boundary. When it fails: the single action already in
  flight is allowed to finish (a call is never aborted halfway) and that completion is the **safe interruption point**; **no further
  test action is issued and no new operation starts**; the rest of the cycle is abandoned; lab windows already open are left as they
  are (no wind-down churn; they are recorded in the journal); and the run is classified as a disk-guard failure (H4). **Evidence
  collection already begun is not interrupted by the disk guard** (it continues under the emergency rule above and stops only for the
  floor or the budget); evidence of a crash that was not yet collected is still collected within those rules (a read-only copy, and the
  one thing that cannot be redone after a restore). Nothing else continues.

### H7. Instrumentation boundaries

- **`CoCancelCall` record.** One record per cancel attempt, from the watchdog's injectable cancel delegate (test-side):
  `utc` (ISO 8601), `mono_ms` (monotonic), `worker` (id and generation), `operation` (name and `CallJournal` sequence number),
  `target` (server pid and creation time from the journal, or `unknown`), `site` (breadcrumb), `cancel_hr` (the HRESULT **returned by
  `CoCancelCall`**, as `0x%08X` plus its symbolic name only where the SDK headers name it, else `unnamed`; never guessed),
  `blocked_call_returned` (bool), `returned_after_ms` (cancel to return of the blocked call, or `null`), `blocked_call_hr` (the
  HRESULT the cancelled call itself returned, or `null`; kept apart from `cancel_hr`), and `abandoned_workers` (count after the attempt).
- **Evaluation timing** (extends the "Evaluation cost" paragraph above). **Total** runs from the engine's first action in an evaluation
  to the point its result is returned to the caller, on one monotonic stopwatch. **COM wait** is the sum of intervals around each
  *outermost* gateway call (create, enumerate, read path, release), from just before the call to just after it returns; a nested call is
  not added again, and a WinEvent callback that runs inside the wait is part of the wait. **Internal = total - COM wait**, a residual: it
  includes engine logic, cache work, journal bookkeeping, the stopwatch reads themselves, and scheduling or GC pauses on the worker.
  The two figures are **not** claimed to account for all elapsed time. Excluded from both: time queued in the scheduler before the
  evaluation starts, publish latency to the UI thread, and anything after the result is returned. End-to-end latency (REQ-031) is
  measured separately. A test asserts `COM wait <= total` and that interval sums never exceed total. The samples are recorded in
  `TrackerCounters` and exposed through new `TrackerStats` fields (total, COM wait, internal: p50/p95/max).
- **No behaviour change.** Instrumentation must not alter tracker decisions, timeouts, the cancel-then-abandon sequence, cancellation
  behaviour or state transitions. Required checks: the same fake-clock scenarios produce identical state traces, decisions and
  published results with instrumentation on and off; no extra COM call.
- **Non-blocking, and honest about what is lock-free.** There are two kinds of buffer, with different mechanisms. Neither does I/O,
  COM calls or waiting inside its critical section or enqueue path.
  1. **Cancel-record buffer: a bounded, lock-free multi-producer / single-consumer queue** (capacity N = 64, a power of two; positions
     and `seq` are **`ulong`** counters that wrap modulo 2^64; slot index = position & (N - 1); N divides 2^64, so the slot index stays
     consistent across the wrap, and N is far below half the range (2^63), which makes the signed comparison below sound). *Producers:* in practice the tracker's one dedicated watchdog thread
     (`ExplorerTracker.Watchdog`), the only thread that issues `CoCancelCall`, which lives as long as the tracker and **persists across
     worker generations**, so abandoned and replacement workers never write cancel records. The design **does not rely on that**.
     *Contract.* Each slot has a 64-bit `seq` and a payload; initially `slot[i].seq = i`. For the use of slot `i` at position `p`
     (`p mod N = i`) the slot is **free** when `seq == p`, **published** when `seq == p + 1`, and **released for the next lap** when
     `seq == p + N`. The lap is encoded in `seq`, so a reused slot is never mistaken for an earlier use (no ABA). *Comparison semantics (one rule for producer and consumer).* Never compare positions or
     sequences with a raw `<` or `>`. Define `diff(a, b) = (long)unchecked(a - b)` (modular subtraction read as signed 64-bit). Because
     every live `seq` is within N of the position it is compared with, `diff` is in [-N, +N] and its sign is correct even when the
     counters straddle 2^64 (and when `enq` is near `ulong.MaxValue`). *Producer:* build the
     record first (a builder that throws never reaches the queue); read `enq` with `Volatile`; let `d = diff(slot[enq & mask].seq, enq)`. If
     `d == 0`, try `Interlocked.CompareExchange(enq, enq + 1)`: **only a successful exchange claims the slot**, after which the
     producer writes the payload and publishes it with a release store of `seq = enq + 1` (a claimed slot is always published, so no
     unconsumable gap can exist). If `d < 0`, the slot still holds an unconsumed record of the previous lap: the queue is **full**,
     the record is **dropped** and `dropped` is incremented, and **nothing was claimed**, so a full queue needs no rollback and creates
     no gap. If `d > 0`, another producer advanced: reload `enq` and retry. Retries are bounded (8 attempts), after which the
     record is dropped and counted, so every call completes in bounded steps. *Consumer (one thread):* read its own `deq`;
     acquire-read `slot[deq & mask].seq`; if `diff(seq, deq + 1) == 0` (with `deq + 1` computed `unchecked`), read the payload, then release-store `seq = deq + N` (freeing the slot
     for the next lap) and increment `deq`; otherwise the queue is empty, or the next record is claimed but not yet published, and the
     consumer retries later. It never skips a claimed slot, so records come out in claim order; the accepted cost is a short
     head-of-line delay while a producer is between claim and publish. *Memory ordering:* the payload write happens-before the release
     store of `seq = p + 1`; the consumer's acquire read of that `seq` happens-before its payload read; the consumer's payload read
     happens-before its release store of `seq = p + N`, which a later producer acquire-reads before it overwrites the slot; the
     compare-exchange is a full fence. *Reservation assumption (stated, not hidden).* A claimed slot blocks the consumer until it is published, so the
     design **assumes a producer that wins the compare-exchange publishes**: between the exchange and the release store there is
     **no allocation, no call that can throw, no lock, no I/O and no wait** (the payload is copied from the pre-built record into
     preallocated slot storage by plain field stores; a source guard checks this region). A producer that is merely descheduled
     delays the consumer (head-of-line), never the other producers, which fill to N and then drop and count. A producer that dies or
     is stopped between claim and publish cannot be recovered by this design; in this process that cannot happen by exception
     (nothing in the region throws) and the watchdog thread is never aborted, and the spec makes no stronger claim.
     *Many producers on a full queue:* every one sees `d < 0`, drops and counts, and none
     blocks. *Tests, in addition to those below:* wraparound far beyond N laps; **counters seeded near the integer limits**, not only the slot index wrapping:
     `enq = deq = ulong.MaxValue - k` for k = 0 .. 2N (so the claim, the publish, the free-for-next-lap store and the full test all
     occur on both sides of the 2^64 wrap), plus seeds at 2^32 and 2^63 as regression guards against a narrowed type or a raw compare; a
     unit test of `diff` at the extremes (0, 1, -1, +/-N, `ulong.MaxValue` against 0); with the consumer stalled, exactly N records accepted and every further one dropped, `accepted + dropped` equal to the
     attempts; a seam between claim and publish (the consumer neither skips nor reads the unpublished slot, and reads it once after
     publication; with that producer held, other producers fill the remaining N - 1 slots, then drop and count, and everything
     drains in claim order once it is released); a seam between the consumer's payload read and its release store (a producer still sees the slot as full); several
     producers racing a slow consumer (no torn record, no duplicate, per-producer order kept, no loss other than counted drops).
  2. **Timing-sample windows (total, COM wait, internal): lock-based, neither single-writer nor lock-free.** They live in
     `TrackerCounters`, which is **shared by every worker generation** (so that counts survive a replacement). *Writers:* whichever
     worker finishes an evaluation, which can in principle be an abandoned worker that wakes after its replacement started, so
     overlapping writers are expected and are made correct by the **existing** `_gate` lock, not by a single-writer assumption. The
     instrumentation adds **no new lock**: `RecordEvaluation` writes the three samples of one evaluation (total, COM wait,
     internal) in the **same** critical section, so a reader never sees a mixed set. *Readers:* `Percentiles()` (reached from
     `GetStats()` on any thread) copies the fixed windows under the lock and releases it before sorting and computing percentiles.
     The critical sections contain **only reads and writes of the preallocated arrays**: no I/O, no COM call, no wait, no callback.
     *What this does and does not guarantee:* a worker can wait for another thread's bounded, memory-only critical section (another
     writer's three stores, or a reader's copy). That is an accepted property of the existing mechanism, not wait-freedom, and the
     spec makes no stronger claim. The watchdog thread never takes this lock (its cancel path uses the lock-free queue above).
  3. **Reader behaviour.** The drain thread obtains a consistent view of the cancel queue slot by slot (published sequence, then
     read, then release). Statistics readers obtain a consistent view of the timing windows by the copy under the lock described above.
     Neither reader holds anything while doing I/O.
  4. **Tests, none with a latency threshold.** A record builder or sink that **throws** is swallowed with identical decisions and state
     trace, and the failure is counted. A **full queue**: the producer is run while every other thread, including the drain thread, is
     held on a gate the test never releases; the call must return, `dropped` must increment, and decisions must be unchanged. The
     test is non-blocking by structure (if the producer needed any other thread it would hang), guarded by a generous **hang guard that
     fails only on a deadlock and is not a performance bound**. A drain thread blocked forever does not block a producer. A concurrent
     hammer with several producers and a consumer accepts no torn record and loses none that was not counted as dropped. For the
     timing windows: two overlapping simulated worker generations plus a reader hammer, asserting that every snapshot is an unmixed
     set (count and total = COM wait + internal per sample), that `Evaluations` equals the samples recorded, and that no exception
     occurs. Source guards: no blocking primitive on the enqueue path; the `_gate` critical sections contain only array reads and
     writes and no call out of the class; the watchdog path does not reference the `TrackerCounters` lock.

### H8. Build and deployment separation

- **Host builds, guest runs.** The host builds the Release test executables and may run only the non-real tests. The host **never**
  executes `Kind=RealExplorer` or `Destructive` tests (the H1 gate also refuses them there). Nothing is built in the guest (no SDK).
- **Layout (exact).** `stage-vm.ps1` builds `artifacts/vm/stage/<commit>/` and copies it to `drop/stage/<commit>/`, which must contain these three **required** entries:

  ```
  drop/stage/<commit>/
      run-lab.ps1           bootstrap runner; OUTSIDE the manifest and the payload; its hash is pinned separately
      stage-manifest.json   authenticating document; OUTSIDE the payload; lists the payload only, never itself or the runner
      payload/              the authenticated payload: test executables, runtime files, collector and watcher scripts
  ```

  **Required, not exclusive.** Other entries in the stage directory are permitted but ignored: they are never executed, never part of
  the integrity check, and never make the runner refuse. A missing required entry is a refusal. The strict check (any unlisted file is
  an extra and a refusal) applies inside `payload/` only.

  The manifest therefore has no circularity: it describes `payload/` and nothing else. Its paths are relative to `payload/`; each
  entry has path, size and SHA-256, and the document carries a format version and the `<commit>`. The staging allowlist (and the
  secret scan) apply to `payload/`; the runner is staged separately from a fixed source path.
- **Staging checks** (host-side, before anything lands in `drop`): the payload is built from an explicit allowlist and scanned, and
  staging is refused if it contains credentials or secrets (private keys, tokens, `.env`, publish profiles), a user notes database,
  host results from `artifacts/t325`, logs, dumps, `.git` or any file not on the allowlist. After the copy the three entries are
  re-verified in `drop`. The script writes `stage-report.txt` (host only) with the SHA-256 of `stage-manifest.json`, the SHA-256 of
  `run-lab.ps1` and the payload file count; these are the **pinned values**.
- **Authentication, in this order.**
  1. *The runner cannot authenticate itself, so a person checks it every time.* **Immediately before every execution of
     `run-lab.ps1`, not only the first,** the person compares the SHA-256 of the file in the guest (`Get-FileHash`) with the value in
     the trusted host-side `stage-report.txt`, and does not run it on a mismatch. The runner's own check is **no substitute**: it takes
     `-ExpectedRunnerSha256` and refuses if its file hashes differently (this catches a stale or mismatched copy only; a modified
     runner could skip it). It logs its own hash at every start, so the run record shows which runner ran and a skipped comparison
     is visible afterwards. The comparison is recorded in the run notes (in 09 for S2).
  2. *The manifest is authenticated before it is parsed.* The person passes `-ExpectedManifestSha256` (from `stage-report.txt`,
     recorded in 09 when S2 is approved, **never read from `drop`**). The runner reads `stage-manifest.json` **once into a byte array**,
     hashes that array (byte-exact, no normalisation) and compares; only if equal does it **parse that same array**. It never reopens the
     file between hashing and parsing, so there is no time-of-check/time-of-use gap. A missing, malformed (not 64 hex characters) or mismatching value makes it refuse
     (exit 1) before it reads anything else from the stage directory.
  3. *Only then is the manifest parsed and validated:* format version known; `<commit>` equal to the directory name **and matching
     `^[0-9a-f]{7,40}$`** (lowercase hex only; the directory name is validated the same way before it is used in any path); and every
     path passes the **path rules** below. Any violation refuses the whole manifest (no entry is skipped or repaired).
     *Path rules (Windows-specific; the guest executes what these paths name).* A path is a sequence of components joined by `/`
     (the only accepted separator; a backslash is rejected). Each component must: be non-empty; not be `.` or `..`; consist only of
     `[A-Za-z0-9._-]` (an **allowlist**, so a colon (alternate data stream `name:stream`, drive letters), `<>"|?*`, control characters,
     `~` (8.3 short names), `$`, spaces, non-ASCII and anything else is rejected); not end with `.` (or a space; spaces are rejected
     anyway); and not be a **reserved DOS device name**, compared case-insensitively **with or without any extension** (`CON`, `PRN`,
     `AUX`, `NUL`, `COM0`-`COM9`, `LPT0`-`LPT9`, `CONIN$`, `CONOUT$`; for example `nul.txt` and `Com1.dll` are rejected). Component length
     <= 100 and whole path <= 200. No leading `/`, no UNC or device-namespace prefix (`\\?\`, `\\.\`), no empty component (`a//b`).
     *Equivalence:* duplicate detection compares an **ordinal case-insensitive** key of the full path after the above (so paths that
     become equal under Win32 normalization or case folding collide). *Resolution check:* the runner resolves each path under
     `payload/` and requires the resolved full path to start with the `payload/` root plus a separator and to equal the plain
     concatenation (so any surprise from normalization is a refusal); a path whose existing file or any parent directory is a
     **reparse point** (symlink, junction) is rejected. Tests: one case per rule (ADS, each device name with and without extension,
     trailing dot, backslash, `..`, `a//b`, drive and UNC forms, `~1`, over-length, case-fold duplicate, invalid `<commit>`).
  4. *Payload verification:* every listed file exists under `payload/` with the listed size and SHA-256; any file under `payload/`
     that the manifest does not list is an **extra** and makes the runner refuse. **The extra-file check applies to `payload/` only.**
     `stage-manifest.json`, `run-lab.ps1`, any other file in the stage directory and the rest of `drop` (`prep-*.ps1`,
     `restore-admin.ps1`, `dotnet`, ...) are not extras, are never rejected by this check, and are never executed (the runner runs only
     itself and the verified local copy of `payload/` described next).
  5. *Execution is from a verified guest-local copy, and only after the disk guard is established.* The order is: authenticate
     (step 2), validate (step 3), evaluate the gate, **create the run id and `out/runs/<run-id>/` and confirm the monitor** (a valid
     heartbeat, free space above the 25 GiB start gate), and only then copy. The copy writes to the guest's virtual disk and so to
     host space: the disk guard (H6) is evaluated **before each file copied** and a breach stops the copy (nothing is run; infrastructure
     plus disk-guard failure). The runner copies `payload/` to `C:\T325\run\<run-id>\payload\` (guest-local,
     outside the share), hashing each file's bytes **as it copies them**, compares those hashes and sizes with the manifest, and runs
     only the local copy. A host-side change to the share after verification therefore cannot reach what runs. The local copy is
     read-only to the harness and is never modified during the run.
  Deployment invariants: `drop` is mounted **read-only** in the guest (plan § 8 change log); the host never modifies
  `drop/stage/<commit>/` after the manifest hash is pinned, and the staging script **refuses to overwrite an existing `<commit>`
  directory** (a new commit gets a new directory).
  A **coordinated change of a payload file and its manifest entry** alters the manifest bytes, so step 2 fails against the pinned
  hash and the runner refuses. A signature verified against a pinned public key is the alternative, not part of the first
  implementation. A coordinated change of the staging output and of the person's out-of-band copy is outside the accident threat
  model of these gates.
- **The guest runner enforces the complete safety gate** (H1, and H2 for destructive runs) *before* running any lab test, and the
  test code enforces it again; neither relies on the other.
- **S2 snapshot:** taken only on the user's explicit approval.

## Explorer stability (NFR-01)

- **Stress test:** a script hovers 1,000 folders through UI Automation while the app writes notes
  in a loop. Pass criteria: no Explorer crash, every `GetInfoTip` under 50 ms, and no DLL leaks
  after `DllCanUnloadNow`.
- **DB missing / locked / corrupt:** the handler returns `E_FAIL` quickly in each case.
- Run the handler under Application Verifier (heaps, handles, locks) once per release.

## Manual checklist (maintained in `tests/manual-checklist.md`)

Windows 10 22H2, Windows 11 23H2, Windows 11 24H2 × {100%, 150%, mixed-DPI multi-monitor} ×
{light, dark, high contrast}: panel docking, snapping, maximize, tabs, toasts, context menu tiers.
