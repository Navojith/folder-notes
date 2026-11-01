# 07 — Implementation Plan

Work top to bottom. Tick `[x]` only when "Done when" is met. Each phase has exit criteria, and the
next phase doesn't start until they're met. Requirement IDs refer to `01-requirements.md`.

Legend: 🔬 spike · 🧱 build · ✅ verify

---

## Phase 0: De-risk Explorer integration (spikes)

Goal: prove the three risky mechanisms work before any production code exists.
Output: findings in `04-explorer-integration.md` + ADRs in `08-decisions.md`.

- [x] **S1 🔬 Active folder + tabs tracking** (REQ-031, REQ-034): *done 2026-10-31 on Win11 23H2;
  39/39 passed, tab switch ≤117 ms. Win10 isn't available on this machine, so it moves to the
  T3.5 manual matrix. See 04 § S1 findings, ADR-004/007.*
  C# console app in `spikes/S1-tracker/`: print the active Explorer folder on every change
  (window switch, navigation, tab switch, new and closed tab, virtual folders).
  *Done when:* correct on Win10 + Win11 24H2 with tabs, and measured latency is ≤150 ms. The
  active-tab detection method is documented.
- [x] **S2 🔬 Docked WinUI 3 tool window** (REQ-030, 033, 038): *done 2026-10-31; 13/13 passed,
  0 px error, 14 ms median. Interactive-drag feel, mixed DPI and Win10 are still to be checked
  by hand (T3.5). See 04 § S2 findings, ADR-008.*
  `spikes/S2-dock/`: a WinUI 3 window owned by an Explorer HWND that follows move, resize,
  maximize, snap and minimize without stealing focus, on mixed-DPI monitors.
  *Done when:* no visible lag or gap, no focus theft, and it isn't in Alt+Tab. If WinUI 3 can't
  do it cleanly, record the alternative (e.g. a Win32 host with XAML Islands) in an ADR.
- [x] **S3 🔬 Folder infotip handler** (REQ-040, 041): *done 2026-10-31. It works on Win11 in
  Details, List and the icon views, and replaces the default tip. Explorer needs a restart after
  registering. Open items (E_FAIL fallback, tip caching) moved into T4.3. Tiles/Content, nav pane
  and Desktop are in the T4.5 manual check. See 04 § S3 findings, ADR-006.*
  `spikes/S3-infotip/`: a minimal C++ `IQueryInfo` handler registered in HKCU for `Directory`,
  returning static text.
  *Done when:* it's documented in which views (Details, Icons, Tiles, nav pane, desktop) the tip
  appears on Win10/Win11, how it merges with the default tip, and whether emoji render. If it
  doesn't work, ADR-006 picks the fallback.
- [x] **S4 🔬 Folder identity** (REQ-060): *done 2026-10-31; 14/14 passed on NTFS. Found that
  recreated folders get new IDs, so the lookup was revised (ADR-009). exFAT, ReFS, network and
  OneDrive are untested.*
  Confirm that `FILE_ID_INFO` stays stable across rename and same-volume move on NTFS, and what
  happens with OneDrive-synced folders, ReFS, exFAT and network shares. Confirm that
  `OpenFileById` relinks.
  *Done when:* a findings table is added to `03-data-model.md`.

**Exit criteria:** S1–S4 are done, ADRs 004/006 are updated from "proposed" to "accepted" (or
superseded), and specs 03/04 reflect what was learned.

---

## Phase 1: Core (no UI)

- [x] **T1.1 🧱 Solution scaffold.** *Done 2026-10-31: `FolderNotes.slnx` (Core, App with XAML,
  Core.Tests), ShellExt as a CMake stub, central package versions, `global.json` (SDK 10.0.403 +
  Microsoft Testing Platform), `.editorconfig`, README, `.github/workflows/ci.yml`. Build and
  tests are green locally. CI isn't verified yet because there's no GitHub remote.
  App.Tests is deferred to T2.1 (nothing to test yet).*
  Original scope: `FolderNotes.sln` with Core, App, ShellExt (empty), and test
  projects. `Directory.Build.props` (nullable, warnings as errors), `.editorconfig`, and a
  `README.md` with build steps. *Done when:* `dotnet build` and `dotnet test` pass in CI (GitHub Actions, `windows-latest`).
