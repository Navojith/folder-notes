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
| App UI | **WinUI 3 / Windows App SDK 2.x, C# .NET 10 (LTS)** | Native Fluent look, Mica, dark mode, accessibility (ADR-001) |
| COM interop | **CsWin32 / `[GeneratedComInterface]`, typed only** | `dynamic` COM crashed and was slow in S1 (ADR-007) |
| MVVM | CommunityToolkit.Mvvm | Source-generated, low boilerplate |
| Storage | **SQLite** via `Microsoft.Data.Sqlite`, FTS5 | Single file, fast, concurrent readers via WAL (ADR-003) |
| Shell extension | **Native C++20 DLL**, WRL/`winrt::implements`, sqlite3 amalgamation | Microsoft advises against managed in-proc shell extensions (ADR-002) |
| Explorer tracking | `IShellWindows` + `DWebBrowserEvents2` + `SetWinEventHook` | Documented COM automation of Explorer windows (ADR-004) |
| Notifications | Windows App SDK `AppNotificationManager` | Toast with actions, works unpackaged |
| Hotkey | `RegisterHotKey` on a message-only window | Simple, reliable |
| Markdown | Markdig (parse) + custom WinUI renderer, or CommunityToolkit MarkdownTextBlock | Lightweight |
| Packaging | MSIX with **sparse package / external location**, plus an unpackaged per-user installer as fallback | Win11 top-level context menu needs package identity (ADR-005) |
| Tests | xunit.v3 on Microsoft Testing Platform + Shouldly (Core), FlaUI (UI smoke), GoogleTest (ShellExt) | FluentAssertions v8 needs a paid licence for commercial use, so it was replaced by Shouldly (T1.1) |
| Build | `FolderNotes.slnx` via the `dotnet` CLI; ShellExt via CMake + Ninja (`eng/build-shellext.cmd`) | Builds without opening Visual Studio, and CI-friendly |

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
│  ├─ FolderNotes.ShellExt/     ← (CMake project, not in the .slnx)
│  └─ FolderNotes.Package/      ← MSIX / sparse package manifest
├─ tests/
│  ├─ FolderNotes.Core.Tests/
│  ├─ FolderNotes.App.Tests/    ← view-model + service tests
│  ├─ FolderNotes.UITests/      ← FlaUI smoke tests
│  └─ FolderNotes.ShellExt.Tests/
├─ eng/build-shellext.cmd
└─ FolderNotes.slnx
```

## Process & concurrency model

- **Single writer:** only `FolderNotes.exe` writes to `notes.db`. All writes go through
  `INoteRepository` on a dedicated serialized writer: one long-lived connection drained by one
  task from an unbounded `Channel` (ADR-011). Callers `await` their write. Reads use pooled
  read-only connections and never wait for the queue.
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

Resolved in T2.1:

- **No args, first launch:** the tray icon appears **and** the main window opens (someone who
  starts the app from the Start menu expects a window). Only `--background` starts with no window.
  No args when already running: bring the main window forward.
- **Parsing** (`ActivationParser`, pure, unit-tested): the command line is split with Windows
  rules (`CommandLineToArgvW`). The first token is dropped when it is the executable path (the
  forwarded `Arguments` string of an unpackaged app includes it). Switches are case-insensitive
  and also accept a `/` prefix (`/edit`). The first token must be a switch, and only that one
  counts: tokens after its value are ignored.
  - `--edit` needs a non-empty path; a missing path is `Invalid`. A path ending in `"` (after
    trimming trailing spaces) gets that quote replaced by `\`: Explorer's `"%V"` for a drive root
    is `"D:\"`, which Windows splits as `D:"`, and the forwarded string adds a trailing space.
    Windows paths can't contain `"` or end in a space, so the repair is always safe.
  - `--search` takes every remaining token, joined by single spaces, as the query (may be empty).
  - `--open-note` needs a GUID (any `Guid.TryParse` form); otherwise `Invalid`.
  - Unknown switches and stray tokens give `Invalid` with a reason. The router logs a warning
    and treats `Invalid` as *no args* (show the main window), so a bad shortcut never looks like
    a dead app.
- **Redirection:** a second process calls `AppInstance.RedirectActivationToAsync` and exits. The
  main instance gets `AppInstance.Activated` on a background thread, marshals it to the UI
  thread, and runs it through the same parser and `CommandRouter` as its own startup args.
- **Until the target screen exists**, a command opens the main window (T2.6 adds the search
  palette) and the command is logged.
