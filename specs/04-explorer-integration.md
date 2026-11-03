# 04 — Explorer Integration

This is the highest-risk part of the product. Every mechanism below has a **Phase 0 spike** that must
confirm it on Windows 10 22H2 and Windows 11 23H2/24H2 before production work starts.

---

## 4.1 Context menu: "Add / Edit folder note…" (REQ-003/004)

Two tiers:

**Tier 1 (Phase 3, no DLL):** a classic registry verb, per-user, no admin rights.
```
HKCU\Software\Classes\Directory\shell\FolderNotes
    (Default)        = "Folder note…"
    Icon             = "<install>\FolderNotes.exe,0"
    \command (Default) = "<install>\FolderNotes.exe" --edit "%V"
HKCU\Software\Classes\Directory\Background\shell\FolderNotes   ← right-click empty space inside a folder
    \command (Default) = "<install>\FolderNotes.exe" --edit "%V"
```
On Windows 11 this appears under "Show more options". That's acceptable for the MVP.

**Who writes these keys (T3.1, ADR-024).** Never the app by itself: not on first launch, not in development,
test or `perf-check` builds. Three explicit routes only:
- the **installer** (T6.1) on install, through `FolderNotes.exe --register-shell`, and on uninstall through
  `--unregister-shell` (both headless: they write or remove the keys, print nothing and exit 0, or 1 on failure);
- the user, with the Settings toggle *Folder note in the right-click menu* (05 § Settings page);
- a Settings **Repair** when the registration points at another copy.

`ShellVerbRegistrar` reports the actual state from the registry: **Off** (neither key), **On** (both keys,
command and icon equal what this exe would write), **Stale** (anything else under our `FolderNotes` keys: another
exe path, a moved or deleted exe, a missing command, or only one of the two keys). The app only reads at startup
and shows the state in Settings; it never rewrites a Stale registration by itself. It only creates or deletes the
`FolderNotes` key under the two `shell` parents and leaves everything else in `Classes` alone. Paths with spaces
and non-ASCII characters are quoted; the folder argument is `"%V"`.

**Tier 2 (Phase 4b):** an `IExplorerCommand` implementation in `FolderNotes.ShellExt.dll`,
registered through a **sparse MSIX package** (`desktop4:FileExplorerContextMenus`,
`desktop5:ItemType Type="Directory"`), so it appears in the Windows 11 top-level menu. The label
changes dynamically: "Add folder note" or "Edit folder note" (`GetTitle` queries the DB, with the
same 50 ms budget as the tooltip).

## 4.2 Companion panel (REQ-030..038)

### S1 findings (2026-10-31, Windows 11 23H2 build 22631, single window + tabs)

All 39 scripted scenarios passed using **events only (no polling)**:

| Scenario | median | max | trigger that fired |
|---|---|---|---|
| Navigate (different folder name) | 49 ms | 107 ms | `EVENT_OBJECT_NAMECHANGE` |
| Navigate between two folders with the **same name** (`One\src` → `Two\src`) | 51 ms | 65 ms | `NAMECHANGE` (it fires on child windows too, so a title change isn't required) |
| Unicode path, virtual folder (This PC → null) | 50 ms | 54 ms | `NAMECHANGE` |
| New tab (Ctrl+T → Home → null) | 287 ms | — | includes Explorer creating the tab |
| Switch tab (Ctrl+Tab / Ctrl+Shift+Tab) | 100 ms | 117 ms | `NAMECHANGE` |
| Switch window | 28 ms | 72 ms | `EVENT_SYSTEM_FOREGROUND` |
| Minimize | 14 ms | — | `EVENT_SYSTEM_MINIMIZESTART` |

Learnings that are now **requirements on the implementation**:
- **Active tab = the *visible* `ShellTabWindowClass` child** of the frame, matched to the shell
  window entry whose `QueryService(SID_STopLevelBrowser) → IShellBrowser::GetWindow` returns that
  HWND. The "first child in z-order" heuristic disagreed twice in transient states, so it's a
  tie-breaker only.
- **Do not use C# `dynamic` for COM.** The `dynamic` binder's type-library cache isn't
  thread-safe, and it crashed (`ArgumentException`) when two STA threads used it. Use typed
  interop (CsWin32 / `[GeneratedComInterface]`).
- **Evaluation cost is the bottleneck.** Re-enumerating `IShellWindows` with late-bound calls
  costs an average of 34 ms (p95 89 ms) per evaluation, which is most of the tab-switch
  latency. The production tracker must cache the browser→(frame, tab HWND) map (refreshed on
  `DShellWindowsEvents`), query only the active tab's path, and coalesce bursts of `NAMECHANGE`
  events. **Target: under 10 ms per evaluation.**
- Hooks only *post* a message; all COM work runs on the tracker's STA loop (never re-entrantly
  inside a WinEvent callback).
