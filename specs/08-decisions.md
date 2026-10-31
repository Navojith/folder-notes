# 08 — Architecture Decision Records

Status values: **proposed** → **accepted** / **rejected** / **superseded by ADR-xxx**.

---

## ADR-001: WinUI 3 + C# .NET 8 for the app — *accepted*

**Context:** The UI needs to look native to Windows 11 (Mica, Fluent controls, dark mode,
accessibility) and work closely with Win32/COM (Explorer automation, hotkeys, window ownership).
**Options:** WinUI 3; WPF + WPF-UI; Tauri/Electron; Avalonia.
**Decision:** WinUI 3 (Windows App SDK) with C#.
**Consequences:** Best native fidelity and direct access to Win32 interop through CsWin32. Tool-window
behaviour needs some Win32 work (spike S2). Windows only, which is a stated non-goal anyway.
Electron and Tauri were rejected: they're heavier when idle (REQ-080) and don't look native.

## ADR-002: The shell extension is native C++, not managed — *accepted*

**Context:** Infotip handlers run *inside explorer.exe*. Microsoft explicitly recommends against
writing in-process shell extensions in managed code, because of runtime version conflicts, load
time and stability risk.
**Decision:** A small C++20 DLL with a static CRT and SQLite compiled in, read-only.
**Consequences:** Two languages in the repo. Path normalization exists twice, so we keep shared
test vectors (`06-testing.md`) to make sure both implementations agree.

## ADR-003: Central SQLite DB as the source of truth, sidecars opt-in — *accepted*

**Context:** Notes have to be searchable, survive renames, and not pollute user folders (principle 2).
**Decision:** One SQLite DB per user, in WAL mode, with the app as the single writer. Sidecar
`.foldernote.json` files are written only per note and only when the user opts in.
**Consequences:** Notes don't travel with folders by default (mitigated by export and the opt-in
sidecar). The shell extension can read the DB directly, so there's no IPC and no dependency on the
app running.

## ADR-004: Explorer tracking via IShellWindows + WinEvents — *proposed (pending S1)*

**Context:** We need the active folder of the foreground Explorer window, including Windows 11 tabs.
**Decision:** `IShellWindows` enumeration plus `DWebBrowserEvents2`, with foreground and location
WinEvent hooks and a 250 ms poll fallback while Explorer has the foreground.
**Alternatives:** UI Automation reading the address bar (fragile, localized); a shell extension
that pushes events from inside Explorer (more in-process risk).
**Open question:** how reliable active-tab detection is on Windows 11. S1 decides.

## ADR-005: Two-tier context menu — *accepted*

**Context:** The Windows 11 top-level context menu only accepts `IExplorerCommand` handlers from
packaged apps (sparse package allowed). Classic registry verbs only appear under "Show more options".
**Decision:** Ship the Tier 1 registry verb for the MVP (no DLL, no package). Add Tier 2
`IExplorerCommand` with a sparse package in Phase 4b.
**Consequences:** Windows 11 users get one extra click in the MVP.

## ADR-006: Tooltip mechanism — *proposed (pending S3)*

**Decision (provisional):** An `IQueryInfo` infotip handler registered for `Directory` in HKCU.
**Fallback if S3 fails:** an opt-in `desktop.ini` `InfoTip` mirror (REQ-073), plus an expanded
panel preview to make up for it.