- **T2.3:** `--edit` and `--open-note` open the compact editor window (05 § Editor behaviour),
  one per note/folder. `--open-note` moves to the main window's detail pane in T2.4.

## App shell (T2.1)

- **Entry point:** a custom `Program.Main` (`DISABLE_XAML_GENERATED_MAIN`) does the
  single-instance check *before* any XAML is created, so a redirected second launch costs
  almost nothing.
- **Lifetime:** `DispatcherShutdownMode.OnExplicitShutdown`. Closing the main window **hides** it
  (since T2.4; it was destroyed before): the detail editor is flushed first, and the same window
  is shown again next time, because destroyed WinUI windows leak native memory (ADR-015). The
  process stays in the tray. Only **Quit** in the tray menu ends the process: it removes the tray icon, flushes
  the log and disposes the store (which drains the writer queue).
- **Tray icon:** `Shell_NotifyIcon` on a hidden top-level Win32 window owned by the UI thread
  (ADR-012). Left-click → search; right-click → the menu in 05 § Tray. "Panel on/off" is shown
  disabled until Phase 3. The icon is re-added when `TaskbarCreated` arrives (Explorer restart).
- **Composition:** `Microsoft.Extensions.DependencyInjection`. Singletons: `ILogger` (Serilog),
  `IFolderIdentityService`, `SqliteNoteStore` (as `INoteRepository` and `ILabelRepository`),
  `ISearchService`, `IUiDispatcher`, `UiNoteEvents`, `CommandRouter`, `TrayIcon`. The store is
  opened once at startup at `%LOCALAPPDATA%\FolderNotes\notes.db`.
- **UI-thread marshalling (O-11, ADR-011):** UI code never subscribes to the store's events
  directly. `UiNoteEvents` subscribes once and re-raises `NoteChanged` / `LabelsChanged` through
  `IUiDispatcher` (a wrapper over `DispatcherQueue.TryEnqueue`), so handlers always run on the
  UI thread and never delay the writer task.
- **Logging:** Serilog file sink at `%LOCALAPPDATA%\FolderNotes\logs\app-<date>.log`, rolling
  daily, 7 files kept, minimum level Information (Debug in Debug builds). Unhandled exceptions
  (XAML `UnhandledException`, `AppDomain`, `TaskScheduler.UnobservedTaskException`) are logged
  as Fatal/Error before anything else happens.

## Editor (T2.3)

- **`NoteEditorViewModel`** (CommunityToolkit.Mvvm `ObservableObject`, no XAML types) owns one
  editing session: load, draft, validation, debounced autosave, flush, delete/undo, label
  suggestions. Dependencies: `INoteRepository`, `ILabelRepository`, `IFolderIdentityService`,
  `TimeProvider` (debounce and undo timers, so tests use `FakeTimeProvider`), `IUiDispatcher`
  (timer callbacks are marshalled to the UI thread), and `ILogger`. Created per window by a factory
  registered in DI. Tests: `FolderNotes.App.Tests/Editor`.
- **`EditorWindow`** hosts `NoteEditorView`: 560×640 logical px, Mica, registered with
  `ThemeService`. It's centred on the foreground Explorer window (`CabinetWClass`) if there is
  one, otherwise on the monitor under the cursor, and kept inside that monitor's work area.
  `AppShell` keeps the open editors keyed by note ID and folder `PathNorm`.
- **Editor windows are reused, not destroyed (ADR-015).** Closing an editor flushes it, hides the
  window and disposes its view model; `AppShell` keeps **one** hidden editor window and attaches
  the next session to it. Extra windows (several editors open at once) are destroyed when closed.
  Quit flushes every editor and then destroys all of them.
- **Idle trim.** When the last window has closed, `IdleTrim` runs one aggressive, compacting GC
  after 3 s, so the managed wrappers of a destroyed window (and the native XAML objects they hold)
  are released even though an idle tray process never triggers a GC on its own.

## Library (T2.4)

- **`LibraryViewModel`** (no XAML types) owns the All notes list: the query (sort, label and
  status filters), the rows, the view mode, the selection and the empty state. Dependencies:
  `INoteRepository`, `ILabelRepository`, `INoteEvents` (the UI-thread events of `UiNoteEvents`,
  behind an interface so tests can raise them), `TimeProvider` and `ILogger`. It lives as long as
  the main window, so its state survives hiding the window.
- **`LibraryRow`** (`ObservableObject`) wraps one `Note` and exposes the display strings from
  `LibraryFormat` (path trimming, next date, relative time; pure functions, unit-tested). An edit
  replaces the row's note in place (ADR-017).