- The polling fallback stays (it costs nothing while Explorer isn't in the foreground), at 500 ms,
  as a safety net only.

### Detecting the active Explorer folder (`ExplorerTracker`): design (T3.2, ADR-025)

> Supersedes the earlier step list (which had `DWebBrowserEvents2` sinks, a `DShellWindowsEvents`
> subscription and a 50 ms debounce). S1 and ADR-004 had already shown the sinks are not needed; this section
> is the single description of what T3.2 builds. Values marked **(tunable)** are starting points that T3.2.5
> measures and may change *in this spec* before the task is ticked.

#### A. Structure: pure logic vs COM

The tracker is split so almost everything can be unit-tested without Explorer:

| Part | Kind | Responsibility |
|---|---|---|
| `IShellWindowsGateway` | interface (COM behind it) | `TryEnumerateTabs()` → list of `(frameHwnd, tabHwnd, TabHandle)`; `TryReadPath(TabHandle)` → `PathResult`; `Dispose` releases everything it handed out. The only place COM exists. |
| `ComShellWindowsGateway` | COM implementation | Typed interop (§B). Used only on the tracker's STA thread. |
| `FakeShellWindowsGateway` | test double | Scriptable tabs, paths, failures and delays. |
| `TabMap` | pure | The browser→tab cache (§C). |
| `EvaluationScheduler` | pure state machine, takes a `TimeProvider` | The coalescing rules (§D). |
| `ComFailureClassifier` | pure | HRESULT → `Transient` / `Gone` / `Other` (§B.5). |
| `TrackerStateMachine` | pure | `Stopped → Running ⇄ Recovering`, wedge → `Quarantined` (`Waiting` ⇄ `Trial`) → `Running`, `Disabled` (§B.6, §F, §M). |
| `CallJournal`, `QuarantineRecord` | pure + sequence lock | The blocked-operation record per worker; the process-wide quarantine entries, evidence and trial budget (§M). |
| `SignalRouter` | pure + one `Interlocked` word | The pending-signal bits and the current worker's wake-up target; `Attach`/`Detach` (§B.7). |
| `HookThread` | Win32 glue (no COM) | The dedicated hook thread: message loop, install/re-scope/uninstall, trivial callbacks (§B.7). |
| `TrackerThreadBudget` | pure (in `TrackerProcessGuard`) | Process-wide thread counters per kind: live and abandoned (§B.6). |
| `ExplorerTracker` | glue | COM worker (STA) thread, hook thread, watchdog thread, publishing. No decision the pure parts could make. |

Lives in `FolderNotes.App/Explorer/`. **Not started by `AppShell` in T3.2**; T3.4 starts it behind the panel
setting (REQ-037). With the panel off the tracker is not running at all: no thread, no hooks, no timer.

#### B. COM interop

1. **Typed only (ADR-007).** Hand-declared `[GeneratedComInterface]` interfaces (decision D1 in 09; recommended)
   for exactly the surface used: `IShellWindows` (`get_Count`, `Item`), `IWebBrowser2` (`get_HWND` only),
   `IServiceProvider` (`QueryService`), `IShellBrowser` / `IOleWindow` (`GetWindow`, `QueryActiveShellView`),
   `IShellView` (cast only), `IFolderView` (`GetFolder`), `IPersistFolder2` (`GetCurFolder`) and the shell helpers
   `SHGetNameFromIDList(SIGDN_FILESYSPATH)` / `ILFree`. Every method is `[PreserveSig]` (raw `HRESULT`, classified by
   us). Validated by S5/P1 (§ H): vtable order and arity against the SDK headers, live calls, `VARIANT`, casts, release.
   `IDispatch`-derived interfaces are declared with their four `IDispatch` slots as placeholders and are only
   called through their vtable. No `dynamic`, `InvokeMember`, `Type.GetTypeFromProgID` + reflection, or
   `Activator` on a COM type. `Item(VARIANT)` takes a private blittable `Variant` struct (`VT_I4` index); probe P1
   proves this before the task relies on it. A test fails if `dynamic` or `InvokeMember` appears under `Explorer/`
   (same style as T3.1's source guard). **User decision D1:** every interface and method signature is validated by
   T3.2.1 *before* implementation (vtable order against the SDK headers and a live call per method, `VARIANT`
   marshaling, `HRESULT` handling incl. `[PreserveSig]` vs throwing, `QueryInterface`/casting, RCW lifetime and
   release). If the probes show unreliable interop, **switch to CsWin32; do not add workarounds around a wrong
   interop definition.**
2. **One thread owns every COM object.** One dedicated `STA` thread named `ExplorerTracker.Worker` with its own message
   loop (an STA must pump; the WinEvent hooks are *not* on this thread, see B.7). Every RCW is created, used and released on it.
   No COM reference leaves that thread; what crosses to the UI is the immutable `ActiveFolderState` (an nint and
   a string).
3. **Ownership.** Every RCW lives in a `ComRef<T>` (`IDisposable`; `Release()` = **`ComObject.FinalRelease()`**
   on the STA thread; idempotent). `Marshal.ReleaseComObject` / `FinalReleaseComObject` throw `ArgumentException` on
   source-generated RCWs (S5/P1), so they are never used. Owners: `ComShellWindowsGateway` owns the `IShellWindows` root; `TabMap` owns one
   `ComRef` per tab (the smallest interface that can read the path; the `IShellView`, folder and PIDL objects
   are acquired, used and released inside a single `TryReadPath`). Shutdown order: tab refs → root →
   `CoUninitialize`. A live-RCW counter (always on; it is one integer) is asserted to be 0 at the end of every
   gateway test. *Why prompt release matters beyond our memory:* a reference we keep to a closed tab keeps that
   object alive inside Explorer, so a stale reference is a small leak in Explorer, against principle 1.
4. **Not re-entrant.** While a thread that installed an out-of-context hook waits on an outgoing COM call, COM
   dispatches the **WinEvent callbacks** to it (S5/P2 measured this; posted thread messages are not dispatched). That
   is why the hooks moved to their own thread (B.7), where there are no COM waits. Callbacks still do nothing but set
   a bit and post at most one message, and the evaluation handler keeps a guard: if an evaluation is running, a new
   request only sets the dirty flag.
5. **Failure classification** (`ComFailureClassifier`). Nothing throws out of the loop: every entry point is
   wrapped, failures are counted, and logged at most once per 10 s per class.

   | Class | HRESULTs (examples) | Action |
   |---|---|---|
   | `Transient` | `RPC_E_CALL_REJECTED` 0x80010001, `RPC_E_SERVERCALL_RETRYLATER` 0x8001010A, `RPC_E_CALL_CANCELED` 0x80010002 | keep the entry; retry on the ladder (§D.4); publish nothing new until it resolves |
   | `Gone` | `RPC_E_DISCONNECTED` 0x80010108, `CO_E_OBJNOTCONNECTED` 0x800401FD, `RPC_E_SERVER_DIED` 0x80010007, `RPC_E_SERVER_DIED_DNE` 0x80010012, `RPC_S_SERVER_UNAVAILABLE` 0x800706BA, `RPC_S_CALL_FAILED` 0x800706BE, `RPC_E_INVALID_OBJECT` 0x80010114 | drop **that** entry (release it); if it came from the root `IShellWindows`, run recovery (§F) |
   | `Other` | `E_FAIL`, `E_NOINTERFACE`, `E_NOTIMPL`, `E_UNEXPECTED` | `path = null` for that tab this time; after 3 in a row on one entry, drop it. **Exception (S5/P1):** `E_FAIL` for a tab whose `IsWindow(tabHwnd)` is false is `Gone`: a closed window's objects return `E_FAIL`, not a disconnect code |
6. **Time-boxing** (a COM call has no timeout of its own). Two layers, because the first may not apply to every call:
   - *Cancel.* The STA thread calls `CoEnableCallCancellation` once. A **watchdog** (a separate thread that waits
     on an event; it is idle unless an evaluation is running) records "evaluation started at t". Past
     **250 ms (tunable)** it calls `CoCancelCall(staThreadId, 0)`; the blocked call returns
     `RPC_E_CALL_CANCELED` (`Transient`). S5/P2 measured this against a real out-of-process server (§ H); it has **not**
     been shown against Explorer itself, which we will not hang.
   - *Abandon.* If the evaluation is still blocked **1 s (tunable)** after the cancel (cancellation only works for
     calls through a COM proxy, and not inside non-COM waits), the thread is declared **wedged**: the watchdog
     bumps the `generation`, publishes `Inactive` and abandons the thread (background thread, never joined).
     **No replacement starts at once** (changed 2026-11-03, § M): the tracker enters `Quarantined` and a new
     worker (with an empty cache) starts only on the evidence rules of § M. Anything the old thread later produces carries the old
     generation and is dropped. Abandoned COM objects are **not** released from another thread (it could
     deadlock); they are leaked once, deliberately.
   - *What abandonment guarantees, and what it does not (user decision 1, 2026-11-03, T3.2.5 F2).* Abandonment
     bounds **our own waiting**: no Folder Notes thread that matters (UI, publisher, `Stop`) waits more than the
     cancel + abandon deadlines (1.25 s after the evaluation began; `Stop` ≤ 2 s). It does **not** unblock the
     stuck call, repair the remote server or tell us when that server is healthy again. The abandoned call stays
     pending inside our process until the server answers or dies. Two properties of cancellation are kept apart:
     *cancel latency* (how long `CoCancelCall` itself takes; ≤ 1.1 ms in P6, 0.01 ms against a frozen real
     Explorer) and *cancel effectiveness* (whether the blocked call then returns; yes against the frozen scratch
     server in P6, **no** against a frozen real Explorer in T3.2.5 F2). Cancellation is **best-effort** and nothing
     relies on it working. What happens *after* an abandonment (no immediate replacement against the same server,
     global quarantine, release on evidence) is § M (approved 2026-11-03).
   - *Circuit breaker.* 3 wedges within 10 min → `Disabled` (reason `CircuitBreaker`): publish `Inactive`, log once,
     and Settings shows "Explorer tracking paused after repeated problems" with a **Retry** (T3.4). It never retries by
     itself in a loop. An explicit Retry clears the breaker's wedge window and allows one more recovery cycle.
   - *Hard bounds (user decisions D4, Q1, 2026-11-03).* (1) **At most one tracker instance per process** (a second
     `Start` on any instance while one is active is refused; enforced by a process-wide guard and tested). (2) **At
     most one live COM worker (STA) thread serves the tracker, plus at most one abandoned worker per wedge, and at
     most 6 abandoned workers ever per process lifetime** (Q1: the breaker still trips at 3 wedges in 10 min, so the
     cap allows two breaker episodes: three abandoned workers trip the breaker, one Retry permits a second cycle, and
     after the 6th abandoned worker the tracker is `Disabled` with reason `ThreadCap` for the rest of the process).
     **Retry never resets the lifetime counter**, and the counter is process-wide (the shared `TrackerProcessGuard`),
     not per tracker instance, so creating a new tracker object cannot buy new threads. `Retry` is refused once the
     cap is reached. A replacement worker is never started while the previous one is still being cancelled. When both
     limits are reached by the same wedge the reason is `ThreadCap` (the permanent one wins). (3) **An abandoned worker
     cannot publish:** the single publish gate compares the caller's generation with the current one, and abandoning
     invalidates the generation *before* the replacement starts. An abandoned worker owns no hooks (they live on the
     hook thread, §B.7), so there is nothing of ours left delivering events to it: the router (§B.7) is detached
     from it before the replacement attaches. (4) Abandoned COM objects are leaked at most once per wedge and
     counted in `TrackerStats`. (5) **Until proven by probe P2, no text or code claims COM calls can be cancelled
     safely.** If P2 fails, the 1 s abandonment is the effective limit and this section says so.
   - *Permanent exhaustion is shown plainly (Q1).* The state machine exposes `Disabled` with reason `CircuitBreaker`
     (Retry offered) or `ThreadCap` (permanent). T3.4's Settings card must tell them apart: `CircuitBreaker` says
     "Explorer tracking paused after repeated problems." with **Retry**; `ThreadCap` says "Explorer tracking is turned off
     for this session after repeated problems. Restart Folder Notes to try again." with **no Retry button** (not a
     disabled one: an action that can never work is not offered). Both reasons are logged once, with the counts.
   - *Process thread budget (Q2).* Threads the tracker may own, all named `ExplorerTracker.*`: **live** 1 COM worker,
     1 hook thread (§B.7), 1 watchdog thread (waits on an event, so it is idle unless an evaluation runs); at most 3
     live at any time. **Abandoned** (leaked on purpose, lifetime of the process): ≤ 6 workers, ≤ 1 hook thread. So the
     tracker never owns more than 3 + 7 = **10 threads at any moment of the process's life** (live plus left-behind), and
     ≤ 3 when nothing has wedged. The existing COM worker limits (1 live, ≤ 1 per wedge, breaker 3/10 min) are unchanged by the hook
     thread. The budget is enforced by the same process-wide guard (one counter per kind) and tested.
   - `Stop`/`Dispose` never wait unbounded: post `WM_QUIT`, wait ≤ 2 s, then abandon.
7. **The hook thread (Q2, 2026-11-03).** WinEvent hooks do **not** live on the COM worker. One dedicated thread
   `ExplorerTracker.Hooks` owns them:
   - *It never makes a COM call* (no `CoInitialize`, no interface pointer, no RCW). It runs a plain Win32 message loop
     (`GetMessage`) because out-of-context hook callbacks are delivered through that loop.
   - *It owns the hook lifecycle.* Install, re-scope and uninstall all happen on this thread (`UnhookWinEvent` must run
     on the thread that called `SetWinEventHook`). The worker (and `Stop`) only send it commands through posted thread
     messages: `Install`, `SetScope(pid)`, `Uninstall`, `Quit`. Commands execute in order on the hook thread, so there
     is no concurrent access to hook handles.
   - *Callbacks are trivial:* map the event id to a bit, `Interlocked.Or` it into a pending-signal word, and, only if
     the word was zero, post **one** wake-up message to the current worker. No class-name lookup, no `IsWindow`, no
     path, filesystem, COM, logging, allocation or UI work. Scoping (§D.2) is done by the `idProcess` argument of
     `SetWinEventHook`, not by filtering in the callback.
   - *Scope decisions are the worker's.* On `FOREGROUND` the worker evaluates which frame is foreground and asks the
     hook thread to `SetScope(pid)` for the frame's process. The hook thread does not resolve anything.
   - *The router decouples hooks from workers.* A `SignalRouter` holds the pending-signal word and the **current**
     worker's wake-up target (thread id + generation). `Attach(worker)` / `Detach(worker)` are called by the
     supervisor. Replacing an abandoned worker is `Detach(old)` then `Attach(new)` and **never touches a hook**. While
     no worker is attached, signals accumulate in the word and the new worker's first evaluation consumes them.
   - *Wake-ups are bounded.* At most one wake-up message is outstanding per worker (the worker clears the pending
     word **before** it starts an evaluation, so an event arriving during the evaluation posts again). A wedged worker
     therefore holds at most one unread message, and once detached receives none.
   - *Start/stop order.* Start: reserve budget → hook thread up and `Install` acknowledged (or failed) → watchdog →
     worker. Stop: `Detach` worker → `Uninstall` on the hook thread (≤ 1 s) → `Quit` → worker quit (≤ 2 s) → watchdog.
     Each step is bounded; a step that overruns is abandoned, never waited for.
   - *Failure of the hook layer is degraded, not fatal.* If `SetWinEventHook` fails, the hook thread dies, or it does
     not acknowledge in time, the tracker keeps running on the poll and the 30 s reconciliation (D2) with at most 2 s of
     staleness, logs once, and reports `HooksActive = false` in `TrackerStats`. It does not count toward the circuit
     breaker (no COM wedge happened).
   - *Hook thread restarts are bounded.* A hook thread that exited is not restarted by itself; the next `Start()` or
     `Retry()` may create one new hook thread. A hook thread that had to be abandoned is **never replaced in this
     process** (poll-only from then on). So hook threads are created at most once per `Start`, abandoned at most once.

   *Thread-bound guarantees after this change (clarified, nothing else changes):* (a) every COM object is created, used
   and released on the one live worker, as before; (b) hooks are installed, re-scoped and removed only on the hook
   thread; (c) the watchdog calls `CoCancelCall` and signals "abandon", and touches neither hooks nor COM objects;
   (d) the earlier wording "the abandoned thread's hooks are removed by the watchdog" is **withdrawn**: the abandoned
   worker has no hooks; (e) WinEvent callbacks no longer run inside the worker's COM waits (S5/P2's 7 callbacks in 257 ms
   were delivered to the installing thread, which is now the hook thread). The worker can still receive its own posted
   wake-up only between waits, and keeps the "evaluation running → set dirty flag" guard (§B.4) as a defence.

#### C. Browser-to-tab mapping (the cache)

- **Cached:** `tabHwnd → TabEntry { frameHwnd, ComRef, lastPath, failureCount, generation }` and
  `frameHwnd → [tabHwnd]`. A tab's `ShellTabWindowClass` HWND is stable for the tab's lifetime and unique among
  live windows, so it is the key. Without tabs (Windows 10) each frame has one implicit tab, keyed by the frame HWND.
- **Steady state does not enumerate.** Evaluation = `GetForegroundWindow` → frame check → find the visible
  `ShellTabWindowClass` child (a few `FindWindowEx` calls) → map lookup → **one** path read on that one entry.
  `IShellWindows` is not enumerated on a hit. Target **< 10 ms** per evaluation (S1: 34 ms with enumeration and
  late binding).
- **Active tab = the visible `ShellTabWindowClass`** matched to the entry whose `IShellBrowser.GetWindow()` is that
  HWND (S1). "First child in z-order" is a tie-breaker only. No tab class in the frame → the frame's single
  entry. Anything else → `unresolved`: path `null`, counted, retried on the ladder.