- [x] **T1.2 🧱 Domain model + validation** (REQ-001, 002, 010, 020). *Done 2026-10-31: the
  rules got stable codes in 01 § Validation rules; lengths count Unicode characters, matching
  SQLite `length()`.* `Note`, `Label`, `NoteStatus`, `FolderRef`, validation rules. *Done when:* unit tests cover every rule in REQ-002.
- [x] **T1.3 🧱 Path normalization + shared vectors** (REQ-063). *Done 2026-10-31: 57 vectors
  in `tests/shared/path-normalization-vectors.json`, all passing. The rules are in 03 § String
  normalization rules. Mapped drives are injected from the vector file.* *Done when:* there are 40 or more
  vectors (drive letters, UNC, mapped drives, `\\?\`, trailing slash, casing, long paths, Unicode)
  and all of them pass.
- [x] **T1.4 🧱 FolderIdentity service** (REQ-060, 061, 062), from S4's findings. *Done
  2026-10-31: 18 integration tests on real NTFS temp folders (rename, move, ancestor rename,
  delete, Recycle Bin, recreate, copy, spellings, 8.3). IDs are stored only on NTFS/ReFS.
  `Locate` returns Found / InRecycleBin / NotFound (spec 02). The ID-first lookup (ADR-009)
  needs the repository, so it's in T1.5.* *Done when:*
  integration tests for rename, move, delete → orphan, and relink pass on real temp folders.
- [x] **T1.5 🧱 SQLite schema + migrations + repository** (REQ-001…006, 010…012). *Done
  2026-10-31: `SqliteNoteStore` (notes + labels), ADR-009 lookup with re-binding, `OrphanScanner`,
  and migration backup. 47 storage tests pass, including 3 writers + 4 readers concurrently and an
  external read-only (tooltip-style) reader. The FTS table stores its own content (ADR-010).
  Writer model: ADR-011.*
  Single-writer queue, soft delete/restore, `NoteChanged` event, backup before migration.
  *Done when:* repository tests pass, including concurrent reader + writer.
- [x] **T1.6 🧱 Search** (REQ-051, 052, 023). *Done 2026-10-31: the grammar is in 03 § Search
  query grammar, with 50+ parser tests. The 10k-note benchmark (xunit `Category=Performance`, Debug
  build) has a worst p95 of 54 ms (`due:30d`, typo fallback 30 ms). BenchmarkDotNet is deferred to
  T5.5. Fixing it found a full-scan conflict check in upsert, now index-backed.* FTS5 index maintenance, query parser for filters,
  ranking, and the typo fallback. *Done when:* parser tests pass and the benchmark is <100 ms at 10k notes.
- [x] **T1.7 🧱 Export / import JSON** (REQ-070). *Done 2026-10-31: `NotePortability`, with the
  format and import rules in 03 § Export format. The lossless round-trip test passes, and so does
  export → import → export (byte-identical). Import is one transaction, with a savepoint per note.*
  *Done when:* a round-trip test is lossless.

**Exit:** Core coverage ≥85% and all Phase 1 tests are green. *Met 2026-10-31: 362/362 green
(Debug + Release), Core line coverage 93.0% (branch 86.6%).*

---

## Phase 2: App shell + editor + search (usable without Explorer integration)

- [x] **T2.1 🧱 WinUI 3 app skeleton.** Single instance, activation-args router (`--edit`,
  `--search`, `--open-note`, `--background`), tray icon, DI container, Serilog. (REQ-080)
  *Done 2026-10-31: custom `Program.Main` + `AppInstance` redirection, `ActivationParser` /
  `CommandRouter`, `TrayIcon` on our own window (ADR-012), MS DI + Serilog, `UiNoteEvents`
  (closes O-11). App.Tests added (47 tests). Verified live: a second launch forwards and exits in
  ~120 ms, tray click → search, menu → Quit, idle private working set 31.7 MB tray-only and
  54.2 MB after the window was used (REQ-080 <60 MB). Commands open the main window until
  T2.3/T2.6 add their screens.*
  *Done when:* parser, router and UI-marshalling tests pass; a second launch with `--edit`
  reaches the running instance; the tray menu works; idle memory is under the REQ-080 target.
- [x] **T2.2 🧱 Theme + design tokens.** Label palette resources, status icons, Mica, light/dark,
  high contrast. (REQ-082, REQ-083 contrast) See 05 § Design tokens.
  *Done 2026-10-31: `Themes/Tokens.xaml` (12 × 3 tokens × Light/Dark/HC), `ThemeTokens`,
  `StatusVisuals`, `ThemeService` (ADR-013), `LabelChip` / `StatusPill`, and a token sample on the
  placeholder main window. 84 new App tests: contrast, HC system colours only, a hex-colour lint,
  status mapping and the theme service. Checked live: Light/Dark/System switch on a running window
  (chips and title bar follow) and UIA names "Label: …" / "Status: …". The HC screenshot isn't
  done: switching to HC changes the whole machine's theme, so it moves to the T2.9 manual pass
  (O-14). New icon from `eng/make-icon.ps1`.*
  *Done when:* `Tokens.xaml` defines all 12 palette keys × 3 tokens in Light, Dark and
  HighContrast; tests verify every chip text ≥ 4.5:1 and every accent ≥ 3:1 (05 rules), that the
  HC dictionary uses only system colours, and that no other app XAML/C# contains hex colours;
  `StatusVisuals` covers every status; `LabelChip` / `StatusPill` exist with UIA names;
  `ThemeService` switches a live window between System/Light/Dark; screenshots of the token
  sample in light, dark and HC look right; the placeholder icon is replaced.
- [x] **T2.3 🧱 Note editor page + compact editor window** (REQ-002…007, 011, 020). Autosave
  indicator and delete with undo. See 05 § Editor behaviour, 02 § Editor / § Markdown.
  *Done when:* `FolderNotes.exe --edit "C:\Temp\x"` (an existing folder) creates and edits a note
  end to end; view-model tests cover draft/autosave/flush/delete-undo/labels with a fake clock;
  killing the app mid-edit loses at most the last 500 ms (REQ-005 AC, checked live).
  *Done 2026-10-31: `NoteEditorViewModel` (ADR-014) + `NoteEditorView` + `EditorWindow` (reused,
  ADR-015), `MarkdownModel`/`MarkdownView` (Markdig 1.4), `WrapPanel`, `ColorSwatch`. 48 new App
  tests (24 view model, 19 Markdown, 5 line endings). Checked live on Release via UI Automation:
  `--edit "C:\Temp\x"` → draft titled "x" → typed → "Saved" and the row in SQLite; inline label
  created (orange, least used); preview renders; killed 50 ms after a title edit and 850 ms after
  a body edit → body kept, title lost (REQ-005 AC); a second `--edit` reuses the open window;
  delete → Undo → delete → window closes itself after 10 s; a reused window shows the next
  folder's fresh session. Found and fixed live: the TextBox's bare CR line breaks (now stored as
  LF), and a native memory leak per destroyed window (ADR-015). REQ-080 after using the editor is
  65 MB, over the 60 MB target: O-15, resolved by ADR-016 (per-state budgets).*
- [x] **T2.4 🧱 Library view** (REQ-054). List/grid, sort, filter, master/detail. See 05 § Library
  behaviour, 02 § Library, ADR-017.
  *Done when:* view-model tests cover load, every sort, label (all-of) and status (any-of)
  filters, clear filters, empty states, in-place edits, removal on delete, reloads on
  create/restore/label change (coalesced) and selection kept by ID; `LibraryFormat` tests cover
  path trimming, next date and relative time; a Core test shows the hierarchical path sort (O-9);
  checked live on Release: the list and cards show seeded notes, sort and filters work, the
  detail pane edits and autosaves, the narrow layout's Back works, `--open-note` selects the
  note, and `eng/perf-check.ps1` (with a main-window stage) passes REQ-080 / NFR-06. Also: a
  missing folder's `--edit` reuses its window (O-17).
  *Done 2026-11-01: `LibraryViewModel` / `LibraryRow` / `LibraryFormat` (ADR-017), `LibraryPage`
  (list + cards, sort and filter menus, filter chips, empty states, master/detail with the shared
  `NoteEditorView`), `AccentBar`, NavigationView main window (hidden and reused, ADR-015) with a
  Settings stub for the theme, `INoteEvents`, `FolderLauncher`; hierarchical path sort in Core
  (O-9). 43 new tests (42 App: view model and formatting; 1 Core). Checked live on Release via UI
  Automation against seeded notes: all three sorts, label filter + chip removal, detail edit →
  "Saved" and the row updated in place, cards, the narrow layout's Back, `--open-note` clearing a
  hiding filter, reopen keeps state on the same window, and two `--edit` of a missing folder →
  one window (O-17). `perf-check` (8 editor + 8 main-window cycles) passes: tray-only 32.5 MB; editor open ≤ 68.1 MB; main window with the library ≤ 81.8 MB open and ≤ 81.4 MB after close; no growth (-0.8 / -2.8 MB); idle CPU 0 %.*
- [x] **T2.5 🧱 Labels manager** (REQ-012, 013). See 05 § Labels page, 02 § Labels page, ADR-018.
  *Done when:* view-model tests cover the list and usage, create (least-used colour, trimming,
  rule and duplicate errors), rename (case-only, duplicate refused, errors keep edit mode),
  recolour, delete, reload on label and note changes, and Show notes; a Core test covers
  `CountNotesByLabelAsync`; editor tests show a renamed label updated and a deleted one dropped
  without an autosave bringing it back; checked live on Release (create, rename, recolour, delete,
  Show notes, an open detail editor following a rename and a delete) and `eng/perf-check.ps1`
  passes.
  *Done 2026-11-01: `LabelsViewModel` / `LabelItem`, `LabelsPage` (new-label form, inline rename,
  colour flyout, delete confirmation, Show notes), `ColorPickerFlyout`, a Labels item in the
  navigation rail, `NoteEditorViewModel.RefreshLabelsAsync` wired to the library's detail editor
  and every compact editor (ADR-018), `LibraryViewModel.ShowOnlyLabelAsync`;
  `CountNotesByLabelAsync` and `Palette.LeastUsed` in Core. 23 new tests (20 App, 3 Core). Checked
  live on Release via UI Automation against seeded notes: usage counts, create (least-used colour
  preset, then the next one), duplicate refused, rename client → customer, recolour → pink,
  delete do-not-delete after "It will be removed from 2 notes.", a compact editor and the detail
  editor both showing "customer" and dropping the deleted label, the detail editor's next
  autosave not re-creating it, and Show notes → All notes filtered to customer (3 notes). Fixed
  live: a stale "already exists" error stayed after clearing the name. `perf-check` passes: tray-only 33.3 MB; editor open ≤ 69.6 MB; main window ≤ 82.5 MB; growth 1.2 / -2.5 MB; idle CPU 0 %. Visiting the Labels page adds about 4 MB (77.5 → 81.8 MB with only the main window used).*
- [x] **T2.6 🧱 Search palette + global hotkey** (REQ-050…053). See 05 § Search palette
  behaviour, 02 § Search palette, ADR-019.
  *Done when:* view-model tests (fake clock) cover the empty-box recent list, debounce, stale
  results dropped, parse errors, no hits, selection and wrapping, Enter / missing folder,
  Ctrl+Enter, Ctrl+C, and autocomplete accept; `SearchHighlight` and `SearchCompletion` have unit
  tests; checked live on Release: `Ctrl+Alt+N` from another app opens the palette, typing finds
  seeded notes with highlights, `label:` autocompletes, Enter opens Explorer at the folder,
  Ctrl+Enter selects the note in the library, Ctrl+C puts the path on the clipboard, Esc and
  clicking elsewhere close it, `--search acme` prefills; REQ-051's 100 ms holds for a keystroke at
  10k notes (Release); `eng/perf-check.ps1` passes, with a palette stage.
  *Done 2026-11-01: `SearchPaletteViewModel` / `SearchResultRow`, `SearchHighlight`,
  `SearchCompletion`, `SearchPaletteWindow` (Acrylic, no title bar, reused, ADR-019),
  `HighlightedText`, the hotkey on the tray window (`RegisterHotKey`, Ctrl+Alt+N), `--search` and
  the tray opening the palette, `ISearchService.Parse`; Labels and Settings pages now load on
  first visit. 23 new App tests. Checked live on Release with the real hotkey pressed while another
  app was in front: the palette opens in ≈200 ms with the caret in the box, "acme" finds Acme with
  bold matches, `label:cl` suggests `client` and Tab completes it, Ctrl+C copies the path, Enter
  opens Explorer at the folder, Ctrl+Enter selects the note in the library, Esc and clicking
  elsewhere close it, `--search acme label:client` prefills. Found and fixed live: the palette
  stayed behind the foreground app and lost the caret (ADR-019 addendum). REQ-051 in Release at
  10k notes: p95 8.7–38.3 ms over 10 query shapes. Memory: the palette costs ≈10 MB once, putting
  "windows used" at 91–93 MB; the user raised that budget to 100 MB (ADR-016 amendment). `perf-check` (with the new palette stage) passes against 100 MB: tray-only 33.1 MB; editor ≤ 69.8 MB; main window ≤ 83.3 MB; palette ≤ 94.2 MB open / 93.3 MB closed (private bytes ≈ 115 MB); growth 0.2 / -2.4 / 3.3 MB; idle CPU 0 %.*
- [x] **T2.7 🧱 Orphaned view + relink** (REQ-060, 061). See 03 § Orphan detection and § Relink
  rules, 05 § Orphaned page, 02 § Orphaned page and orphan scan, ADR-020.
  *Done when:* Core tests on real temp folders and with a fake identity cover: missing (deleted)
  folders, moved and renamed folders, folders recreated at the same path, the Recycle Bin, an
  unmounted volume, a different volume at the same path, duplicate folder names (a same-named
  folder elsewhere never takes a note), conflicts with another note (neither note changes),
  concurrent binding changes (skipped), relink refusals (duplicate, deleted note, file, Recycle
  Bin, changed binding) leaving the note unchanged, an autosave after a relink keeping the new
  folder, and note content identical after relink and undo; view-model tests cover the list and
  reasons, the per-row check on activation, Check again, the relink flow (cancel, refusals,
  different-name confirm, success, Undo, failed Undo), delete with Undo, the badge count, and open
  editors following a relink; checked live on Release (delete, move and Recycle Bin a noted folder;
  the page shows the reasons; relink through the real picker; Undo; restore from the Recycle Bin
  reattaches) and `eng/perf-check.ps1` passes.
  *Done 2026-11-01: Core: `OrphanScanner` rewritten as `Classify` (file system only) + `CheckAsync`
  (conditional writes) with `FolderState` / `OrphanOutcome`; `RelinkTarget`; `LocateStatus.VolumeNotMounted`;
  `RelinkAsync` / `SetOrphanedAsync` take `expectedFolder` (`NoteBindingChangedException`); Upsert keeps
  an existing note's binding and orphan state (import excepted); the lookup's re-bind needs the same
  volume serial and is conditional (ADR-020). App: `OrphansViewModel` / `OrphanItem`, `OrphansPage`
  (reasons, Check again, Relink… with the WinAppSDK folder picker, different-name ContentDialog,
  Undo bar, Delete with Undo, Open note), the Orphaned nav item with a count badge,
  `OrphanScanService` (10 s after start, every 6 h, on demand, single-flight), and
  `NoteEditorViewModel.RefreshFolderAsync` wired to the library's detail editor and the compact
  editors. 53 new tests (26 Core, 27 App); 683/683 green in Release. Checked live on Release
  against seeded notes under `C:\Temp\FolderNotesE2E\T27`: the startup scan (5 notes, 21 ms) followed a
  moved folder and orphaned a deleted one, one in the Recycle Bin and an old dev note; the page
  showed the right reasons and badge; relink through the real picker (commit button "Relink"),
  Undo within 10 s, the different-name question (Cancel, then Relink), a refused relink onto a
  folder with a note, Delete + Undo, restore from the Recycle Bin + Check again ("1 reattached"),
  and the library's detail editor following a relink. Every note kept its body, labels, status
  and `updated_at`. **Found live and fixed:** opening Labels or Settings crashed the app since
  T2.6 (`x:Load` can't realize elements declared in a Window; ADR-021); pages are now created in
  code. `perf-check` passes: tray-only 34 MB; editor ≤ 68.4 MB; main window ≤ 77 MB; palette
  ≤ 78.8 MB; growth -2.5 / -1.6 / +0.5 MB; idle CPU 0 / 0.052 %. The Orphaned page adds ≈2.7 MB once
  visited (70.5 → 73.2 MB; 77.4 MB with Labels visited too).*
- [x] **T2.8 🧱 Settings page** (REQ-080 start with Windows, REQ-081) and start with Windows (Run key,
  or a StartupTask when packaged). See 05 § Settings page, 02 § Settings, 03 § Settings, ADR-022.
  *Done when:* Core tests cover the settings store (round trip, overwrite, remove, survives reopen);
  App tests cover `AppSettings` (defaults, round trip, invalid values give defaults), `HotkeyGesture`
  (parse, format, key-press rules), `SettingsViewModel` (theme saved and applied, shortcut capture,
  change success, refused shortcut keeps the old one, reset, startup-failure warning, startup toggle,
  save failures revert), the library restoring its saved sort/view/filters (dropping deleted labels),
  and `RunKeyStartupRegistration` against a scratch registry key; checked live on Release: theme and
  library state survive a restart, a new shortcut opens the palette and the old one doesn't, a
  shortcut held by another process is refused, the startup toggle adds and removes the Run entry
  (the machine's original state restored afterwards), tray *Settings* and *Manage labels* navigate;
  and `eng/perf-check.ps1` passes.
  *Done 2026-11-01: `ISettingsStore` on `SqliteNoteStore`; App: `AppSettings` (defaults, tolerant
  parsing, writes only what changed), `HotkeyGesture`, `IHotkeyHost` on `TrayIcon` (`TryChange` puts the
  previous shortcut back), `RunKeyStartupRegistration` (+ path repair at startup), `SettingsViewModel`
  and `SettingsPage` (Appearance, Search shortcut capture/Reset/warning, Startup toggle, Labels link,
  About with the data folder) replacing the stub; the theme loads before any window; the library
  restores and saves sort/view/filters (`LibraryState`); tray *Settings* opens the page. 35 new tests
  (4 Core, 31 App); 718/718 green in Release. Checked live on Release: Dark theme, cards view and
  Ctrl+Shift+F captured with real key presses survive a tray Quit + restart; the new shortcut opens
  search and Ctrl+Alt+N doesn't; Reset refused while another process held Ctrl+Alt+N (current one
  kept working); restarting while another process held Ctrl+Shift+F showed the warning, and Reset
  then registered Ctrl+Alt+N and cleared it; the startup toggle wrote `"<exe>" --background` to the
  Run key and removed it (no entry before or after); tray *Settings* and *Manage labels* navigate.
  Found while testing: a shortcut another app has *registered* never reaches the capture box (05
  note). `perf-check` passes: tray-only 33.9 MB; editor ≤ 68.4 MB; main ≤ 76.9 MB; palette ≤ 78.5 MB;
  growth -0.6 / -0.4 / -0.1 MB; idle CPU 0 %. The Settings page adds ≈3.4 MB once visited.*
- [ ] **T2.9 ✅ Accessibility pass** (REQ-083) with Accessibility Insights. No critical issues.

**Exit:** a user can add, find, edit and delete notes through the app and `--edit`, and FlaUI
smoke tests pass.

---

## Phase 3: Explorer: context menu + companion panel (MVP complete)

- [x] **T3.1 🧱 Tier-1 context menu verbs** (REQ-003, 004, ADR-024). Registered by the installer
  (`--register-shell`) and removed on uninstall (`--unregister-shell`); the user can turn them on or off,
  and repair a stale registration, in Settings. **Never automatic** (not on first run, not in dev, test or
  `perf-check` builds). See 04 § 4.1, 02 § Explorer context-menu verbs.
  *Done when:* `RegistryShellVerbRegistrar` tests on a scratch registry root cover Off/On/Stale (other exe,
  one key missing, command missing), enable, disable, repair, idempotence, paths with spaces and non-ASCII, and
  that nothing outside our keys is touched; a test proves `App.xaml.cs`/`AppShell.cs` never enable the verbs;
  the CLI switches work; the Settings card shows the real state; a live check on the real HKCU registers, right-click
  on a folder runs `--edit "<folder>"` through the verb, and unregisters, leaving the registry as found.
- [ ] **T3.2 🧱 ExplorerTracker service** (REQ-031, 034, NFR-01, NFR-06), production version of S1.
  Design: 04 § ExplorerTracker design, ADR-025. Not wired to the UI until T3.4. Done in five steps, in order;
  T3.2 is ticked when all five are:
  - [x] **T3.2.1 🔬 Probes** *(done 2026-11-01: P1, P2, P3, P5 passed or measured, P4 deferred by D5; results in 04 § H, ADR-025 updated, no spec failure that needs CsWin32)* (spike `spikes/S5-tracker-probes`, never shipped; findings go into 04 and ADR-025):
    **P1** every interface and method signature in 04 B.1 is validated (vtable order vs SDK headers and a live call
    per method, `VARIANT` index, `HRESULT` handling, `QueryInterface`/casting, RCW lifetime and release); on
    unreliability switch to CsWin32, no workarounds (decision D1); **P2** `CoCancelCall` returns a blocked
    proxy call within 50 ms of the request, against a scratch out-of-process COM server; **P3** path-read parity
    (04 § E list) and cost; **P5** idle CPU with global vs process-scoped `NAMECHANGE` hooks while a browser and a
    terminal update their titles. *(P4, the `rundll32` private-server behaviour, needs a disposable session: see
    T3.2.5 and D5.)* *Done when:* each probe has a measured result in 04 and ADR-025 is updated; if P1 or P2 fail,
    the spec is amended (CsWin32; abandon-only time-boxing) before T3.2.2 starts.
  - [ ] **T3.2.2 🧱 Pure logic** (`TabMap`, `EvaluationScheduler`, `ComFailureClassifier`, `TrackerStateMachine`,
    `ActiveFolderState`, `FakeShellWindowsGateway`) and their unit tests (06 § ExplorerTracker tests, *Logic*).
    *Done when:* all logic tests pass with `FakeTimeProvider`, no Win32/COM referenced from these types.
  - [ ] **T3.2.3 🧱 COM layer:** typed interfaces, `ComRef<T>`, `ComShellWindowsGateway`, the STA host with
    message loop, watchdog (cancel → abandon → circuit breaker). *Done when:* the COM, stale/disconnected and
    wedge tests pass; the `dynamic`/`InvokeMember` source guard passes; live-RCW count is 0 after tests.
  - [ ] **T3.2.4 🧱 `ExplorerTracker` composition:** scoped WinEvent hooks, scheduler wiring, poll safety net,
    recovery, latest-wins publishing through `IUiDispatcher`, `NotifyShellRestarted` from the tray window,
    `TrackerStats`, DI registration (not started). *Done when:* the logic tests plus fake-gateway recovery tests
    pass; `AppShell`/`App.xaml.cs` do not start it (guard test).
  - [ ] **T3.2.5 ✅ Real-Explorer verification:** the integration and reliability tests, the idle-CPU
    measurement, P4 and the gated restart test where a disposable session exists (D5). *Done when:* 06 §
    ExplorerTracker tests pass on this machine (Windows 11 23H2); numbers (latency, evaluation cost, idle CPU,
    leak deltas) are recorded in 09; every "tunable" value in 04 is either confirmed or changed in the spec; the
    REQ-031 evaluation cost is < 10 ms (closes the ❌ in 09). Restart recovery and Windows 10 are recorded as
    verified-by-fault-injection only if they could not be run (O-6, O-23).
- [ ] **T3.3 🧱 PanelDocker** (REQ-030, 033, 038), production version of S2: position modes,
  DPI, snap and maximize handling.
- [ ] **T3.4 🧱 Companion panel UI** (REQ-032, 035, 036, 037): states, inline edit, pin,
  collapse tab, inherited context.
- [ ] **T3.5 ✅ Panel latency + stability tests** (REQ-031, NFR-02, NFR-06): automated Explorer navigation
  tests and the manual checklist on 3 OS builds. Add the panel-active and Explorer-navigation
  stages to `eng/perf-check.ps1` (06 § Resource budgets), then **re-baseline the REQ-080 budgets**
  with the panel and Acrylic in place (O-16, ADR-016).

**Exit (= MVP):** right-click → add note → the panel shows it when the folder is opened, and
search finds it.

---

## Phase 4: Hover tooltip + Windows 11 top-level menu

- [ ] **T4.1 🧱 ShellExt DLL scaffold.** C++20, static CRT, sqlite3 amalgamation, COM
  registration through the app (HKCU), versioned install folder.
- [ ] **T4.2 🧱 C++ path normalizer** passing the shared vectors (REQ-063).
- [ ] **T4.3 🧱 IQueryInfo infotip handler** (REQ-040…042), with the time budget and fail-open
  behaviour. **First, verify** that returning `E_FAIL` restores Explorer's default tooltip, and
  whether an edited note shows on the next hover (if not, the app sends `SHChangeNotify(SHCNE_UPDATEITEM)`).
- [ ] **T4.4 🧱 Settings toggle** registers and unregisters the handler (REQ-043). Explorer
  only picks up the change after a restart (S3), so offer "Restart Explorer now" or "applies at
  next sign-in".
- [ ] **T4.5 ✅ Explorer stress test + Application Verifier** (NFR-01).
- [ ] **T4.6 🧱 (4b) IExplorerCommand + sparse package** for the Windows 11 top-level menu (ADR-005).

**Exit:** the tooltip works in the views confirmed by S3, the stress test passes, and there are
zero Explorer crashes.

---

## Phase 5: Reminders, portability, polish

- [ ] **T5.1 🧱 ReminderService + toasts** (REQ-021, 022, 024): scheduling, snooze, once-only,
  and the main window's *Due & reminders* page (overdue / next 30 days / later, 05 § Main window).
- [ ] **T5.2 🧱 Sidecar sync** (REQ-071, 072): write, read, conflict dialog.
- [ ] **T5.3 🧱 Daily DB backup** (REQ-084).
- [ ] **T5.4 🧱 Markdown preview polish + checkboxes toggle from the panel** (REQ-007).
- [ ] **T5.6 🧱 Import preview + apply + snapshot, Core** (REQ-070, O-21, ADR-023; after T5.3).
  Spec first: 03 § Import with preview and snapshot, 01 AC. `PreviewImportAsync` (read-only),
  `ApplyImportAsync` with per-conflict and per-label-colour resolutions, binding-change flag,
  `ISnapshotService` (verified), the new `ImportReport`; the old `ImportAsync(policy)` becomes a wrapper.
  *Done when:* tests prove the preview leaves the database file byte-identical; each choice (keep mine /
  take imported / keep both) does what 03 says; label colour choices work; a binding change is flagged; Apply
  without a choice refuses; an injected failure mid-apply leaves the database unchanged; a failed snapshot stops
  Apply; the snapshot opens and restores per the steps in 03; the v1 lossless and byte-identical round-trip
  tests are unchanged and green.
- [ ] **T5.7 🧱 Backup UI: export, import preview, summary** (REQ-070, O-21; after T5.6). A Settings
  card *Backup*: Export all…, Import…, Open backups folder; the import preview with per-conflict choices,
  label colour choices, binding-change notes, Apply, and the summary with the snapshot path and the restore
  steps. Spec first: write 05 § Backup at the start of this task.
  *Done when:* view-model tests cover the choices and Apply gating; a live check exports, edits, imports with
  conflicts, and restores the snapshot by following the steps.
- [ ] **T5.5 ✅ Performance pass** at 10k notes (NFR-02, NFR-05), plus the 120-cycle soak of
  `eng/perf-check.ps1` (REQ-080, NFR-06).

---

## Phase 6: Ship

- [ ] **T6.1 🧱 Packaging:** MSIX (signed) + per-user installer (e.g. Velopack or Inno Setup),
  clean uninstall (registry, shell ext, startup entry; data kept unless the user ticks "remove my notes").
  The installer turns *Start with Windows* on by default (REQ-080, ADR-022).
- [ ] **T6.2 🧱 Auto-update** (handles the locked ShellExt DLL through versioned folders).
- [ ] **T6.3 ✅ Release checklist:** manual matrix, privacy review (no network, NFR-03), README and screenshots.

---

## Later (C priority / v2)

REQ-008 templates · REQ-073 desktop.ini mirror · notes on files · overlay icon for noted folders ·
note history/revisions · encrypted notes · optional sync via a user-chosen folder (OneDrive/Dropbox).
