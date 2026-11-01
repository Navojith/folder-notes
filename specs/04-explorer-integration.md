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
| `TrackerStateMachine` | pure | `Stopped → Running ⇄ Recovering`, `Wedged → Running`, `Disabled` (§B.6, §F). |
| `ExplorerTracker` | glue | STA thread, WinEvent hooks, message loop, watchdog, publishing. No decision the pure parts could make. |

Lives in `FolderNotes.App/Explorer/`. **Not started by `AppShell` in T3.2**; T3.4 starts it behind the panel
setting (REQ-037). With the panel off the tracker is not running at all: no thread, no hooks, no timer.

#### B. COM interop

1. **Typed only (ADR-007).** Hand-declared `[GeneratedComInterface]` interfaces (decision D1 in 09; recommended)
   for exactly the surface used: `IShellWindows` (`get_Count`, `Item`), `IServiceProvider`, `IShellBrowser` /
   `IOleWindow` (`GetWindow`, `QueryActiveShellView`), `IPersistFolder2` (`GetCurFolder`), `IWebBrowser2`
   (`get_HWND`, `get_LocationURL`) and the shell helpers `SHGetNameFromIDList(SIGDN_FILESYSPATH)` / `ILFree`.
   `IDispatch`-derived interfaces are declared with their four `IDispatch` slots as placeholders and are only
   called through their vtable. No `dynamic`, `InvokeMember`, `Type.GetTypeFromProgID` + reflection, or
   `Activator` on a COM type. `Item(VARIANT)` takes a private blittable `Variant` struct (`VT_I4` index); probe P1
   proves this before the task relies on it. A test fails if `dynamic` or `InvokeMember` appears under `Explorer/`
   (same style as T3.1's source guard). **User decision D1:** every interface and method signature is validated by
   T3.2.1 *before* implementation (vtable order against the SDK headers and a live call per method, `VARIANT`
   marshaling, `HRESULT` handling incl. `[PreserveSig]` vs throwing, `QueryInterface`/casting, RCW lifetime and
   release). If the probes show unreliable interop, **switch to CsWin32; do not add workarounds around a wrong
   interop definition.**
2. **One thread owns every COM object.** One dedicated `STA` thread named `ExplorerTracker` with its own message
   loop (also required by the out-of-context WinEvent hooks). Every RCW is created, used and released on it.
   No COM reference leaves that thread; what crosses to the UI is the immutable `ActiveFolderState` (an nint and
   a string).
3. **Ownership.** Every RCW lives in a `ComRef<T>` (`IDisposable`; `Release()` = `Marshal.FinalReleaseComObject`
   on the STA thread; idempotent). Owners: `ComShellWindowsGateway` owns the `IShellWindows` root; `TabMap` owns one
   `ComRef` per tab (the smallest interface that can read the path; the `IShellView`, folder and PIDL objects
   are acquired, used and released inside a single `TryReadPath`). Shutdown order: tab refs → root →
   `CoUninitialize`. A live-RCW counter (always on; it is one integer) is asserted to be 0 at the end of every
   gateway test. *Why prompt release matters beyond our memory:* a reference we keep to a closed tab keeps that
   object alive inside Explorer, so a stale reference is a small leak in Explorer, against principle 1.
4. **Not re-entrant.** While the STA thread waits on an outgoing call, COM dispatches window messages to it. The
   message handler is guarded: if an evaluation is running, a new request only sets the dirty flag. WinEvent
   callbacks do nothing but set a flag and post one message.
5. **Failure classification** (`ComFailureClassifier`). Nothing throws out of the loop: every entry point is
   wrapped, failures are counted, and logged at most once per 10 s per class.

   | Class | HRESULTs (examples) | Action |
   |---|---|---|
   | `Transient` | `RPC_E_CALL_REJECTED` 0x80010001, `RPC_E_SERVERCALL_RETRYLATER` 0x8001010A, `RPC_E_CALL_CANCELED` 0x80010002 | keep the entry; retry on the ladder (§D.4); publish nothing new until it resolves |
   | `Gone` | `RPC_E_DISCONNECTED` 0x80010108, `CO_E_OBJNOTCONNECTED` 0x800401FD, `RPC_E_SERVER_DIED` 0x80010007, `RPC_E_SERVER_DIED_DNE` 0x80010012, `RPC_S_SERVER_UNAVAILABLE` 0x800706BA, `RPC_S_CALL_FAILED` 0x800706BE, `RPC_E_INVALID_OBJECT` 0x80010114 | drop **that** entry (release it); if it came from the root `IShellWindows`, run recovery (§F) |
   | `Other` | `E_FAIL`, `E_NOINTERFACE`, `E_NOTIMPL`, `E_UNEXPECTED` | `path = null` for that tab this time; after 3 in a row on one entry, drop it |
6. **Time-boxing** (a COM call has no timeout of its own). Two layers, because the first may not apply to every call:
   - *Cancel.* The STA thread calls `CoEnableCallCancellation` once. A **watchdog** (a separate thread that waits
     on an event; it is idle unless an evaluation is running) records "evaluation started at t". Past
     **250 ms (tunable)** it calls `CoCancelCall(staThreadId, 0)`; the blocked call returns
     `RPC_E_CALL_CANCELED` (`Transient`). Probe P2 proves this against a real out-of-process server.
   - *Abandon.* If the evaluation is still blocked **1 s (tunable)** after the cancel (cancellation only works for
     calls through a COM proxy, and not inside non-COM waits), the thread is declared **wedged**: the watchdog
     bumps the `generation`, publishes `Inactive`, abandons the thread (background thread, never joined) and
     starts a fresh STA thread with an empty cache. Anything the old thread later produces carries the old
     generation and is dropped. Abandoned COM objects are **not** released from another thread (it could
     deadlock); they are leaked once, deliberately.
   - *Circuit breaker.* 3 wedges within 10 min → `Disabled`: publish `Inactive`, log once, and Settings shows
     "Explorer tracking paused after repeated problems" with a Retry (T3.4). It never retries by itself in a loop.
   - *Hard bounds (user decision D4).* (1) **At most one tracker instance per process** (a second `Start` on any
     instance while one is active is refused; enforced by a process-wide guard and tested). (2) **At most one live
     STA thread serves the tracker, plus at most one abandoned thread per wedge, and at most 3 abandoned threads
     ever per process lifetime** (the breaker is tripped by the 3rd wedge, and `Retry` after it is allowed only while
     fewer than 3 abandoned threads exist; at 3 the tracker stays `Disabled` until the app restarts). A replacement
     thread is never started while the previous one is still being cancelled. (3) **An abandoned thread cannot
     publish:** the single publish gate compares the caller's generation with the current one, and abandoning
     invalidates the generation *before* the replacement starts; also the abandoned thread's hooks and timers are
     removed by the watchdog (hooks are keyed by thread) and its message loop is told to quit. (4) Abandoned COM
     objects are leaked at most once per wedge and counted in `TrackerStats`. (5) **Until proven by probe P2, no text
     or code claims COM calls can be cancelled safely.** If P2 fails, the 1 s abandonment is the effective limit and
     this section says so.
   - `Stop`/`Dispose` never wait unbounded: post `WM_QUIT`, wait ≤ 2 s, then abandon.

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
   re-scoped on every foreground change, and they are removed while no Explorer frame is foreground. The callback
   does only: check `idObject == OBJID_WINDOW`, compare the root window's class against a tiny allow-list, set
   the dirty flag, post at most one message.
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

#### E. Reading the path

Primary: `QueryService(SID_STopLevelBrowser)` → `IShellBrowser.QueryActiveShellView` → `IPersistFolder2.GetCurFolder`
→ `SHGetNameFromIDList(SIGDN_FILESYSPATH)`; a failing conversion = virtual folder = `null`. Fallback:
`IWebBrowser2.get_LocationURL` → `PathCreateFromUrl`. The S1 late-bound route (`Document.Folder.Self.Path`) is not
used (ADR-007). Probe P3 decides which is primary, by parity on: normal, Unicode, UNC, mapped drive, This PC, Home,
Recycle Bin, a search result, a `.zip` browsed in Explorer, an MTP device. The tracker reports the string the
shell gives; checking that it is a real directory is the consumer's job (`IFolderIdentityService.TryResolve`).

#### F. Reliability and recovery

- **Explorer restart or crash.** Detected by any of: `Gone` from the root; the tray window forwarding
  `TaskbarCreated` (`ExplorerTracker.NotifyShellRestarted()`; the tracker's thread has no top-level window so it
  cannot hear the broadcast, but the tray window of ADR-012 already does); the process id of `GetShellWindow()`
  changing; `GetShellWindow() == 0`. Then, in order: (1) publish `Inactive` so the panel hides; (2) release every
  `ComRef` (dead proxies release fast; failures swallowed); (3) state `Recovering`; (4) try to recreate
  `IShellWindows` **only when `GetShellWindow() != 0`**, at +250 ms, +500 ms, 1 s, 2 s, then every 5 s, with no
  limit while the app runs; (5) on success rebuild the map, evaluate, publish. The WinEvent hooks belong to the
  system, not to Explorer, so they survive a restart; they are re-scoped (§D.2) and reinstalled if installing
  ever failed. The user never has to restart Folder Notes.
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
- **Stop.** `Stop()` removes hooks and timers, releases all `ComRef`s, ends the thread (bounded wait, §B.6) and
  publishes `Inactive`. `Start()` after `Stop()` starts from an empty cache.
- **The tracker only reads.** It never posts to, subclasses, injects into or modifies an Explorer window. A tracker
  fault ends in `Inactive` and one log line.

#### G. Limits imposed by Windows and the COM interfaces

Not every Explorer event can be observed reliably. Each limit and what the design does about it:

| # | Limit | Consequence in the design |
|---|---|---|
| L1 | WinEvents are asynchronous, unordered relative to COM state, can be coalesced or dropped under load, and `NAMECHANGE` does not guarantee the *path* changed. | Every event only means "re-read"; truth is read from the shell; the poll and the 30 s reconciliation stay. |
| L2 | There is **no documented event for "active tab changed"** or "navigation committed". S1 saw `NAMECHANGE` / `SHOW` / `HIDE` fire for them on build 22631; that is observed behaviour, not a contract, and may differ on 24H2 and later builds, and on Windows 10. | Poll fallback stays; the T3.5 matrix runs on 3 builds; without the tab class the logic degrades to one implicit tab. |
| L3 | Out-of-context hooks need a message loop and arrive as messages, so they are delayed while the STA thread is in a COM call. | Time-boxed calls (§B.6); not re-entrant (§B.4). |
| L4 | COM has no per-call timeout. `CoCancelCall` works only for calls through a COM proxy and only while the thread is in a COM wait. | Cancel, then abandon the thread; circuit breaker. |
| L5 | `IShellWindows` registers a window after its HWND exists and removes it around the HWND's destruction. | Retry ladder; `IsWindow` and `Gone` checks; reconciliation. |
| L6 | `TaskbarCreated` is broadcast only to top-level windows, only when a taskbar is created, and not when the shell restarts without one. | Forwarded from the tray window **and** detected independently (`Gone`, shell process change). |
| L7 | `CLSID_ShellWindows` can be served by a private, empty `rundll32.exe` server (§F). | Gate on the shell window; validate the enumeration. |
| L8 | Explorer windows at a higher integrity level (run as administrator) may not be readable from a non-elevated process. | Unsupported in v1 (user decision): the tracker reports no usable path (`null`) without crashing and **without retrying continuously**: an access-denied / `unresolved` result for an elevated frame is remembered per frame HWND and not retried until that frame navigates or loses and regains foreground. Verify by hand (09 O-23). |
| L9 | Virtual folders, libraries, Quick Access, search results, the Recycle Bin and MTP devices have no file-system path; a `.zip` browsed in Explorer reports a path that is a *file*. | `null` for the former; the consumer validates directories. |
| L10 | The desktop (`Progman` / `WorkerW`) and common file dialogs are not `CabinetWClass` frames. | Out of scope: `Inactive`. |
| L11 | During a pending navigation the shell still reports the old folder; HWNDs are recycled; the lock screen has no foreground window. | Publish the committed folder; the confirm pass; no foreground → `Inactive`. |
| L12 | Windows 10 22H2 has no tabs and could not be tested here (09 O-6). | Windows 11 evidence is not Windows 10 evidence; O-6 stays open until checked on Windows 10. |

Emitted event: `ActiveFolderChanged { FrameHwnd, TabHwnd, Path?, Sequence }` (§D.8).

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