- **Filling the map (`Rebuild`)** enumerates `IShellWindows` once, releases entries that are gone and adds new
  ones. It runs: at start; on a **miss** (foreground frame or visible tab not in the map: a new window or tab);
  after `Gone` from the root; after a shell restart; and as a **reconciliation** at most every 30 s **(tunable)**
  while an Explorer frame is foreground (never in the background). Rebuilds are coalesced like evaluations and
  never nest.
- **Creation, closure, navigation:**
  - *New window or tab:* `EVENT_OBJECT_CREATE` for `CabinetWClass` / `ShellTabWindowClass` marks the map dirty.
    `IShellWindows` registers a window slightly *after* its HWND exists, so the first rebuild may miss it; misses
    use the retry ladder (§D.4).
  - *Closure:* `EVENT_OBJECT_DESTROY` for a cached tab or frame removes its entries immediately (released on the STA
    thread). A missed `DESTROY` is still caught: `IsWindow(tabHwnd)` is checked on every hit, a `Gone` from the
    path read drops the entry, and the reconciliation sweeps the rest.
  - *Navigation* never changes the map (the tab and its browser object persist); only the path read changes.
  - *HWND reuse* cannot attach a stale object: a hit reads through the cached object, which fails with `Gone`
    if it is dead; the entry is dropped and rebuilt.
- **Bound:** at most 256 entries; beyond that, the oldest non-foreground entries are released first.

#### D. Event sources and coalescing

1. **Sources (events only; no `DWebBrowserEvents2` or `DShellWindowsEvents` sinks; decision D2):**
   `EVENT_SYSTEM_FOREGROUND` (always installed; rare), and while an Explorer frame is foreground:
   `EVENT_OBJECT_NAMECHANGE`, `EVENT_OBJECT_SHOW` / `HIDE` (tab switch), `EVENT_SYSTEM_MINIMIZESTART` / `END`,
   `EVENT_OBJECT_CREATE` / `DESTROY` (map upkeep). All out-of-context with `WINEVENT_SKIPOWNPROCESS`.
2. **Idle cost control.** `NAMECHANGE` is global and chatty (browsers, terminals and editors rename windows all
   the time). So those hooks are **scoped with `idProcess` = the foreground Explorer frame's process** and
   re-scoped on every foreground change, and they are removed while no Explorer frame is foreground (the worker asks
   the hook thread to do both, §B.7). The callback runs on the hook thread and does only: check `idObject ==
   OBJID_WINDOW` (a parameter compare), set the event's bit, post at most one message. **Changed 2026-11-03 (Q2):** the
   class-name allow-list check moved out of the callback, because reading a window's class is a call into another
   process's window; the worker applies it when it consumes the bits (cheap: the process is already scoped).
3. **Coalescing = throttle with a trailing edge, not a debounce** (decision D3). A debounce delays *every* change by
   its quiet period and can starve under a steady stream; S1's worst navigate was 107 ms, so a 50 ms debounce
   could push REQ-031's 150 ms to about 167 ms. The scheduler:
   - the first signal evaluates **as soon as the loop reaches it** (leading edge, ~0 ms added);
   - signals during the next **30 ms (tunable)** only mark `dirty`; when the window ends, if `dirty`, exactly one
     **trailing** evaluation runs. So the final state is always processed, and a change waits at most one window;
   - a continuous stream costs at most one evaluation per 30 ms (≈33/s), each under 10 ms;
   - **Confirm pass:** after an evaluation that *changed* the state, one more runs at **+75 ms (tunable)** to
     catch Explorer's transient states (the title changes before the view commits). If it differs, a second event
     is published. Nothing waits for it before the first event.
4. **Retry ladder** for `unresolved`, a miss or `Transient`: re-evaluate at +50, +150, +400, +1000 ms, then stop
   until the next signal. Never an unbounded loop.
5. **Poll safety net.** Only while an Explorer frame is foreground and not minimized: every **500 ms** after a
   signal, backing off to **2 s** after 10 s without any signal **(tunable)**. No timer otherwise. (ADR-004 said
   250 ms and the old text of this file 500 ms; reconciled to 500 ms. S1 showed events alone are enough.)
6. **No-op suppression.** A state is published only if `(frameHwnd, normalizedPath)` differs from the last
   published one (path ordinal, ignoring case; `null == null`). `tabHwnd` is **not** compared: switching between
   two tabs on the same folder changes nothing the panel shows. An evaluation that changes nothing publishes
   nothing and allocates nothing.
7. **Foreground target.** An Explorer frame is a `CabinetWClass` window. The foreground window counts as that frame
   if it is the frame **or is owned by it** (`GW_OWNER` chain); that is how the docked panel (owned by the frame,
   S2) keeps its folder while it has focus (§ Docking). Any other foreground window, or none → `Inactive`.
7a. **A missed event never leaves the panel stale for good (user decision D2).** Events are optimisations; these
   bound staleness without them: while an Explorer frame is foreground the poll runs (≤ 2 s between reads, 500 ms
   after activity), so the displayed folder is at most **2 s** out of date even if every WinEvent is lost; the
   30 s reconciliation repairs the map; on losing/gaining foreground a read always happens (the foreground event is
   itself covered by the poll and by the next signal). A test drops all events (hooks disabled) and asserts the
   state converges within 2.5 s.
7b. **Ordering and flicker (user decision D3).** Every evaluation takes its `Sequence` number *when it starts*; a
   result is published only if its sequence is newer than the last published one, so an older result can never
   overwrite a newer state (also across the confirm pass and retries). Delivery to the UI keeps order (single
   writer, latest-wins). The confirm pass publishes only if the path/frame actually differs, so a stable navigation
   produces one update, and a corrected transient produces two. REQ-031 latency has priority over one-update-per-
   navigation.
