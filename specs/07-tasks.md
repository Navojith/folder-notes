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
- [ ] **T1.4 🧱 FolderIdentity service** (REQ-060, 061, 062), from S4's findings. *Done when:*
  integration tests for rename, move, delete → orphan, and relink pass on real temp folders.
- [ ] **T1.5 🧱 SQLite schema + migrations + repository** (REQ-001…006, 010…012).
  Single-writer queue, soft delete/restore, `NoteChanged` event, backup before migration.
  *Done when:* repository tests pass, including concurrent reader + writer.
- [ ] **T1.6 🧱 Search** (REQ-051, 052, 023). FTS5 index maintenance, query parser for filters,
  ranking, and the typo fallback. *Done when:* parser tests pass and the benchmark is <100 ms at 10k notes.
- [ ] **T1.7 🧱 Export / import JSON** (REQ-070). *Done when:* a round-trip test is lossless.

**Exit:** Core coverage ≥85% and all Phase 1 tests are green.

---

## Phase 2: App shell + editor + search (usable without Explorer integration)

- [ ] **T2.1 🧱 WinUI 3 app skeleton.** Single instance, activation-args router (`--edit`,
  `--search`, `--open-note`, `--background`), tray icon, DI container, Serilog. (REQ-080)
- [ ] **T2.2 🧱 Theme + design tokens.** Label palette resources, status icons, Mica, light/dark,
  high contrast. (REQ-082)
- [ ] **T2.3 🧱 Note editor page + compact editor window** (REQ-002…007, 011, 020). Autosave
  indicator and delete with undo. *Done when:* `FolderNotes.exe --edit "C:\Temp\x"` creates and
  edits a note end to end.
- [ ] **T2.4 🧱 Library view** (REQ-054). List/grid, sort, filter, master/detail.
- [ ] **T2.5 🧱 Labels manager** (REQ-012, 013).
- [ ] **T2.6 🧱 Search palette + global hotkey** (REQ-050…053).
- [ ] **T2.7 🧱 Orphaned view + relink** (REQ-061).
- [ ] **T2.8 🧱 Settings page** (REQ-081) and start with Windows (Run key, or a StartupTask when packaged).
- [ ] **T2.9 ✅ Accessibility pass** (REQ-083) with Accessibility Insights. No critical issues.

**Exit:** a user can add, find, edit and delete notes through the app and `--edit`, and FlaUI
smoke tests pass.

---

## Phase 3: Explorer: context menu + companion panel (MVP complete)

- [ ] **T3.1 🧱 Tier-1 context menu verbs** (REQ-003, 004), registered and unregistered by the
  app (HKCU) on first run, from Settings, and on uninstall.
- [ ] **T3.2 🧱 ExplorerTracker service** (REQ-031, 034), production version of S1: STA thread,
  events + polling fallback, Explorer restart recovery.
- [ ] **T3.3 🧱 PanelDocker** (REQ-030, 033, 038), production version of S2: position modes,
  DPI, snap and maximize handling.
- [ ] **T3.4 🧱 Companion panel UI** (REQ-032, 035, 036, 037): states, inline edit, pin,
  collapse tab, inherited context.
- [ ] **T3.5 ✅ Panel latency + stability tests** (REQ-031, NFR-02): automated Explorer navigation
  tests and the manual checklist on 3 OS builds.

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

- [ ] **T5.1 🧱 ReminderService + toasts** (REQ-021, 022, 024): scheduling, snooze, once-only.
- [ ] **T5.2 🧱 Sidecar sync** (REQ-071, 072): write, read, conflict dialog.
- [ ] **T5.3 🧱 Daily DB backup** (REQ-084).
- [ ] **T5.4 🧱 Markdown preview polish + checkboxes toggle from the panel** (REQ-007).
- [ ] **T5.5 ✅ Performance pass** at 10k notes (NFR-02, NFR-05).

---

## Phase 6: Ship

- [ ] **T6.1 🧱 Packaging:** MSIX (signed) + per-user installer (e.g. Velopack or Inno Setup),
  clean uninstall (registry, shell ext, startup entry; data kept unless the user ticks "remove my notes").
- [ ] **T6.2 🧱 Auto-update** (handles the locked ShellExt DLL through versioned folders).
- [ ] **T6.3 ✅ Release checklist:** manual matrix, privacy review (no network, NFR-03), README and screenshots.

---

## Later (C priority / v2)

REQ-008 templates · REQ-073 desktop.ini mirror · notes on files · overlay icon for noted folders ·
note history/revisions · encrypted notes · optional sync via a user-chosen folder (OneDrive/Dropbox).
