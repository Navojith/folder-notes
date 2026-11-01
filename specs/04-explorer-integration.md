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

### Detecting the active Explorer folder (`ExplorerTracker`)

1. `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, out-of-context, to see when an Explorer window
   (`CabinetWClass`) gains or loses the foreground.
2. Enumerate `IShellWindows` (`CLSID_ShellWindows`) to map HWND → `IWebBrowser2` → `Document`
   (`IShellFolderViewDual`) → `Folder.Self.Path`. Subscribe to `DShellWindowsEvents`
   (WindowRegistered/Revoked) to track new and closed windows.
3. For each Explorer browser object, sink `DWebBrowserEvents2::NavigateComplete2` so we hear about
   in-window navigation.
4. **Windows 11 tabs (REQ-034):** each tab is a separate entry in `IShellWindows` with the **same
   top-level HWND**. To find the active tab: for each candidate, get its `IShellBrowser` via
   `IServiceProvider::QueryService(SID_STopLevelBrowser)` → `GetWindow` → the tab's
   `ShellTabWindowClass` HWND. The active tab is the one whose `ShellTabWindowClass` window is
   visible (`IsWindowVisible`). Re-evaluate on `EVENT_OBJECT_SHOW`/`HIDE` for that class
   (in-window tab switch) plus NavigateComplete2. *Spike S1 must validate this.*
5. **Fallback:** poll the foreground Explorer's active folder every 500 ms while it has the
   foreground, as a safety net only (S1 showed events alone are enough). There's no polling in
   the background.
6. Virtual folders (This PC, Home, Gallery, Recycle Bin, search results, `shell:` namespaces) have
   no file system path. The tracker emits `path = null` and the panel hides.
7. All COM work runs on a dedicated **STA thread** with its own message loop, never on the UI
   thread. Events are marshalled to the UI with `DispatcherQueue`.

Emitted event: `ActiveFolderChanged { HWND explorerHwnd, string? path, RECT bounds }`, debounced to 50 ms.

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

- Listen for the `TaskbarCreated` registered message. When it arrives, rebuild all
  `IShellWindows` subscriptions.
- All COM calls into Explorer have timeouts. If Explorer is hung, the tracker treats it as "no
  active folder" and doesn't block the UI.
- The tracker and docker can be fully disabled in settings without affecting the rest of the app.