8. **Publishing.** `ActiveFolderChanged` carries `ActiveFolderState { FrameHwnd, TabHwnd, Path?, Sequence }` (no COM
   objects, no bounds; bounds are `PanelDocker`'s, T3.3). It is raised on the **UI thread** through `IUiDispatcher`,
   **latest-wins**: if several states wait for delivery only the newest is delivered; consumers drop anything
   with an older `Sequence`. `Current` returns the last published state without locking. `Inactive` = `FrameHwnd 0`.
   `Path = null` for virtual folders and anything the shell reports without a file-system path.
   **Latest-wins is intentional (reviewed in T3.2.5).** The panel shows a level (which folder is active now), not a
   history. Merging is therefore allowed to drop intermediate states, including an `Inactive` that was immediately
   followed by another folder: the consumer sees one event with a newer `Sequence` and perhaps the same path as before.
   Consumer contract: (1) compare `Sequence` and drop older events; (2) never count events or rely on seeing every
   `Inactive` (a hide/show pair for a quick window switch may arrive as a single "new frame/path" state, which avoids
   flicker); (3) treat equal path with a different `FrameHwnd` or `Sequence` as "refresh", not as "nothing happened".
   Every state is still *published* in order by the `StatePublisher`; only delivery to the UI thread merges.

#### E. Reading the path

**One route** (S5/P3): `QueryService(SID_STopLevelBrowser)` → `IShellBrowser.QueryActiveShellView` → cast `IFolderView`
→ `GetFolder(IID_IPersistFolder2)` → `GetCurFolder` → `SHGetNameFromIDList(SIGDN_FILESYSPATH)`; the conversion
failing with `E_INVALIDARG` = virtual folder = `null`. The `LocationURL` fallback was **dropped**: it mangles UNC
paths (`\\localhost\C$\WINDOWS` became `\C$\WINDOWS`) and covered nothing route A doesn't. The S1 late-bound route is
not used (ADR-007). P3 compared the routes on: normal, Unicode, known folder, drive root, UNC, This PC, Home, Quick
Access, Recycle Bin, Libraries, a `.zip`, and a search result (not obtained, see § H). The tracker reports the string the
shell gives; checking that it is a real directory is the consumer's job (`IFolderIdentityService.TryResolve`).

#### F. Reliability and recovery

- **Explorer restart or crash.** Detected by any of: `Gone` from the root; the tray window forwarding
  `TaskbarCreated` (`ExplorerTracker.NotifyShellRestarted()`; the tracker's thread has no top-level window so it
  cannot hear the broadcast, but the tray window of ADR-012 already does); the process id of `GetShellWindow()`
  changing; `GetShellWindow() == 0`. Then, in order: (1) publish `Inactive` so the panel hides; (2) release every
  `ComRef` (dead proxies release fast; failures swallowed); (3) state `Recovering`; (4) try to recreate
  `IShellWindows` **only when `GetShellWindow() != 0`**, at +250 ms, +500 ms, 1 s, 2 s, then every 5 s, with no
  limit while the app runs; (5) on success rebuild the map, evaluate, publish. The WinEvent hooks belong to the
  system, not to Explorer, and to the hook thread, not the worker (§B.7), so they survive both an Explorer restart
  and a worker replacement; they are re-scoped (§D.2) on the hook thread and reinstalled there if installing ever
  failed. The user never has to restart Folder Notes.
- **Do not create `ShellWindows` while the shell is down.** On this machine `CLSID_ShellWindows` is registered as
  `LocalServer32 = rundll32.exe shell32.dll,SHCreateLocalServerRunDll {9BA05972-…}`: if Explorer's class
  registration is absent, COM starts its own **empty, private** `ShellWindows` server in a `rundll32.exe` and
  hands us that, and we would track a list that never contains the real windows. So: gate on `GetShellWindow()`,
  and **validate after creating**: if `CabinetWClass` frames exist but two consecutive rebuilds enumerate none,
  treat the root as wrong, release it and recreate on the ladder. Probe P4 confirms the behaviour on a
  disposable session (it needs Explorer to be killed, so it never runs on the dev desktop by default; see D5).
- **Several windows and tabs.** The map holds all of them; only the foreground frame's visible tab is read.
  Windows in separate processes (*Launch folder windows in a separate process*) share the same `IShellWindows`
  list; the hook's process scope is re-derived per foreground frame, so it follows.
- **Rapid switching, navigation and window churn.** Bounded by the throttle (≤ 33 evaluations/s, each < 10 ms), the
  dirty flag, latest-wins publishing and the §C bound. Open/close loops release entries on `DESTROY`; nothing grows
  with the number of windows ever seen.
- **Idle CPU.** No timer unless an Explorer frame is foreground; hooks scoped by process; no allocation on a no-op
  evaluation; the watchdog waits on an event, not a sleep loop. Budget **< 0.1 %** of one core over 60 s with
  Explorer foreground and untouched, **0 %** with another app foreground (06).
- **Stop.** `Stop()` removes hooks (on the hook thread) and timers, releases all `ComRef`s, ends the worker, hook and
  watchdog threads (bounded waits, §B.6/B.7, in the order given in B.7) and publishes `Inactive`. `Start()` after `Stop()` starts from an empty cache.
- **The tracker only reads.** It never posts to, subclasses, injects into or modifies an Explorer window. A tracker
  fault ends in `Inactive` and one log line.

#### G. Limits imposed by Windows and the COM interfaces

Not every Explorer event can be observed reliably. Each limit and what the design does about it:

| # | Limit | Consequence in the design |
|---|---|---|
| L1 | WinEvents are asynchronous, unordered relative to COM state, can be coalesced or dropped under load, and `NAMECHANGE` does not guarantee the *path* changed. | Every event only means "re-read"; truth is read from the shell; the poll and the 30 s reconciliation stay. |
| L2 | There is **no documented event for "active tab changed"** or "navigation committed". S1 saw `NAMECHANGE` / `SHOW` / `HIDE` fire for them on build 22631; that is observed behaviour, not a contract, and may differ on 24H2 and later builds, and on Windows 10. | Poll fallback stays; the T3.5 matrix runs on 3 builds; without the tab class the logic degrades to one implicit tab. |
| L3 | Out-of-context hooks need a message loop. **Measured (S5/P2):** while a hook-owning STA thread waits inside an outgoing COM call, COM's wait loop still dispatches WinEvent callbacks to it (7 during one 257 ms call), but not posted thread messages. | Hooks live on a separate COM-free hook thread (§B.7, Q2), so no callback runs inside a COM wait; callbacks only set a bit and post one message (§B.4); time-boxed calls (§B.6). |
| L4 | COM has no per-call timeout. `CoCancelCall` works only for calls through a COM proxy and only while the thread is in a COM wait. Its own latency is undocumented; measured ≤ 1.1 ms in every case tried (§ H, P6). **Its effectiveness is not guaranteed either:** against a frozen real Explorer it returned at once and the call stayed blocked (T3.2.5 F2). | Cancel (best-effort), then abandon the thread; circuit breaker; cancel latency is measured and logged; contingency design in § H P6. Abandonment bounds our waiting, not the server's recovery (§ B.6); what follows an abandonment is § M (global quarantine, release on evidence). |
| L5 | `IShellWindows` registers a window after its HWND exists and removes it around the HWND's destruction. | Retry ladder; `IsWindow` and `Gone` checks; reconciliation. |
| L6 | `TaskbarCreated` is broadcast only to top-level windows, only when a taskbar is created, and not when the shell restarts without one. | Forwarded from the tray window **and** detected independently (`Gone`, shell process change). |
| L7 | `CLSID_ShellWindows` can be served by a private, empty `rundll32.exe` server (§F). | Gate on the shell window; validate the enumeration. |
| L8 | Explorer windows at a higher integrity level (run as administrator) may not be readable from a non-elevated process. | Unsupported in v1 (user decision): the tracker reports no usable path (`null`) without crashing and **without retrying continuously**: an access-denied / `unresolved` result for an elevated frame is remembered per frame HWND and not retried until that frame navigates or loses and regains foreground. Verify by hand (09 O-23). |
| L9 | Virtual folders, libraries, Quick Access, search results, the Recycle Bin and MTP devices have no file-system path; a `.zip` browsed in Explorer reports a path that is a *file*. | `null` for the former; the consumer validates directories. |
| L10 | The desktop (`Progman` / `WorkerW`) and common file dialogs are not `CabinetWClass` frames. | Out of scope: `Inactive`. |
| L11 | During a pending navigation the shell still reports the old folder; HWNDs are recycled; the lock screen has no foreground window. | Publish the committed folder; the confirm pass; no foreground → `Inactive`. |
| L12 | Windows 10 22H2 has no tabs and could not be tested here (09 O-6). | Windows 11 evidence is not Windows 10 evidence; O-6 stays open until checked on Windows 10. |

Emitted event: `ActiveFolderChanged { FrameHwnd, TabHwnd, Path?, Sequence }` (§D.8).

#### H. S5 probe findings (T3.2.1, 2026-11-01, Windows 11 23H2 build 22631, .NET 10, `spikes/S5-tracker-probes`)

**P1: hand-written `[GeneratedComInterface]` is reliable here (decision D1 holds; no CsWin32 switch).**
- Vtable order and arity of all 8 declared interfaces match the Windows SDK 26100 headers (`ExDisp.h`, `ShObjIdl_core.h`,
  `servprov.h`, `oleidl.h`), checked mechanically: the probe parses each `*Vtbl` struct. The check itself needed one
  fix (SAL annotations such as `__RPC__in_range(0,16384)` contain commas), which is why the production guard test must
  strip annotations before counting.
- Live calls pass for every method used: `get_Count`, `Item(VARIANT)`, `get_HWND` (frame), `QueryService` + `GetWindow`
  (tab = `ShellTabWindowClass`), `QueryActiveShellView`, the `IFolderView` cast, `GetFolder`, `GetCurFolder`. The
  `Variant` struct passed by value (24 bytes on x64) works.
- **HRESULT behaviour (`[PreserveSig]`):** `Item` out of range returns **`S_FALSE` (1)** with a null out: any non-zero
  result means "no item". `Item(VT_EMPTY)` returns `E_INVALIDARG`. Nothing throws.
- **Casting:** `is`/`as`/explicit casts QI correctly; a cast to an interface the object lacks fails (`InvalidCastException`
  for the explicit cast).
- **Release (a correction to the earlier spec):** `ComObject.FinalRelease()` returns the raw proxy reference count to
  its baseline (5 → 7 with the RCW and its QI'd interfaces → 5), after which any use throws `ObjectDisposedException`.
  `Marshal.ReleaseComObject` and `FinalReleaseComObject` throw `ArgumentException` on these RCWs. One `ComObject` serves
  all interfaces QI'd from one COM object, so one `FinalRelease()` per object is enough.
- **A closed window's objects do not report a disconnect code.** After the frame closed: `get_HWND` and
  `QueryActiveShellView` returned **`E_FAIL` (0x80004005)**, `GetWindow` still returned 0 (cached HWND). So `Gone` cannot
  rely on `RPC_E_DISCONNECTED`: **`IsWindow(tabHwnd) == false` or `E_FAIL` on a tab whose window is gone ⇒ `Gone`**
  (B.5/C amended below). The `RPC_E_*` codes stay in the table for a dead Explorer *process*, which the probe did not
  exercise (D5).
- Cost/leaks: 300 cycles of enumerate + read + release over 2 windows took 134 ms in total (≈0.45 ms a cycle); handles
  306 → 310; managed heap flat.
- Interop notes for the production code: `BSTR` out parameters need `[MarshalUsing(typeof(BStrStringMarshaller))]`;
  `Span<char>`/`char[]` in `LibraryImport` need `DisableRuntimeMarshalling` (the app does not set it), so use `char*`
  buffers.

**P2: `CoCancelCall` works against an out-of-process COM proxy (scratch `LocalServer32` server blocking in
`IPersist.GetClassID`; the registry key and processes are removed afterwards).**
- With `CoEnableCallCancellation` on the calling STA thread, a watchdog's `CoCancelCall(tid, 0)` at ~250 ms made the
  blocked call return **`RPC_E_CALL_CANCELED` (0x80010002) 0.2-2.4 ms later**; five more cancel cycles on the same
  proxy returned in 0.2-0.4 ms and the proxy stayed usable. The same holds inside a message loop with a WinEvent hook.
- **Without** `CoEnableCallCancellation`, `CoCancelCall` fails (`RPC_E_CANCEL_DISABLED` 0x80010140) and the call runs to
  completion (6007 ms): enabling is mandatory, once per tracker thread.
- **Limits of this evidence:** it proves cancel for a call through a COM proxy to another process, which is how the
  tracker reaches Explorer. It does not prove Explorer itself behaves the same when genuinely hung; the abandon step
  (1 s) stays as the backstop, and no text claims more (D4).
- **WinEvent callbacks run on the thread while it waits in the outgoing call** (7 callbacks during a 257 ms call).
  So callbacks are re-entrant with respect to the call, not merely delayed (corrects L3): callbacks must stay trivial
  (flag + post). A thread message posted with `PostThreadMessage` was **not** dispatched during the wait, so the
  evaluation handler itself is not re-entered, but its guard stays (cheap).
- Side finding: an `IOleWindow` call into the scratch server returned `RPC_E_CANTCALLOUT_ININPUTSYNCCALL` at once and never
  reached the server (`IOleWindow` methods are input-synchronous); not investigated further because Explorer's
  `IOleWindow.GetWindow` works (P1). Plain ole32-proxied interfaces were used for the cancel test.

**P3: path reads (route A = `GetCurFolder`; B = `LocationURL`; C = the S1 late-bound route, as the reference).**
- A agrees with C on every case obtained: plain, Unicode (`Ünï 项目 📁`), known folder (`Downloads`), drive root, UNC admin
  share (`\\localhost\C$\WINDOWS`), and the `.zip`.
- Virtual folders: This PC, Home, Quick Access, Recycle Bin, Libraries all gave A = `null` (`SHGetNameFromIDList` →
  `E_INVALIDARG`), where C gave the `::{GUID}` strings. Matches REQ-031's virtual-folder rule.
- A **`.zip` folder reports the path of the `.zip` file** (L9, confirmed): the consumer must check it is a directory.
- B failed on UNC (`\C$\WINDOWS`) and returns the same `null` for virtual folders, so it was dropped (§ E).
- **Not obtained:** a *search-results* window (the `search-ms:` launch produced no tracked window within 6 s), a mapped
  drive and an MTP device (none available). They remain listed in the T3.5 manual matrix; route A returns `null`
  for anything `SHGetNameFromIDList` cannot turn into a file-system path.
- **Cost:** route A median **0.36 ms** (max 1.6 ms) per read on a cached tab; one full typed enumeration of every tab
  (`Item` + `get_HWND` + `QueryService` + `GetWindow`) **0.37 ms** for 2-3 windows. S1's late-bound figure was 34 ms. The
  < 10 ms evaluation target has an order of magnitude of margin (to be re-measured end to end in T3.2.5).

**P5: hook scoping.** Another process fired `NAMECHANGE`:
| Source rate | No hook | Global hook | Hook scoped to Explorer's pid |
|---|---|---|---|
| 20/s | 0 callbacks | 172 callbacks / 10 s (≈17/s) | **0** |
| 200/s | 0 | 633 / 10 s (≈63/s) | **0** |

The global hook delivers other applications' events; the scoped one delivers none (decision D2's scoping holds).
The delivered rates are well below the generated rates, direct evidence for L1 (events are dropped/coalesced under
load). **The CPU cost could not be resolved**: the process's CPU clock ticks at 15.6 ms, so 0.156 % (one tick) to 0.000 %
is noise at this rate. What is proven is that scoping removes the callbacks entirely; the idle-CPU budget is
verified end to end in T3.2.5 and T3.5.

**P6: can `CoCancelCall` itself block the watchdog thread? (T3.2.4 pre-work, 2026-11-03).** *Documentation* (Microsoft Learn, `CoCancelCall`,
`ICancelMethodCalls::Cancel`): `CoCancelCall` finds the thread's cancel object and calls `Cancel(ulTimeout)`; `ulTimeout` is "the number of seconds
[it] waits for the server to complete the outbound call after the client requests cancellation"; "a return value of S_OK does not mean that the call
was canceled, only that an attempt was made". **Nothing documented bounds the latency of the request itself, and nothing says it cannot block.**
*Measured* (scratch server, Release, this machine; the "frozen" cases suspend the scratch server process with `NtSuspendProcess`, never Explorer):
| Case | `CoCancelCall(tid, 0)` returned | Result |
|---|---|---|
| target idle / nonexistent thread id / thread already exited / target in a non-COM wait | 0.00-0.05 ms | `0x8001011E` (cancel not possible), no side effect |
| target in a call to a hung server | 1.09 ms | `S_OK`; the call returned `RPC_E_CALL_CANCELED` 1.6 ms later |
| target in a call to a **frozen server process** (no thread can answer) | 0.05 ms | `S_OK`; call returned 0.4 ms later |
| same, `ulTimeout` = 3 s | 0 ms | `S_OK` at once; the **cancelled call** returned 3002 ms later: the timeout delays the client call, it does not block the canceller |
| 150 cancels at random moments in a call | mean 0.03, max 0.17 ms | none over 50 ms |
| 3 threads cancelling the same target at once | 0.02-0.04 ms each | no deadlock |
| caller COM-initialised (MTA) vs not | 0.00 vs 0.05 ms | same |
*Conclusion:* in every adverse case tried the request is client-side and returns in about a millisecond; the watchdog uses `ulTimeout = 0`.
*Not established:* (1) it was never run against Explorer's own proxy while Explorer is genuinely hung (we will not hang it, D4/D5); (2) the
cancel path inside `combase` is undocumented, so a future Windows build could behave differently; (3) a thread-suspended or debugger-stopped
*caller* is not a case (it is our own thread). **Residual risk:** if `CoCancelCall` ever did block, the watchdog thread would stop and a wedged
worker would not be replaced (the UI stays responsive; the panel would show no folder). **Mitigation now:** the watchdog times every cancel
(`MaxCancelCallMs`, in `TrackerStats`) and logs once if one exceeds 50 ms, so T3.2.5 and later field logs show it. **Contingency design (not built;
needs the user's approval because it changes the thread budget):** arm a threadpool timer for the abandon deadline *before* issuing the cancel, so
abandonment never depends on the cancel returning; if the watchdog is found stuck, abandon it too and start one new watchdog (budget: ≤ 1 abandoned
watchdog thread, owned-at-once 10 → 11). Trigger: any measured cancel > 50 ms on a real Explorer in T3.2.5.
*Real Explorer (T3.2.5 F2, 2026-11-03):* against a lab-owned `/separate` explorer.exe frozen with `NtSuspendProcess`, the cancel returned in
**0.01 ms** (trigger not hit, contingency stays unbuilt) and **the blocked call did not return** until the process was resumed (~12 s). So
latency is fine and effectiveness is not. Two facts were not recorded and must be before any conclusion about *why*: `CoCancelCall`'s
HRESULT (the watchdog discards it; `0x8001011E` would mean the worker was not in a cancellable COM wait at all) and which call was blocked
(read on a cached tab, or the enumeration of a replacement). Both are added by § M.2 and probe P7 (VM). The contingency would not help:
the cancel is not slow, it is ineffective.

**P4** (shell down, `rundll32` private server) is not run: it needs Explorer killed (D5). It stays open as O-24.

#### J. Failure modes introduced by the separate hook thread (Q2, 2026-11-03)

*Measured while building it (T3.2.3, real Win32, no Explorer):* a global `EVENT_SYSTEM_FOREGROUND` hook and a hook scoped by `idProcess` both
deliver to the hook thread's message loop. **Scoping is by the process that owns the *window* of the event**, not by the process that
called `NotifyWinEvent`: an event raised for the desktop window did not reach a hook scoped to the raising process; one raised for a
window of that process did. This matches how Explorer's own windows behave and is why the scope is the Explorer frame's process.

Reviewed before implementation. Each has an owner and a test (06 § Hook thread).

| # | Failure mode | Effect | Handling |
|---|---|---|---|
| H1 | `SetWinEventHook` returns 0, or the hook thread throws or exits | No events | Degraded, not fatal: poll + reconciliation keep the panel ≤ 2 s behind; `HooksActive = false`; logged once; no breaker count. One re-create per `Start`/`Retry` |
| H2 | The hook thread does not respond to `Uninstall`/`Quit` (stuck in `UnhookWinEvent` or the loop) | `Stop` would wait | Bounded waits (1 s, then abandon); an abandoned hook thread is never replaced in this process; ≤ 1 abandoned hook thread ever; the system removes its hooks if the thread ends |
| H3 | Event storm (a scoped process spams `NAMECHANGE`) | Hook thread CPU, queue pressure | Callback = one `Interlocked.Or` and at most one post (word was zero); no allocation; messages cannot pile up |
| H4 | Lost wake-up: bit set just after the worker cleared the word | A change missed | Worker clears the word **before** it evaluates; any bit set later re-posts. Test with a concurrency hammer. The poll is the backstop anyway |
| H5 | Router points at an abandoned or dead worker | Posts go nowhere or fill a dead queue | `Detach(old)` precedes `Attach(new)`; a post to a detached or dead target is dropped; generation compared on attach/detach |
| H6 | Start/stop ordering: a worker is attached before hooks are acknowledged, or hooks outlive the worker | Early events lost; callbacks into nothing | Fixed order in B.7; signals accumulate while no worker is attached |
| H7 | `UnhookWinEvent` called from the wrong thread | Hook leaks, call fails | All hook calls only on the hook thread (commands by posted message); test asserts thread identity |
| H8 | Re-scope race: a stale `SetScope(pid)` arrives after a newer one | Hooks scoped to the wrong process | Commands are processed in order; `SetScope` carries a sequence, older ones are ignored; the worker re-requests on every foreground change |
| H9 | A callback runs while the hook thread is applying a command | Torn hook state | Same thread: callbacks and commands never overlap |
| H10 | Process-wide thread growth (restart loops) | Unbounded threads | Thread budget in B.6: ≤ 3 live, ≤ 7 abandoned over the process lifetime; `Start` is refused when a kind is exhausted |
| H11 | Hook thread accidentally touches COM or UI (a later change) | Re-introduces the COM-wait re-entrancy or a hang | Source-guard test: `HookThread`/`SignalRouter` reference no `ComRef`, `ComObject`, `Marshal`, gateway types or `IUiDispatcher` |

#### L. Composition (T3.2.4, 2026-11-03)

Written before the code. COM ownership stays on the worker; nothing here is started by `AppShell` (a guard test) and DI only *defines* the registration.
- **Parts.** `IWin32Shell` (non-COM window queries: foreground, frame class, owner, minimized, shell window and its process, `IsWindow`, tab children in
  z-order, "any Explorer frame exists"; real `Win32Shell`, fake in tests). `TrackerEngine` (runs only on the worker thread: gateway, `TabMap`,
  `EvaluationScheduler`, `RootValidator`, recovery; **synchronous and driven by a `TimeProvider`, so it is tested with fakes and a fake clock, no threads**).
  `ExplorerTracker` (the public face and the owner of the machine, publisher, router, hook thread, watchdog, supervisor and counters).
- **Signals.** Router bits map to scheduler kinds: `Foreground`→Foreground; `NameChange`/`ShowHide`/`Minimize`→Navigation; `CreateDestroy`→Structure. The
  callback no longer filters by window class (§ B.7), and the engine does not either: an event from the scoped process that changes nothing costs one
  evaluation that publishes nothing (throttled to ≤ 33/s, < 10 ms). `Structure` first sweeps entries whose window is gone (`IsWindow`).
- **Evaluation** (the `TrackerRig` of T3.2.2, made real): (1) shell check: `GetShellWindow()` is 0 or its process changed ⇒ recovery. (2) foreground
  → frame by `ForegroundResolver` (a window owned by a frame keeps it, minimized = none); none ⇒ scope 0 and publish Inactive. (3) re-scope the hooks to
  the frame's process if it differs from the last request. (4) a frame marked unresolved is not re-read by Poll/Reconcile/Retry/Confirm (L8); it is
  retried by a Foreground or Structure signal or a navigation-signal read. (5) active tab = `ActiveTabResolver`; none visible ⇒ `Unresolved`. (6) miss or
  `Rebuild` ⇒ enumerate once, classify the root's HRESULT (`Gone` ⇒ recovery), validate with `RootValidator`, `Reconcile`. (7) one `TryReadPath` on the
  cached entry; failures are classified (`windowAlive` from `IsWindow`), recorded in the map, and publish nothing new; success publishes through the
  `StatePublisher` with the worker's generation and the evaluation's sequence. The `LadderExhausted` advice marks the frame unresolved.
- **L7 against L8 (found by the engine tests).** "Frames exist but the root enumerates none" also describes a desktop whose only Explorer windows
  are elevated and invisible to our root. Replacing the root forever would be exactly the retry loop L8 forbids. So a suspect root is replaced **at most
  twice in a row**; after that an empty enumeration is accepted as real (the frames become `unresolved`, L8). Any non-empty enumeration resets the count.
- **Scope requests are idempotent and generation-gated** (found by the tracker tests): a replacement worker's fresh engine asks for the scope the hook thread
  already has, so the hook thread ignores a request for the pid it is already listening to (no uninstall/reinstall of four hooks); and each engine's channel
  (`GenerationScope`) forwards only while its generation is current, so a zombie can never re-scope the hooks.
- **Hook-scope policy.** Desired scope = process of the foreground Explorer frame, 0 when none. The engine asks `IHookScope.SetScope` only when it
  changes. (`HookThread` implements `IHookScope`; the engine never touches a hook handle.)
- **Recovery.** `ShellGone` (root `Gone`, `NotifyShellRestarted`, shell process change, shell window 0, or `RootValidator` says `ReplaceRoot`): machine
  → `Recovering` (publishes Inactive), map cleared and gateway disposed **on the worker**, then attempts at the machine's back-off (250, 500, 1000,
  2000 ms, then every 5 s, unbounded) **only while `GetShellWindow() != 0`**; a created root is validated (`Suspect` retries on the next back-off step;
  `ReplaceRoot` discards it); success ⇒ `RecoverySucceeded`, rebuild, evaluate. No user action. Recovery runs on the worker, so a hung attempt is
  covered by the watchdog like any other call.
- **Timers.** The engine returns "next due"; the worker arms one `System.Threading.Timer` (a pool timer, not one of our threads) whose callback only posts
  a wake-up to the worker; an abandoned worker's wake-ups are refused. Zero delay re-posts instead of looping, so the message loop is never starved
  (at most 8 evaluations per wake-up).
- **Publishing.** The sink keeps a single latest-wins slot and posts at most one task to `IUiDispatcher`; the event is raised on the UI thread with the
  newest state; a queue shutting down drops it silently. `Current` is the publisher's current state.
- **Start/Stop order** (supervisor callbacks): `Start` = machine → hook thread → watchdog → worker. `Stop` = detach worker → hooks off (≤ 1 s) → worker
  (≤ 2 s) → watchdog (≤ 1 s) → Inactive. `Retry` re-runs the start sequence (a hook thread that ended cleanly may be re-created once).
- **Thread safety of the state machine.** Called from the UI, worker and watchdog threads, so its mutating methods are synchronized (§ B.6 unchanged).
- **Stats.** `TrackerStats`: evaluations, rebuilds, recoveries, published states, evaluation p50/p95/max ms (last 256), wedges (abandoned workers),
  replacements, live RCWs, cross-thread releases, hooks active, cancel calls and `MaxCancelCallMs`, state and disabled reason, tab entries.
- **DI.** `AddExplorerTracker(IServiceCollection)` registers `IExplorerTracker` as a singleton that is *not started*; `App.xaml.cs` does not call it until T3.4.

#### M. Frozen-server quarantine (**APPROVED 2026-11-03**, user decision 1 + safeguards; built in T3.2.6)

*Why (T3.2.5 F2).* When a COM server never answers, cancellation may not release the call (§ B.6, L4). Before this section the
supervisor started a replacement at once. The replacement enumerated `IShellWindows` again, touched the same frozen process and wedged
too. After three wedges in about 3 s the tracker was `Disabled` until the user retried. The COM architecture is kept (one STA worker,
cancel at 250 ms, abandon at +1 s, the generation gate, breaker 3 / 10 min, lifetime cap 6, thread budget ≤ 3 live / ≤ 7 abandoned).
Only **what happens after an abandonment** changes. **Supersedes** the "starts a fresh STA thread" step of § B.6.

**M.1 Rules.** (1) Cancellation is best-effort. A successful cancel *request* is never taken as proof that the remote operation ended.
(2) The abandon deadline is the backstop. (3) On abandonment, identify the unresponsive server where possible (M.2). (4) No worker is
started automatically against an unresolved server, except a trial worker under M.5. (5) Quarantine is **global**: while any entry is
unresolved, tracking is suspended for **all** Explorer windows, including those in healthy processes (M.3, accepted limitation).
(6) Resume on evidence only (M.4). (7) Retry, Stop/Start and new tracker instances respect quarantine (M.6). (8) Breaker, cap and thread
budget are unchanged and cannot be bypassed (M.8). No new thread and no extra cancellation thread.

**M.2 Call tracking (the journal).** Each worker host owns one `CallJournal`. The worker opens an **operation** before its first
outgoing COM call and closes it only after the last call of that operation has returned (in a `finally`). An operation spans
consecutive calls to one target, because casts (`QueryInterface`) and releases on a proxy are outgoing calls too. A record holds:
`Generation` (the worker's), `OperationId` (monotonic per journal, never reused), `Site`, `TargetPid` (0 = not known), `FrameHwnd`,
`ItemIndex` and `StartedAt` (timestamp). There are two levels: the engine's operation, and an inner one the gateway opens per
enumerated item. The snapshot reports the innermost active one.

| Site | Opened by | Calls covered | Target |
|---|---|---|---|
| `RootCreate` | engine | `CoCreateInstance(ShellWindows)` + wrap | shell pid (probable; L7 may hand out a private server) |
| `Enumerate` | engine | `get_Count`, then each item | shell pid while no item is open |
| `ItemProbe(i)` | gateway | `Item(i)`, the cast and `get_HWND` | **unknown** (no documented API names a proxy's server before the call) |
| `ItemRead(i)` | gateway | `QueryService`, `GetWindow` and the wraps, after `get_HWND` returned | `GetWindowThreadProcessId(frame)`: known |
| `PathRead` | engine | `TryReadPath` | process of the foreground frame: known |
| `Release` | engine | map sweeps and drops, gateway and map disposal | unknown (may span several processes) |

*Stale or incomplete records are never read as active calls.* Writes use a sequence lock: the version is odd while a record is being
written, and the writer is the worker thread only. A reader retries at most 4 times, and a record whose version is odd or changed
during the read is reported as **indeterminate**. A record is **active** only if all of these hold: the version is even, the active
flag is set, `Generation` is the host's generation, and `StartedAt` is not earlier than the start of the evaluation the watchdog is
timing. Each `Evaluate` bracket begins by checking that no operation was left open; a dangling one is closed and counted as a defect
(`JournalDefects` in stats; a test asserts 0). Indeterminate, inactive or stale records give an **unknown** suspect, never a known one.
*Cancel result.* The watchdog stores the HRESULT of each `CoCancelCall` with the worker it targeted (`LastCancelHr`). It is diagnostic
only and is never used as release evidence (rule 1).

**M.3 Suspects, and why quarantine is global.** At abandonment the supervisor copies the snapshot into a quarantine **entry**:
`{zombie host, snapshot, suspect, cancel HRESULT, abandonedAt}`. The suspect is:
- **known**: an active record with `TargetPid` ≠ 0;
- **candidates**: an active record without a pid; then every process that owns a `CabinetWClass` frame, plus the shell (at most 8,
  otherwise unknown);
- **unknown**: no active record, or an indeterminate or stale one.

A process identity is **pid + creation time**, so a reused pid is never mistaken for the old process. A fresh worker has an empty
cache, so its first act is an enumeration, which calls into every Explorer process. The tracker therefore cannot leave out one process.
This is why quarantine is global and why no worker runs while an entry is unresolved, except a trial (M.5). The user accepted this
limitation (2026-11-03). Positional skipping through undocumented `IShellWindows` ordering is **rejected** and will not be used.

**M.4 Evidence.**
- **E1, the blocked operation completed (delayed worker completion).** The zombie's journal shows that the recorded operation has ended
  (an inactive record, or a higher `OperationId`), or the zombie thread exited. The server answered *that* operation. E1 resolves its
  entry whatever the suspect kind. A late zombie never publishes (generation), never re-scopes hooks, and **never refunds the lifetime
  cap**.
- **E2, the server ended or was replaced.** No process with the suspect's identity runs any more (exited, or the pid now belongs to a
  process with another creation time). The shell counts as replaced when its process ended. E2 resolves a *known* entry, or a
  *candidates* entry once every candidate has ended. It never resolves an *unknown* entry.
- **E3, health hold: evidence for attempting recovery, not proof of health.** For at least **10 s** without a break, no hang evidence
  was seen, and at least **30 s** (minimum quarantine) have passed since the most recent abandonment in the record. *Hang evidence*
  means `IsHungAppWindow` is true for any frame of a suspect, or for any Explorer frame when a suspect is unknown, or for the shell
  window when the shell is a suspect. A hold that sees hang evidence starts again from zero. E3 only allows a **trial worker** (M.5). It
  never resolves an entry, because a pumping UI thread does not prove that the COM call will be answered. A suspect with no window
  shows no hang evidence, so its hold completes on time alone.
- **Not evidence:** time alone; a Retry; a cancel request returning `S_OK`.

*Monitor.* It runs on the **existing watchdog thread**, which then waits on its event with a **1 s** timeout instead of an infinite
wait. Each tick checks E1, E2 and hang evidence, narrows a candidate set to a known suspect when exactly one candidate shows hang
evidence, and decides on a trial or a release. It uses only Win32 calls that send nothing to Explorer: `OpenProcess`
(`PROCESS_QUERY_LIMITED_INFORMATION`) with `GetProcessTimes`/`GetExitCodeProcess` (the handle is closed at once, none is kept),
`FindWindowEx` for `CabinetWClass`, `GetWindowThreadProcessId`, `IsHungAppWindow` and `GetShellWindow`. The watchdog still touches no
hook and no COM object. When no entry is unresolved, the watchdog waits as before (no idle cost). Sampling every 1 s can miss a hang
shorter than a second; that is accepted. *Depends on probe P7 (VM, VM plan stage D):* if `IsHungAppWindow` turns out not to report a
frozen or busy frame, hang evidence is always false. Holds then complete on time alone, and the bounds of M.8 still hold.

**M.5 Trial workers.**
- *Permission.* At most **one trial worker at a time** (it takes the single live-worker slot). A trial starts only if the abandoned
  count is below the lifetime cap; otherwise the state is already `Disabled (ThreadCap)`. Starting a trial **does not clear the
  record**: entries stay until E1/E2 or a passed trial.
- *Automatic trials are budgeted per server.* When E3 completes, an automatic trial starts only if at least one unresolved entry's
  suspect has not yet had an automatic trial in this record. Starting the trial marks **every** current suspect as *tried*. Two suspects
  count as the **same server** if they are the same identity, if either is unknown, or if their candidate sets intersect
  (conservative). So the same unresponsive server gets **at most one automatic trial**. After a failed trial, another automatic trial
  needs **both** a new, different suspect (which only a new wedge produces) **and** another complete health hold. The number of
  automatic workers is therefore bounded by the number of distinct wedges, which the breaker and the cap bound.
- *Deadline: 10 s from the trial worker's start.* A trial **passes** at the deadline if it was not abandoned **and** completed at least
  one full enumeration (`RootCreate` + `Enumerate` returned success with the root validated, § F). Passing clears the record (state →
  `Running`). A trial that reaches the deadline without one is **inconclusive**: it is stopped within the normal bound (≤ 2 s), and
  if it does not end it is abandoned and counted as a wedge. The state returns to `Quarantined (Waiting)`, the hold restarts, and the
  suspects stay tried.
- *Wedge.* A trial worker abandoned by the watchdog, or not started within the start timeout, goes through the normal wedge
  accounting (cap, then breaker). Otherwise the state returns to `Quarantined (Waiting)` with the new entry merged, and the hold restarts.
- *During a trial* the trial worker publishes like any worker (generation-gated). A failure publishes `Inactive`, as every abandonment
  does. Its status is shown as a recovery attempt, not as tracking (M.7).

**M.6 State transitions.** `TrackerState` gains `Quarantined`, with the sub-states `Waiting` (no worker) and `Trial` (one trial worker).
The quarantine record lives in `TrackerProcessGuard`, which is process-wide like the cap. Precedence on a wedge: `ThreadCap` >
`CircuitBreaker` > `Quarantined`. Every abandonment, from any state, goes through `TrackerProcessGuard.AbandonThread` (cap) and the
breaker window.

| # | From | Event | Guard | To | Actions |
|---|---|---|---|---|---|
| **Entering** | | | | | |
| Q1 | Running / Recovering | worker abandoned | lifetime cap reached | Disabled (`ThreadCap`) | entry recorded (stats only); permanent, as before |
| Q2 | Running / Recovering | worker abandoned | breaker trips | Disabled (`CircuitBreaker`) | entry added; Retry follows Q22/Q23 |
| Q3 | Running / Recovering | worker abandoned (incl. a recovery attempt, suspect = shell) | otherwise | Quarantined.Waiting | generation bumped + `Inactive` (as before); entry added; **no replacement**; hold starts at zero |
| **Waiting** | | | | | |
| Q4 | Q.Waiting | tick: every entry resolved (E1/E2) | — | Running | record cleared; one worker started (reserved; hooks untouched) |
| Q5 | Q.Waiting | tick: E3 complete | some suspect not yet tried; abandoned < cap | Q.Trial | one trial worker; all current suspects marked tried; record kept |
| Q6 | Q.Waiting | tick: E3 complete | every suspect tried | Q.Waiting | nothing (no automatic worker; waits for E1/E2/Retry) |
| Q7 | Q.Waiting | tick: hang evidence | — | Q.Waiting | hold restarts at zero; a candidates entry with exactly one hung candidate becomes known |
| **Trial** | | | | | |
| Q8 | Q.Trial | deadline (10 s): not abandoned and ≥ 1 full enumeration | — | Running | record cleared; the trial worker continues as the normal worker |
| Q9 | Q.Trial | deadline: no full enumeration | — | Q.Waiting | trial stopped (≤ 2 s; abandoned and counted if it does not end); hold restarts; suspects stay tried |
| Q10 | Q.Trial | trial worker abandoned or did not start (**repeated trial failure**) | Q1/Q2 first | Q.Waiting | entry merged; hold restarts; next automatic trial only for a not-yet-tried suspect (M.5) |
| Q11 | Q.Trial | every entry resolved by E1/E2 before the deadline | — | Running | record cleared; the trial worker becomes the normal worker |
| **Delayed completion / replacement (any state with a record)** | | | | | |
| Q12 | any | a zombie's operation completes or its thread exits (E1) | — | — | that entry resolved; if the record is then empty: Waiting → Q4, Trial → Q11, Disabled → Q14, Stopped → cleared at the next Start (Q17). Never publishes, never refunds the cap |
| Q13 | any | a suspect's process exits or its pid is reused (E2, incl. a **shell restart**) | — | — | as Q12. After a shell replacement the next worker connects to the new shell, or enters `Recovering` by itself (§ F) if the shell window is not back yet |
| Q14 | Disabled (`CircuitBreaker`) | record becomes empty | — | Disabled | no automatic restart (the breaker latch stands); a later Retry follows Q23 |
| **Stop / Start** | | | | | |
| Q15 | Q.Waiting | Stop | — | Stopped | monitor stops with the watchdog; record kept |
| Q16 | Q.Trial | Stop | — | Stopped | trial worker stopped (≤ 2 s; abandoned and counted if it does not end); its suspects stay tried; record kept |
| Q17 | Stopped | Start (this or a **new tracker instance**) | record unresolved after an immediate E1/E2 check | Q.Waiting | hooks and watchdog started, no worker; hold starts at zero |
| Q18 | Stopped | Start | record empty (or resolved by that check) | Running | as before |
| **Retry** | | | | | |
| Q19 | Running / Recovering / Stopped | Retry | — | unchanged | `NotDisabled` (as before) |
| Q20 | Q.Trial | Retry | — | Q.Trial | refused: `TrialInProgress` |
| Q21 | Q.Waiting | Retry | hang evidence now → refused `ServerNotResponding`; otherwise abandoned < cap | Q.Trial | an **explicit** trial: needs no hold and no untried suspect; record kept; breaker and cap apply to its wedge |
| Q22 | Disabled (`CircuitBreaker`) | Retry | record unresolved: hang evidence → refused `ServerNotResponding` (window kept); otherwise abandoned < cap | Q.Trial | breaker window cleared (Q1 rule), explicit trial |
| Q23 | Disabled (`CircuitBreaker`) | Retry | record empty, abandoned < cap | Running | as before |
| Q24 | Disabled (`ThreadCap`) | Retry | — | Disabled | `ThreadCapReached` (as before) |

*Worked example: one frozen process, repeated failures.* Wedge 1 → Waiting. After ≥ 30 s with a 10 s clean hold → automatic trial.
The trial wedges (wedge 2) → Waiting, and the suspect is tried, so no more automatic trials. The user presses Retry while the window
looks fine → explicit trial. It wedges (wedge 3) → breaker → `Disabled (CircuitBreaker)`. Retry is refused while hang evidence
remains; once allowed, its trial wedge (4) is followed by Q22 again… until the 6th abandonment → `ThreadCap`. Abandoned workers never
exceed 6, and no worker was ever started automatically twice against the same server. If the process is resumed at any point, E1
resolves the record within about 1 s.

**M.7 Status shown to the user.** `TrackerStats.Availability` (and the T3.4 Settings card) distinguishes:

| Availability | When | Meaning for the user |
|---|---|---|
| `Tracking` | Running | normal |
| `TemporarilyUnavailable` | Recovering (shell down or restarting, § F) | resumes by itself, nothing to do |
| `Quarantined` | Quarantined.Waiting | "An Explorer window is not responding. Tracking resumes when it responds or is closed." Retry offered (may be refused with a reason) |
| `RecoveryAttempt` | Quarantined.Trial | trying again; no action offered |
| `Paused` | Disabled (`CircuitBreaker`) | "paused after repeated problems", Retry (§ B.6) |
| `PermanentlyOff` | Disabled (`ThreadCap`) | off until Folder Notes restarts, no Retry button (§ B.6) |
| `Stopped` | Stopped | panel off |

The wording belongs to T3.4 and spec 05.

**M.8 Bounds: cannot be bypassed.** (a) *Live workers ≤ 1:* a trial and a normal worker share the one slot
(`TryReserveThread(Worker)`), and Waiting holds none. (b) *Lifetime cap 6:* every abandonment (wedge, start timeout, trial deadline
stop, `Stop`) goes through `AbandonThread`. Trials and Retry check `abandoned < cap` first. E1 never decrements the count. The record
and the count are both process-wide. (c) *Breaker 3 / 10 min:* every wedge counts, trial wedges included; only an explicit Retry clears
the window (Q22, as Q1 decided). (d) *Threads:* the monitor runs on the watchdog thread. No thread is added; with the watchdog's 1 s wait
the owned-at-once maximum stays 10. (e) *One tracker per process:* unchanged. A new instance meets the same record (Q17).
(f) *Automatic workers per server ≤ 1* (M.5). Residual risk, unchanged: a server that is merely slow costs an abandoned worker each time
(VM plan stage C measures how often).

**M.9 Hang pre-screen (depends on P7).** Before a `PathRead`, if `IsHungAppWindow(frame)` is true, return `Transient` without calling
(retry ladder, § D.4). Before an enumeration, if any Explorer frame is hung, defer it (`Transient`). This avoids a wedge, and therefore
an abandoned worker, for a window that has already been hung for more than 5 s (the Windows threshold). It cannot catch a freeze younger
than that. *Built (T3.2.6):* the same check also defers a worker's **first connection** (`CoCreateInstance` + enumeration at start or
in recovery) to the recovery back-off, because that connection enumerates every Explorer process too. While that lasts, a running
worker reports `Recovering` (availability `TemporarilyUnavailable`). Skipped calls are counted (`PreScreened`). `IWin32Shell` gains
`IsHung(hwnd)` and `AnyExplorerFrameHung()` (frames and the shell window).

**M.10 Observability.** `TrackerStats`: `Availability`, `QuarantineEpisodes`, unresolved entries (site, suspect kind and pid, age),
releases by E1, E2 and passed trial, trials started (automatic/explicit), passed, inconclusive and wedged, `LastCancelHr`,
`LastWedgeSite`, `JournalDefects`, `FullEnumerations`, `PreScreened`. Logged once at each entry and release.

**M.11 Tests.** Deterministic, with a fake clock and a fake server probe: every row Q1–Q24; the same-server rule; at most one automatic
trial per server; holds that restart on hang evidence; the 30 s minimum; pid reuse is not E1/E2; journal sequence-lock torn reads are
indeterminate; a stale or dangling record is never active; the cancel HRESULT never releases; the record survives Stop/Start and a new
instance; the cap and live-worker limit hold across trial loops (a model test that drives random event sequences and asserts M.8 after
each step). Timing: supervisor + watchdog with a blocking fake gateway (no worker while Waiting; release ≤ 1.5 s after the block ends;
trial deadline). Real, **VM only**: VM plan stage D.

**M.12 Interpretations fixed while building it (T3.2.6, 2026-11-03).**
- *The machine records the entry.* `OnWedge(entry)` adds the entry to the record inside the machine's lock, in every outcome (Q1–Q3),
  so no monitor look can see a quarantined state without its entry.
- *A worker that does not start in time* (its first connection hung) is one wedge and **nothing more**: the earlier "create another
  one until the breaker stops it" loop is gone (rule 4).
- *An inconclusive trial that stopped cleanly* (`TrialStopped`) also bumps the generation and publishes `Inactive`: the trial may have
  published, and quarantine means "no folder shown".
- *Start after Stop* restarts the hold from zero (Q17). The supervisor runs one E1/E2 assessment just before `Start` decides.
- *The monitor keeps looking while `Disabled (CircuitBreaker)`* with unresolved entries (still 1 s, still Win32-only), so the record
  can clear by itself (Q14). It stops with the watchdog when the tracker is stopped. With nothing unresolved it does not run.
- *A process that cannot be opened* (access denied) is treated as **running** and never as ended: a false E2 would release a
  quarantine. The cost is that E2 cannot resolve such a suspect, so it waits for E1 or a trial.
- *Publishing `Inactive` while already inactive is suppressed* (no event), as for every no-op publish (§ D.6).
- *Journal levels.* The engine's operations are `RootCreate`, `Enumerate` (target = shell), `PathRead` (target = the frame's
  process), `Release` (map sweeps/drops, root disposal). The gateway's per-item `ItemProbe`/`ItemRead` nest inside `Enumerate`. A
  third level never occurs in the code; if it did, it would be counted as a defect.
