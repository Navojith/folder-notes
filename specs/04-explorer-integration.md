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

**Tier 2 (Phase 4b):** an `IExplorerCommand` implementation in `FolderNotes.ShellExt.dll`,
registered through a **sparse MSIX package** (`desktop4:FileExplorerContextMenus`,
`desktop5:ItemType Type="Directory"`), so it appears in the Windows 11 top-level menu. The label
changes dynamically: "Add folder note" or "Edit folder note" (`GetTitle` queries the DB, with the
same 50 ms budget as the tooltip).

## 4.2 Companion panel (REQ-030..038)

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
5. **Fallback:** if events misbehave, poll the foreground Explorer's active folder every 250 ms
   while it has the foreground (cheap, with no polling in the background).
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
  (Emoji only if spike S3 shows that the tooltip renders it correctly. Otherwise use plain text.)
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

## 4.4 Explorer restarts & robustness

- Listen for the `TaskbarCreated` registered message. When it arrives, rebuild all
  `IShellWindows` subscriptions.
- All COM calls into Explorer have timeouts. If Explorer is hung, the tracker treats it as "no
  active folder" and doesn't block the UI.
- The tracker and docker can be fully disabled in settings without affecting the rest of the app.