- **Reloads** are coalesced: at most one `ListAsync` runs at a time, and any number of requests
  during it cause exactly one more.
- **`LibraryPage`** (XAML) shows the rows as a `ListView` or a `GridView`, the command bar, the
  filter chips, and the detail pane, which hosts a `NoteEditorView`. `MainWindow` hosts the
  `NavigationView` and the pages. The detail editor's view model comes from the same factory as
  the compact editor's.
- `AppShell.OpenNote(id)` shows the main window and calls `LibraryViewModel.SelectAsync(id)`.

## Labels page (T2.5)

- **`LabelsViewModel`** (no XAML types): the label rows (`LabelItem`: label, usage, edit state,
  error), the new-label form, and create / rename / recolour / delete through `ILabelRepository`.
  Dependencies: `ILabelRepository`, `INoteEvents`, `ILogger`. Reloads are coalesced like the
  library's. It lives as long as the main window.
- Usage comes from `ILabelRepository.CountNotesByLabelAsync()`: one `GROUP BY` over
  `note_labels` joined to live notes.
- **Open editors follow label changes (ADR-018):** `NoteEditorViewModel.RefreshLabelsAsync()`
  reloads the label list, replaces each label on the note with its current version and drops the
  ones that no longer exist, without marking the note dirty. `LibraryViewModel` calls it for its
  detail editor and `AppShell` for every compact editor, on `INoteEvents.LabelsChanged`.
- "Show notes" calls `LibraryViewModel.ShowOnlyLabelAsync(name)` and switches the main window to
  All notes.

## Markdown (REQ-007)

`MarkdownModel.Parse(string)` (Markdig 1.4, task lists + auto-links, HTML not rendered) turns the
body into a small UI-free model: blocks (heading 1–6, paragraph, list item with depth, marker and
an optional task check, code block, quote, rule) holding spans (text with bold/italic/code/strike
flags, links). `MarkdownView` renders the model into a `RichTextBlock`. Links open only for
`http`, `https` and `mailto`; anything else renders as plain text. Raw HTML is shown as text. The
model is unit-tested; the renderer is checked by eye. The companion panel (T3.4) reuses it.

## Key interfaces (FolderNotes.Core)

```csharp
public interface INoteRepository {
    Task<Note?> GetByFolderAsync(FolderRef folder, CancellationToken ct = default);
    Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Note> UpsertAsync(Note note, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);   // soft-delete for undo
    Task RestoreAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken ct = default);
    Task<int> PurgeDeletedAsync(TimeSpan olderThan, CancellationToken ct = default);
    Task<Note> RelinkAsync(Guid id, FolderRef newFolder, CancellationToken ct = default); // REQ-061
    Task SetOrphanedAsync(Guid id, bool orphaned, CancellationToken ct = default);
    event EventHandler<NoteChangedEventArgs> NoteChanged;  // raised after commit, on the writer thread
}
// GetByFolderAsync runs the ADR-009 lookup (03 § Lookup algorithm) and does the re-binding itself.

public interface ILabelRepository {                  // REQ-010, 012, 013
    Task<IReadOnlyList<Label>> ListLabelsAsync(CancellationToken ct = default);
    Task<Label> CreateLabelAsync(string name, string color, CancellationToken ct = default);
    Task<Label> UpdateLabelAsync(Label label, CancellationToken ct = default); // rename/recolor
    Task DeleteLabelAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, int>> CountNotesByLabelAsync(CancellationToken ct = default); // live notes per label (T2.5)
    event EventHandler LabelsChanged;
}
// Both are implemented by SqliteNoteStore (FolderNotes.Core.Storage). OrphanScanner runs the
// 03 § Orphan detection pass over the repository and IFolderIdentityService.

public interface ISearchService {
    Task<IReadOnlyList<SearchHit>> SearchAsync(string rawQuery, int limit, CancellationToken ct);
}

public interface IFolderIdentityService {
    FolderRef Resolve(string path);                 // path → canonical + normalized path + volume/file id
    bool TryResolve(string path, out FolderRef folder); // false for invalid paths or files
    LocateResult Locate(FolderRef storedRef);       // find the current path for a stored ref
}
// LocateResult(Status, Path): Found (path, maybe moved) · InRecycleBin (path under $Recycle.Bin)
// · NotFound. A ref without a file ID (network/removable, REQ-062) is "Found" only if its path exists.

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