- *Evidence for a trial* is the count of validated full enumerations (`TryConnect` success, or a `Rebuild` that answered). The trial
  passes if that count grew between its start and its deadline.

#### K. Interpretations fixed while building the COM layer and thread hosts (T3.2.3, 2026-11-03)

Code in `src/FolderNotes.App/Explorer/Com/` (interop, `ComRef`, gateway) and `Explorer/Native/` (hosts):
- **Gateway shape.** `IShellWindowsGateway.TryEnumerateTabs(list)` returns an HRESULT (0, or the root's failure to classify) and appends
  `DiscoveredTab<TabHandle>`; `TryReadPath(handle)` returns `PathReadResult(Path, Hr)`; a virtual folder is success with a null path.
  Nothing throws for a COM failure; a stray exception is turned into its HRESULT. `IShellWindowsGatewayFactory.TryCreate` is separate so
  recovery can retry creation. A tab whose `GetWindow` is 0 is keyed by its frame (the implicit tab of 04 C); a window with no
  `IShellBrowser` is skipped (nothing to read).
- **What is kept.** Only the `IShellBrowser` per tab (`ComTabHandle`). The dispatch item, `IShellView` and `IPersistFolder2` live inside one call and
  are released there. `RcwTracker` counts live wrappers and any release from a thread other than the acquiring one (must be 0).
  *Not provable by a counter:* that `FinalRelease` actually dropped the last Explorer-side reference; S5/P1 measured that on a proxy, and T3.2.5
  re-checks it against Explorer.
- **Worker host.** `StaWorkerHost` is the one STA thread: `CoInitializeEx`, `CoEnableCallCancellation`, its own queue and loop, delegates `onStart`
  / `onWake` / `onStop` / posted actions all running on it, each bracketed for the watchdog by `Evaluate`, exceptions logged and swallowed.
  It is the router's wake target. It is started once; a replacement is a new object with the new generation. `MarkAbandoned` makes it ignore everything.
- **Watchdog thread.** One `ExplorerTracker.Watchdog` thread for the tracker's life, waiting on an event (idle = 0 CPU, tested). It reads the watched
  worker's `EvaluationWatchdog`: `CoCancelCall(threadId, 0)` at the cancel time, then reports the wedge. It references no hook and no COM object (guard test).
- **Supervisor.** `WorkerSupervisor` does the order the spec asks for, under one lock: `Detach` and un-watch the old worker, `OnWedge` (generation bump and
  `Inactive` first), then at most one replacement, started within a bound (1 s), attached, watched. A replacement that does not start counts as another
  wedge, so the breaker and the cap bound that loop. `Stop`: detach → end the worker (≤ 2 s, else abandon) → stop the watchdog (≤ 1 s); an
  abandoned-on-Stop worker **counts against the lifetime cap** (`Stop(workerAbandoned)`) but not the breaker (it was not an evaluation wedge).
- **Start after exhaustion.** `Start` is refused with `Disabled` whenever the process-wide count has reached the cap, even if the tracker object is new
  or got there through stuck Stops.
- **Still not built (T3.2.4):** the evaluation itself (what `onWake` does), scoped-hook policy, recovery, publishing, DI. The hook thread is
  started and stopped by the composition, not by the supervisor, so the supervisor cannot touch a hook (test).

#### I. Interpretations fixed while implementing the pure logic (T3.2.2, 2026-11-01)

The wording above left these open; the code and its tests now fix them (`src/FolderNotes.App/Explorer/`):
- **Ladder offsets** (D.4: +50, +150, +400, +1000 ms) are measured from the *start of the first failing evaluation*, not
  between rungs. A rung already overdue when its predecessor finishes runs at once. Retries respect the throttle window.
- **Only signal-driven failures start a ladder.** A failing poll or reconcile neither starts, restarts nor cancels one, so
  "then stop until the next signal" holds and a persistent failure costs one read per poll interval, not a ladder per poll.
  A good read ends any ladder. When the last rung fails the scheduler reports `LadderExhausted`; the host then marks the
  frame unresolved (G, L8). *That per-frame memory is host state and is built in T3.2.4.*
- **A confirm pass never chains another confirm.** "Changed" means the evaluation changed what was published.
- **Poll timing:** the next poll is due one interval after the last *completed* evaluation; the interval is 500 ms while the
  last signal is < 10 s old and 2 s after that; any signal returns it to 500 ms. Timed work (confirm, retry, reconcile, poll)
  never starts inside a throttle window; it waits for the window to end. Any evaluation satisfies a due confirm and retry; a
  due reconcile rides on whichever evaluation runs next (`Rebuild = true`).
- **Newest-seen sequence:** a sequence is taken when an evaluation starts and counts as "seen" when it finishes, whether it
  published, was a no-op or produced nothing. Anything older that finishes later is dropped, so a stale read can never replace a
  newer one, even if the newer one was a transient failure.
- **Breaker window:** a wedge counts while its age is *below* 10 minutes (a wedge exactly 10 minutes old has expired).
- **Disabled:** `Stop()` leaves a Disabled tracker Disabled and keeps the process slot (no second tracker may take its place);
  `Start()` is refused; only `Retry()` leaves it.
- **Abandon ordering:** `OnWedge()` invalidates the publisher's generation (publishing Inactive) before it returns a
  replacement, so an abandoned worker cannot publish at any moment a replacement exists.
- **Cache recency:** a rebuild replacing a known tab's handle does not count as a use; only `TryGet` does.
- Added pure parts the plan implied: `StatePublisher` (the single publish gate), `EvaluationWatchdog`, `ActiveTabResolver`,
  `ForegroundResolver`, `RootValidator`. `FakeShellWindowsGateway` is **not** built here: the gateway interface is defined in
  T3.2.3, and T3.2.2's tests use a test-only `FakeExplorerWorld`.

### Docking the panel (`PanelDocker`)

- The panel is a WinUI 3 window with no title bar (`OverlappedPresenter`, `IsResizable` width
  only), set as a **tool window** (`WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE` until clicked; REQ-038).
  It never appears in Alt+Tab or the taskbar.
- **Owner relationship:** `SetWindowLongPtr(GWLP_HWNDPARENT, explorerHwnd)` makes the panel an
  owned window of the Explorer window. It then minimizes with Explorer and stays directly above
  it in z-order, without being topmost over everything. *Spike S2 confirms that WinUI 3 handles
  re-owning cleanly.*
- **Position modes:**
  - *Docked outside right/left* (default): the panel sits flush against the Explorer edge. If
    there's no room on screen (Explorer is maximized or snapped to that edge), fall back to
    *docked inside*.
  - *Docked inside*: overlays the right edge of Explorer's content area (below the toolbar).
    Semi-transparent Acrylic, collapsible to a 28 px tab.
  - *Floating*: free position, remembered per monitor.
- Move tracking: `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` filtered to the Explorer HWND
  (`idObject == OBJID_WINDOW`), plus `EVENT_SYSTEM_MOVESIZESTART/END` and
  `EVENT_SYSTEM_MINIMIZESTART/END`. Reposition with `SetWindowPos(SWP_NOACTIVATE)`.
- Per-monitor DPI v2: compute in physical pixels from `GetWindowRect` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` so the panel sits flush with no gap from the invisible resize borders.
- Hide the panel when: no Explorer window is in the foreground, the path is null, or the panel is
  turned off. Exception: when the panel itself has focus, keep it visible.

### S2 findings (2026-10-31, Windows App SDK 2.5.1, .NET 9, 1 monitor at 125%)

13/13 scripted checks passed: Explorer keeps focus, the panel isn't in Alt+Tab or the taskbar,
it's owned by Explorer, it follows programmatic move/resize (median **14 ms**, max 34 ms,
**0 px** edge error), maximize docks it inside, Win+Left docks it outside right, Win+Right docks
it outside left, it hides while Explorer is minimized and re-docks on restore, it stays above its
owner in z-order, it can be activated for inline editing, and it survives the Explorer window
closing.

Implementation requirements that came out of this:
- **The app must ship an `app.manifest` declaring `PerMonitorV2`.** Without one, an unpackaged
  WinUI 3 app is DPI-unaware: its `GetWindowRect`/`SetWindowPos` coordinates are virtualized
  (1.25× off at 125%), while `DwmGetWindowAttribute` stays physical. That produced 70–337 px
  docking errors.
- Show with `AppWindow.Show(activateWindow: false)`, set `AppWindow.IsShownInSwitchers = false`,
  and use `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. Drop `WS_EX_NOACTIVATE` only while the user edits inline.
- Use `OverlappedPresenter.SetBorderAndTitleBar(true, false)`. The panel has its own invisible
  DWM borders (8 px each side here), so subtract the `WindowRect` − `FrameBounds` delta when positioning.
- **Detach on owner destroy:** hook `EVENT_OBJECT_DESTROY` for the owner HWND → set
  `GWLP_HWNDPARENT = 0` and hide. Re-own with `GWLP_HWNDPARENT` whenever the tracker reports a
  different foreground Explorer frame.
- The WinEvent hooks filtered by Explorer's PID (`LOCATIONCHANGE`, `MOVESIZESTART..MINIMIZEEND`,
  `DESTROY`) run on the WinUI UI thread with no extra message loop needed.
