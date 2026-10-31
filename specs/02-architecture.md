# 02 — Architecture

## Overview

```
┌────────────────────────── explorer.exe ──────────────────────────┐
│  FolderNotes.ShellExt.dll  (native C++, in-proc COM)             │
│   • IQueryInfo infotip handler  → reads notes.db (read-only)     │
│   • IExplorerCommand context menu (Win11 top-level, Phase 4b)    │
└───────────────▲──────────────────────────────────────────────────┘
                │ SQLite read-only (WAL), no IPC needed
┌───────────────┴──────────────────────────────────────────────────┐
│ %LOCALAPPDATA%\FolderNotes\notes.db   (SQLite + FTS5, WAL mode)  │
└───────────────▲──────────────────────────────────────────────────┘
                │ read/write (single writer)
┌───────────────┴──────────────── FolderNotes.exe ─────────────────┐
│ (one process, per user, single instance, tray icon)              │
│                                                                   │
│  App (WinUI 3)                                                    │
│   ├─ MainWindow: library, editor, orphaned, settings             │
│   ├─ SearchPalette: global hotkey popup                          │
│   └─ CompanionPanel: tool window docked to Explorer              │
│                                                                   │
│  Services                                                         │
│   ├─ ExplorerTracker  (foreground window + active folder events) │
│   ├─ PanelDocker      (positions panel relative to Explorer HWND)│
│   ├─ ReminderService  (schedules toasts)                         │
│   ├─ FolderIdentity   (file IDs, path normalization, relink)     │
│   ├─ SidecarSync      (opt-in .foldernote.json)                  │
│   └─ Single-instance + command router (activation args)          │
│                                                                   │
│  FolderNotes.Core (class library: domain, repository, search)    │
└───────────────────────────────────────────────────────────────────┘
        ▲
        │ "FolderNotes.exe --edit "<path>"" (context menu verb / IExplorerCommand)
        │ forwarded to the running instance via AppInstance redirection
```

## Tech stack

| Concern | Choice | Why (see ADRs) |
|---|---|---|
| App UI | **WinUI 3 / Windows App SDK 1.6+, C# .NET 8** | Native Fluent look, Mica, dark mode, accessibility (ADR-001) |
| MVVM | CommunityToolkit.Mvvm | Source-generated, low boilerplate |
| Storage | **SQLite** via `Microsoft.Data.Sqlite`, FTS5 | Single file, fast, concurrent readers via WAL (ADR-003) |
| Shell extension | **Native C++20 DLL**, WRL/`winrt::implements`, sqlite3 amalgamation | Microsoft advises against managed in-proc shell extensions (ADR-002) |
| Explorer tracking | `IShellWindows` + `DWebBrowserEvents2` + `SetWinEventHook` | Documented COM automation of Explorer windows (ADR-004) |
| Notifications | Windows App SDK `AppNotificationManager` | Toast with actions, works unpackaged |
| Hotkey | `RegisterHotKey` on a message-only window | Simple, reliable |
| Markdown | Markdig (parse) + custom WinUI renderer, or CommunityToolkit MarkdownTextBlock | Lightweight |
| Packaging | MSIX with **sparse package / external location**, plus an unpackaged per-user installer as fallback | Win11 top-level context menu needs package identity (ADR-005) |
| Tests | xUnit + FluentAssertions (Core), WinAppDriver/FlaUI (UI smoke), GoogleTest (ShellExt) | |

## Solution layout

```
folder-notes/
├─ CLAUDE.md
├─ specs/                       ← source of truth
├─ spikes/                      ← Phase 0 throwaway code
├─ src/
│  ├─ FolderNotes.Core/         ← domain, repository, search, identity (no UI deps)
│  ├─ FolderNotes.App/          ← WinUI 3 app, services, tray
│  ├─ FolderNotes.ShellExt/     ← C++ DLL (infotip + IExplorerCommand)
│  └─ FolderNotes.Package/      ← MSIX / sparse package manifest
├─ tests/
│  ├─ FolderNotes.Core.Tests/
│  ├─ FolderNotes.App.Tests/    ← view-model + service tests
│  ├─ FolderNotes.UITests/      ← FlaUI smoke tests
│  └─ FolderNotes.ShellExt.Tests/
└─ FolderNotes.sln
```

## Process & concurrency model

- **Single writer:** only `FolderNotes.exe` writes to `notes.db`. All writes go through
  `INoteRepository` on a dedicated serialized writer (async queue).
- **Readers:** the app's UI and the shell extension in Explorer. SQLite WAL lets readers run
  alongside the writer. The shell extension opens with `SQLITE_OPEN_READONLY`,
  `busy_timeout=30 ms`, and caches the connection per thread.
- **Change notification (app internal):** the repository raises `NoteChanged(folderId)` events.
  Panel, library and search subscribe. No cross-process notification is needed because Explorer
  asks for infotips on demand.
- **Single instance:** `AppInstance.FindOrRegisterForKey("FolderNotes")`. A second launch with
  args (`--edit <path>`, `--search`, `--open-note <id>`) is redirected to the main instance and the
  second process exits.

## Command-line / activation contract

| Args | Effect |
|---|---|
| *(none)* | Start to tray (or bring main window forward if already running) |
| `--edit "<folderPath>"` | Open the editor for that folder (create if none) — REQ-003/004 |
| `--search [query]` | Open search palette |
| `--open-note <noteId>` | Open note in main window |
| `--background` | Start silently to tray (used at login) |
| toast activation `action=openFolder&id=…` | From REQ-021 |

## Key interfaces (FolderNotes.Core)

```csharp
public interface INoteRepository {
    Task<Note?> GetByFolderAsync(FolderRef folder, CancellationToken ct = default);
    Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Note> UpsertAsync(Note note, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);   // soft-delete for undo
    Task RestoreAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken ct = default);
    event EventHandler<NoteChangedEventArgs> NoteChanged;
}

public interface ISearchService {
    Task<IReadOnlyList<SearchHit>> SearchAsync(string rawQuery, int limit, CancellationToken ct);
}

public interface IFolderIdentityService {
    FolderRef Resolve(string path);                 // path → normalized path + volume/file id
    string? Locate(FolderRef storedRef);            // find the current path for a stored ref
}

public interface IExplorerTracker {                 // App layer
    event EventHandler<ActiveFolderChangedEventArgs> ActiveFolderChanged; // HWND, path or null
}
```

## Error handling & logging

- Serilog to `%LOCALAPPDATA%\FolderNotes\logs\` (rolling, 7 days). No PII beyond folder paths, and
  logs never leave the machine.
- Shell extension: no logging to disk by default (REQ-042). `OutputDebugString` in debug builds
  only. Every COM entry point is wrapped in `try { … } catch (...) { return E_FAIL; }`.
- The app must survive Explorer restarts. The tracker re-subscribes when `explorer.exe` restarts
  (`TaskbarCreated` message).