- Builds with the plain `dotnet build` command line (no Visual Studio), unpackaged + self-contained.
- **Still to verify by hand** (can't be automated or tested here): smoothness during an
  interactive drag (`spikes/S2-dock` → `S2Dock.exe watch`), mixed-DPI multi-monitor drags,
  Windows 10.

### Panel states

| State | Shown |
|---|---|
| Hidden | No Explorer in foreground, virtual folder, or disabled |
| Empty (collapsed tab) | Folder has no note and *Hide when empty* is on: a 28 px vertical tab with a "+" |
| Empty (expanded) | "No note for **<folder>**" + [Add note] button, plus an inherited-context card (REQ-035) |
| Note | Full note card (see `05-ui-design.md`) with inline edit |
| Sidecar found | "A note travelled with this folder" + [Import] (REQ-072) |

## 4.3 Hover tooltip: infotip handler (REQ-040..043)

- `FolderNotes.ShellExt.dll` implements `IQueryInfo` + `IPersistFile`, which Explorer uses to give
  the handler the hovered item's path via `IPersistFile::Load`.
- Registration (per-user, HKCU, no admin):
  ```
  HKCU\Software\Classes\CLSID\{<our CLSID>}\InprocServer32
      (Default) = <install>\FolderNotes.ShellExt.dll
      ThreadingModel = Apartment
  HKCU\Software\Classes\Directory\shellex\{00021500-0000-0000-C000-000000000046}
      (Default) = {<our CLSID>}
  ```
  *Spike S3 must confirm that Explorer on Windows 11 calls infotip handlers registered for
  `Directory` in Details, Tiles, Icons and the navigation pane, and how it combines our text with
  the default infotip.* If folders aren't supported reliably, use the fallback (below).
- `GetInfoTip` output (plain text, max ~6 lines):
  ```
  📌 Client: Acme Ltd
  Keep · client, do-not-delete · Keep until 1 Jan 2027
  Contract docs and signed SOWs. Ask Sam before
  archiving anything here.
  ```
  (S3: the emoji renders as a monochrome glyph, which is acceptable.)
- Hard rules:
  - Lookup budget: 20 ms target, 50 ms hard cap. The SQLite `busy_timeout` is 30 ms, and if the
    total runs over, return `E_FAIL` so Explorer shows its default tooltip.
  - Read-only DB handle (`SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX`), opened lazily and cached
    per thread. Never create the DB file. If it's missing, return `E_FAIL`.
  - No writes, no network, no loading .NET, no dependency on the app running.
  - Static CRT (`/MT`), no extra DLL dependencies besides system ones, and sqlite3 compiled in.
  - `DllCanUnloadNow` is reference-counted correctly so Explorer can unload us after an update.
- Updating the DLL while Explorer has it loaded: install to a versioned folder
  (`ShellExt\1.2.0\`), re-point the registry, and clean up old versions on next login.
- **Fallback if S3 fails:** offer an opt-in "Explorer tooltip compatibility" mode that writes
  `desktop.ini` `InfoTip` (REQ-073), clearly labelled because it modifies the folder.

### S3 findings (2026-10-31, Windows 11 23H2 build 22631, MSVC 14.44, Windows SDK 26100)

✅ **Folder infotip handlers work on Windows 11** when registered per-user under
`HKCU\Software\Classes\Directory\shellex\{00021500-…}`. No admin rights or approval list are needed.

| View | Handler called + our tooltip shown |
|---|---|
| Details (column header confirmed) | ✅ (screenshot) |
| List | ✅ |
| Large icons / Medium icons / Small icons | ✅ (screenshots) |
| Tiles | ⚠️ worked in 1 of 3 automated runs. In the failed runs no tooltip appeared at all (not even Explorer's default), so it's most likely a hover-automation artifact |
| Content | ⚠️ not confirmed (same symptom as Tiles) |
| Navigation pane, Desktop | ❓ not tested; manual check in T4.5 |

What we learned:
- **Our text replaces Explorer's default folder tooltip** (date created, size, folder contents)
  rather than being added to it. Design consequence: for noted folders the tip should carry the
  most useful information itself.
- **Open question for T4.3:** does returning `E_FAIL` (no note, or over budget) fall back to
  Explorer's default tooltip? REQ-041 assumes it does. Verify this first, before building on it.
  If it doesn't, the handler must return a default-equivalent text itself.
- **Explorer needs a restart to pick up a newly registered handler.** A fresh registration wasn't
  called until `explorer.exe` restarted. That affects the install/enable UX in T4.4: offer
  "Restart Explorer now" or say "takes effect after next sign-in". The same applies after
  unregistering, because Explorer keeps the DLL loaded.
- Explorer calls `IPersistFile::Load(path)` → `GetInfoTip(flags=0x8 QITIPF_USESLOWTIP)`, on
  **different threads** of the same process. That confirms the per-thread connection cache.
- Explorer seems to cache tips per item: hovering the same folder again in another view didn't
  call the handler again. A note edit may therefore not show in the tooltip until the view
  refreshes. Check this in T4.3 and, if needed, call `SHChangeNotify(SHCNE_UPDATEITEM)` for the
  folder after saving.
- Latency of the stub handler: in-process create + `Load` + `GetInfoTip` has a median of
  **0.13 ms**, p99 0.45 ms. The first call takes 58 ms (DLL load, once per process). Inside
  Explorer, `GetInfoTip` took 0.003–0.006 ms. The real budget goes into opening and querying SQLite.
- The 📌 emoji renders as a monochrome glyph (acceptable). **C++ must be compiled with
  `/utf-8`**: without it, `·` in the source came out as `Â·`.
- Built with `/W4 /WX /MT`, a 154 KB DLL with no runtime dependencies.

## 4.4 Explorer restarts & robustness

- Restart detection, recovery, COM time-boxing, the wedged-thread rule and the circuit breaker are specified in
  § ExplorerTracker design, B.6 and F. `TaskbarCreated` is one of four detectors, not the only one.
- If Explorer is hung, the tracker publishes "no active folder" and never blocks the UI thread.
- The tracker and docker can be fully disabled in settings without affecting the rest of the app (with the
  panel off, the tracker is not running at all).
