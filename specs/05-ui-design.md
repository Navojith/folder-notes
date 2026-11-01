# 05 — UI Design

## Visual language

- **Fluent 2 / Windows 11 native:** WinUI 3 default controls, Segoe UI Variable, system accent
  color, Mica on the main window, Acrylic on the panel and search palette, 8 px corner radius,
  4 px spacing grid.
- **Light and dark** follow the system (override in Settings). High-contrast themes are supported
  through theme resources only, with no hard-coded colors.
- **Label palette (12):** red, orange, amber, yellow, lime, green, teal, cyan, blue, indigo,
  purple, pink. Each has light/dark tints chosen for AA contrast on chips.
- **Status iconography** (Fluent System Icons):
  | Status | Icon | Tint |
  |---|---|---|
  | keep | `LockClosed` | blue |
  | safe-to-delete | `Delete` | green |
  | archive | `Archive` | indigo |
  | in-progress | `ArrowSync` | amber |
  | review | `Eye` | purple |
- Motion: subtle connected animations between search → note; panel slides in 150 ms (respects
  "Animation effects" off).

## Design tokens (T2.2)

Defined once in `src/FolderNotes.App/Themes/Tokens.xaml` as `ThemeDictionaries` (`Light`,
`Dark`, `HighContrast`). Every UI surface uses these keys through `{ThemeResource}` (or
`ThemeTokens` in code for keys built at run time). **No hex colours anywhere else** in app XAML
or code; a test enforces this.

**Label palette.** For each palette key `k` (03 / `Palette.Keys`) there are three colour tokens,
each with a matching `…Brush`:

| Token | Used for | Contrast rule (WCAG 2.1, checked by a test) |
|---|---|---|
| `LabelAccent.k` | note accent bar, status icon tint, colour dot | ≥ 3:1 against the layer background (light `#F3F3F3` and `#FFFFFF`; dark `#202020`…`#333333`) |
| `LabelChipBackground.k` | chip / pill fill | — |
| `LabelChipForeground.k` | chip / pill text and icon | ≥ 4.5:1 against `LabelChipBackground.k` |

Values are derived from the Fluent 2 shared colours (red `#D13438`, orange `#F7630C`, amber
`#EAA300`, yellow `#FDE300`, lime `#73AA24`, green `#107C10`, teal `#038387`, cyan `#0099BC`,
blue `#0078D4`, indigo `#5B5FC7`, purple `#8764B8`, pink `#E43BA6`): the light-theme accent is
darkened and the dark-theme accent lightened until it reaches 3.2:1; the chip fill is the base
mixed 82% toward white (light) or 72% toward `#202020` (dark); the chip text is the base darkened
(light) or lightened (dark) until it reaches 5:1.

**High contrast.** In the `HighContrast` dictionary every label token maps to a system colour:
accent → `SystemColorHighlightColor`, chip fill → `SystemColorWindowColor`, chip text →
`SystemColorWindowTextColor`, and chips get a 1 px `SystemColorWindowTextColor` border. The
label's colour is not shown in HC; its name is (05 § Accessibility: colour is never the only signal).
HC on/off is detected with `SystemParametersInfo(SPI_GETHIGHCONTRAST)` and change notifications come
from `UISettings.ColorValuesChanged`. (`AccessibilitySettings.HighContrastChanged` needs a
CoreWindow and throws `0x80070490` in a desktop app, as found in T2.2.) Controls that look tokens
up in code re-render on `ThemeService.ThemeChanged`.

**Status.** `StatusVisuals` maps each status to its icon, tint key and display text (table in
§ Visual language). Icons are `PathIcon`s with the **Fluent System Icons** 20 px *regular*
geometry (MIT) stored as `StatusIcon.<status>` string resources, so they look the same on
Windows 10, which has no Segoe Fluent Icons font. `none` has no icon and no pill.

**Shared controls.** `LabelChip` (name + palette key; UIA name "Label: *name*") and `StatusPill`
(status; UIA name "Status: *text*") render from the tokens and are reused by the editor, library,
panel and search.

**Theme.** `ThemeService` holds the app theme (`System` / `Light` / `Dark`, REQ-081). Every
window registers with it; the service sets `RequestedTheme` on the window's root element and the
title-bar theme (`AppWindow.TitleBar.PreferredTheme`) and re-applies when the setting changes.
`System` follows the Windows app mode live. Persisting the choice is T2.8.

**Backdrops.** Main window and compact editor: `MicaBackdrop`. Panel and search palette:
`DesktopAcrylicBackdrop`. Both fall back to the solid theme background automatically where
unsupported (Windows 10, HC, "Transparency effects" off), so the window root stays transparent
and draws nothing itself.

**Spacing / shape.** `SpacingS` 4, `SpacingM` 8, `SpacingL` 12, `SpacingXL` 16, `SpacingXXL` 24;
`ChipCornerRadius` 4, `CardCornerRadius` 8 (= `OverlayCornerRadius`); `AccentBarWidth` 4.

## Screens

### 1. Companion panel (default 300 px wide, resizable 240–480)

```
┌──────────────────────────────┐
│ 📁 Acme Ltd            ⋯  📌 │  ← folder name, menu, pin toggle
│──────────────────────────────│
│ ▌Client: Acme Ltd            │  ← accent bar + title (editable on click)
│ 🔒 Keep   [client] [do-not-… │  ← status pill + label chips
│ ⏳ Keep until 1 Jan 2027     │  ← date badges (overdue = red tint)
│──────────────────────────────│
│ Contract docs and signed     │
│ SOWs. Ask Sam before         │  ← rendered Markdown, click to edit
│ archiving anything here.     │
│ ☐ Upload 2026 invoices       │
│                              │
│──────────────────────────────│
│ ↳ Inside: "Clients" (D:\…)   │  ← inherited context (REQ-035)
│ Edited 3 days ago      ⤢ Open│  ← open in main window
└──────────────────────────────┘
```
- Empty state: a centered folder glyph, "No note for this folder", a primary [Add note] button,
  and a secondary "Collapse when empty" link.
- Collapsed tab: a 28 px strip with a "+" (or a coloured dot if the folder has a note while the
  panel is collapsed).

### 2. Note editor (main window page, also used as a compact dialog from the context menu)

```
┌────────────────────────────────────────────────────────────┐
│ D:\Clients\Acme Ltd                         [Open folder]  │
│                                                            │
│ Title  [ Client: Acme Ltd                              ]   │
│ Status [ 🔒 Keep ▾ ]   Color ● ● ● ● ● ● ●                 │
│ Labels [ client ✕ ] [ do-not-delete ✕ ] [ + add label… ]   │
│                                                            │
│ Dates  Keep until [ 2027-01-01 📅 ]                        │
│        Review on  [ ——        📅 ]   Safe after [ —— 📅 ]  │
│                                                            │
│ ┌ Write ┬ Preview ┐                                        │
│ │ Contract docs and signed SOWs…                         │ │
│ └────────────────────────────────────────────────────────┘ │
│ ☐ Travel with folder (writes hidden .foldernote.json)      │
│                                       Saved ✓   [Delete]   │
└────────────────────────────────────────────────────────────┘
```
- Opening from the context menu opens a **compact window** (560×640) centered on the Explorer
  window, with the title field focused. `Esc` saves and closes, and `Ctrl+Enter` does the same.
- "Saved ✓" is an autosave indicator (REQ-005). There's no Save button.

#### Editor behaviour (T2.3)

One view (`NoteEditorView`) bound to one `NoteEditorViewModel`; the compact window hosts it now and
the main window's detail pane reuses it in T2.4.

- **Opening.** `--edit <path>`: the path must be an existing folder, otherwise the window shows
  "This folder doesn't exist" and nothing else. The folder is resolved (02 `TryResolve`) and looked up
  with `GetByFolderAsync`; a hit opens that note (REQ-004, and REQ-001: a second "Add" opens the
  existing note). A miss opens a **draft**: `Note.CreateFor(folder)` with the folder name as the
  title (REQ-003), selected so typing replaces it. `--open-note <id>` opens that note in the main window's
  library (since T2.4, see § Library behaviour); an unknown or deleted id shows "This note no longer exists".
  If an editor for the same note or folder is already open, it's activated instead of opening a second one.
  The same goes for a folder that doesn't exist: a second `--edit` with the same path (ignoring
  case) activates the open "This folder doesn't exist" window (O-17).
- **Drafts aren't saved until the first edit.** Closing an untouched draft (close button or `Esc`)
  creates nothing. `Ctrl+Enter` is an explicit save, so it creates the note even if untouched.
- **Autosave (REQ-005).** Every change re-validates (`NoteValidator`). A valid change schedules a
  save 500 ms after the last change (debounced); saves are serialized, and a change during a save
  schedules another one. Invalid state: no save is scheduled, and the field shows the rule's
  message inline (title errors under the title, label errors under the labels box).
- **Indicator** (bottom right, `AutomationId=SaveState`, also a live region): *(empty)* for an
  untouched note · "Saving…" while a save is pending or running · "Saved" · "Not saved: <first error>" ·
  "Couldn't save: <reason>" (store error; the next change retries).
- **Close / flush.** The close button, `Esc` and `Ctrl+Enter` all flush: a pending save runs now. A
  field that is invalid at close is put back to its last saved value (a draft's title goes back to
  the suggested title) and the rest is saved, so only the invalid text is lost. `Esc` and
  `Ctrl+Enter` then close the window.
- **Delete (REQ-006).** [Delete] asks for confirmation (ContentDialog: "Delete this note?" ·
  Delete / Cancel). It's disabled for an unsaved draft. On confirm, any pending save is dropped, the
  note is soft-deleted, and the editor swaps to a "Note deleted" InfoBar with **Undo** for 10 s
  (fields hidden). Undo restores it (same ID and content) and returns to editing. When the 10 s run
  out, the compact window closes itself; closing it earlier keeps the delete.
- **Labels (REQ-011).** An `AutoSuggestBox`: suggestions are existing labels not already on the
  note, matching by prefix first and then substring, case-insensitive, at most 8. If nothing
  matches the text exactly, the last suggestion is `Create label "<text>"`. Enter or picking a
  suggestion adds it. A new label gets the palette colour used by the fewest existing labels
  (ties go to palette order) and is created by the save (the store creates unknown labels by name).
  Chips have a remove button ("Remove label <name>"). Label rule errors (`label.*`,
  `labels.duplicate`) show inline and the label isn't added.
- **Fields.** Status: ComboBox of the six statuses (icon + text). Colour: "None" + 12 swatches
  (radio group, UIA name = colour key). Dates: three `CalendarDatePicker`s with a clear button each.
  Body: SelectorBar *Write* / *Preview*. Write is a multi-line `TextBox` (`AcceptsReturn`, 50k
  limit; its bare CR line breaks are stored as LF); Preview renders the Markdown (REQ-007, see 02 § Markdown). *Travel with folder* stays
  hidden until REQ-071 (Phase 5).
- **Header.** The folder's display path (selectable) and [Open folder], which opens it in Explorer
  (disabled when the folder is missing).
- **Concurrent edits** of the same note from another surface (panel in Phase 3): last write wins.
  The editor doesn't reload while open (revisit in T3.4).

### 3. Main window: NavigationView (left rail)

- **All notes**: a list or card grid toggle. Each row shows the accent bar, title, path (trimmed
  in the middle), status, labels, next date and updated time. Sort and filter commands are in a
  CommandBar.
- **Due & reminders**: overdue, next 30 days, later.
- **Labels**: a label manager (REQ-012). See § Labels page.
- **Orphaned**: notes whose folders can't be found, with Relink… / Delete (REQ-061).
- **Settings** (REQ-081).
- The list and editor use a master/detail layout above 1000 px width and stack below it.

#### Labels page (T2.5)

The **Labels** item sits under *All notes* in the navigation rail (REQ-012 says "Settings →
Labels"; resolved as its own page, and the Settings page links to it in T2.8).

```
Labels  6 labels
┌ New label ─────────────────────────────────────────────┐
│ [ name…                ]  [● orange ▾]  [Add]          │
└────────────────────────────────────────────────────────┘
[client]          Used by 3 notes   Show notes  ✎ Rename  ● Colour ▾  🗑 Delete
[do-not-delete]   Used by 1 note    Show notes  ✎ Rename  ● Colour ▾  🗑 Delete
[e2e-label]       Not used                      ✎ Rename  ● Colour ▾  🗑 Delete
```

- **List.** Every label, sorted by name (ignoring case), as a chip (the live preview of its
  colour) with its usage, "Used by N notes" / "Used by 1 note" / "Not used". Usage counts live
  notes, orphaned ones included, not soft-deleted ones.
- **New label.** A name box, a colour picker preset to the palette colour used by the fewest
  labels (ties go to palette order, the editor's rule, `Palette.LeastUsed`), and [Add] (also
  Enter). The name is trimmed. Errors show under the box: the label rules (`label.*`) and "A label
  named '<name>' already exists." Typing clears the error. After adding, the box clears and the
  next least-used colour is preset.
- **Rename.** [Rename] turns the name into a text box (focused, all selected). Enter or leaving
  the box saves; Esc cancels. A change of case only is a rename. A name that another label has
  is refused with the same message (labels aren't merged in v1). Errors show under that row,
  which stays in edit mode. Every note shows the new name (REQ-012).
- **Colour.** A drop-down with the 12 palette swatches (UIA name = colour key, the current one
  checked); picking one saves.
- **Delete.** Asks first (ContentDialog "Delete label '<name>'?" with "It will be removed from N
  notes." when used · Delete / Cancel). There's no undo: the label is removed from every note.
- **Show notes** (only when used) opens *All notes* filtered to that label alone (other filters
  cleared).
- **Starter labels (REQ-013)** look and behave like any other label. They're only created with a
  new database, so a deleted starter label doesn't come back.
- **Keeping in sync.** The page reloads (coalesced, like the library) on any label change and on
  any note change (usage counts). Open editors (the compact windows and the library's detail pane)
  refresh their labels on every label change: a renamed or recoloured label shows its new look,
  and a deleted one is dropped from the editor without counting as an edit, so a later autosave
  can't bring it back (ADR-018).
- **UIA:** rows are named "Label <name>, used by N notes"; the buttons are named "Rename <name>",
  "Colour of <name>", "Delete <name>", "Show notes with <name>".

#### Library behaviour (T2.4)

`MainWindow` hosts a `NavigationView` (left, `PaneDisplayMode=Auto`). Pages other than All notes
are created the first time they're opened (`x:Load`, since T2.6). Items appear when their task
lands: **All notes** (T2.4), **Labels** (T2.5), Orphaned (T2.7), Due & reminders (T5.1), and in the
footer **Settings** (T2.8). Until T2.8 the Settings page holds only the Theme choice that the
T2.2 token sample had (System / Light / Dark, not saved across restarts). The token sample is gone.

- **What's listed.** Every live note (`ListAsync`, `State = Active`), orphaned ones included; their
  row says "Folder not found". No paging: the list virtualizes (NFR-05 is measured in T5.5).
- **Row (list) and card (grid) content:** the accent bar (the note's colour; the space stays empty
  without one), the title on one line, the path trimmed in the middle (below), the status pill
  (not for *none*), up to 3 label chips then "+n", the next date, and "Edited <relative time>".
  The full path is in the tooltip.
- **Path trimming** (`LibraryFormat.TrimPathMiddle`, max 36 characters in a row, 40 on a card): a
  path that fits is unchanged. Otherwise keep the root (`D:\` or `\\server\share\`) and as many
  trailing segments as fit, and put `…\` between them. If even the last segment doesn't fit, it's
  cut at the end with `…`.
- **Next date** (`LibraryFormat.NextDate`): if the note has a due date (the earlier of *review on*
  and *safe after*; a tie counts as *review on*), show "Review 3 Nov 2026" or "Safe after 3 Nov
  2026", with "Overdue: " in front when the date is before today. Otherwise, if *keep until* is
  today or later, "Keep until 1 Jan 2027". Otherwise nothing. Dates use `d MMM yyyy` in the UI
  culture.
- **Relative time** (`LibraryFormat.Relative`): under 1 minute "just now"; under 1 hour "5 min
  ago"; under 24 hours "3 h ago"; the previous calendar day "yesterday"; under 7 days "3 days
  ago"; otherwise the date (`d MMM yyyy`). Local time. Future times (clock skew) show "just now".
- **Sort** (CommandBar "Sort" menu, radio items): *Last updated* (default, newest first), *Title*
  (A–Z, ignoring case), *Due date* (soonest first, notes without one last), *Path* (A–Z,
  hierarchical: a folder's subfolders come right after it, so `D:\Clients\Acme` sorts before
  `D:\ClientsOld`, O-9).
- **Filter** (CommandBar "Filter" menu): a toggle item per label (a note must have **all** the
  checked labels) and per status except *none* (a note must have **one** of the checked
  statuses), plus "Clear filters". Active filters show as chips under the command bar, each with
  a remove button ("Remove filter <name>"). The count text says "12 notes" (or "1 note") for what's
  listed.
- **View**: *List* or *Cards* (two toggle buttons that act as one choice).
- Sort, filters, view and selection survive closing and reopening the main window (the window is
  reused, 02 § App shell). They aren't saved across restarts until T2.8.
- **Empty states.** No notes at all: a folder glyph, "No notes yet", and "Right-click a folder in
  Explorer and choose Add folder note, or run FolderNotes --edit <folder>." Filters that match
  nothing: "No notes match these filters" and a [Clear filters] button.
- **Master/detail.** At 1000 px (effective pixels) and wider, the list is on the left (45 % of the
  width, at least 400 px) and the editor on the right. Narrower, only one pane shows: selecting a
  note (click, Enter) shows the editor with a Back button, and Back or `Alt+Left` returns to the
  list with the note still selected. With nothing selected, the detail pane says "Select a note to
  see it here".
- **Detail editor.** The same `NoteEditorView` as the compact window, with a fresh
  `NoteEditorViewModel` per selected note (`LoadByIdAsync`). Selecting another note, Back, and
  closing the window flush the current one first, with the same rules as the compact window.
  `Ctrl+Enter` saves now; `Esc` does nothing here. Delete shows the Undo bar; when the 10 s run out
  the detail goes back to "Select a note".
- **Keeping in sync (ADR-017).** Edits update their row in place (content only: an edited row
  doesn't move or disappear because of the sort or filters until the list next reloads). A
  deleted or purged note's row is removed (an open detail keeps its Undo bar). A note created,
  restored, relinked or orphaned elsewhere, or any label change, reloads the list. Selection is
  kept by note ID across reloads.
- **`--open-note <id>`** opens the main window on All notes and selects that note, clearing the
  filters first if they hide it. An unknown or deleted id shows "This note no longer exists" in
  the detail pane.
- **Row actions:** a context menu with *Open folder* (disabled when the folder is missing) and
  *Copy path*.
- **UIA:** a row's name is "<title>, <path>[, <status>][, labels <a, b>][, <next date>], edited
  <relative time>" (T2.9 does the full pass).

### 4. Search palette (REQ-050..053)

```
┌──────────────────────────────────────────────────┐
│ 🔍 acme label:client                              │
│──────────────────────────────────────────────────│
│ ▌Client: Acme Ltd                    🔒 [client] │
│   D:\Clients\Acme Ltd · keep until 2027          │
│ ▌Acme invoices 2025                  📦 [client] │
│   D:\Archive\Acme\2025                           │
│──────────────────────────────────────────────────│
│ ↵ open folder   Ctrl+↵ open note   Ctrl+C copy path │
└──────────────────────────────────────────────────┘
```
- A 640 px wide Acrylic window centered on the active monitor, top third. It closes when it loses
  focus or on `Esc`.
- Matched terms are highlighted. Filter chips autocomplete after typing `label:`, `status:`, `due:`.

#### Search palette behaviour (T2.6)

- **Opening.** The global hotkey (REQ-050; `Ctrl+Alt+N` until Settings makes it configurable in
  T2.8), a left-click on the tray icon, the tray menu's *Search…*, and `--search [query]` (the query
  is put in the box). Opening when it's already open brings it to the front and selects the text.
  If the hotkey can't be registered (another app has it), the app logs a warning and keeps
  working; T2.8 shows that state in Settings.
- **Window.** 640 effective px wide, no title bar, not in the taskbar or Alt+Tab, Acrylic
  (`DesktopAcrylicBackdrop`, solid fallback). Centred horizontally on the monitor of the foreground
  window (the cursor's monitor if there is none), its top at 20 % of that monitor's work area.
  Height fits the content: the box, up to 8 visible rows (the list scrolls), and the footer. It's
  hidden, not destroyed, when it closes (ADR-015), and closes on `Esc`, when it loses focus, and
  after an open action. It takes the foreground even when another app is in front, with the caret
  in the box and the text selected (ADR-019).
- **Results.** Empty box: the 8 most recently updated notes. Otherwise the search runs 120 ms after
  the last keystroke (any search still running is abandoned) and shows up to 20 hits in the
  service's order (03 § Search query grammar). The first row is selected. A parse error
  (`status:bogus`) shows under the box in the caution colour; the rest of the query still runs.
  No hits: "No notes match".
- **Row:** accent bar, title with the matched parts highlighted, status pill and up to 3 label
  chips on the right; second line: the path (middle-trimmed to 60 characters) and the next date
  (05 § Library behaviour). An orphaned note's second line ends with "Folder not found".
- **Highlighting** (`SearchHighlight.Ranges`): for each text term and each word of a phrase, every
  word in the title or path that starts with it, ignoring case and diacritics, is bold for the
  length of the term. Fuzzy hits have nothing highlighted.
- **Keys (REQ-053).** Up / Down move the selection (also from the box; they wrap). **Enter** opens
  the selected note's folder in Explorer and closes the palette; if the folder doesn't exist, the
  footer says "Folder not found" and the palette stays. **Ctrl+Enter** opens the note in the main
  window's library (`--open-note` behaviour) and closes. **Ctrl+C** copies the selected note's
  path when the box has no selected text (otherwise it copies the text, as usual); the footer says
  "Path copied" for 2 s and the palette stays open. A click on a row selects it and does Enter.
- **Footer:** `↵ open folder · Ctrl+↵ open note · Ctrl+C copy path`, or a short message (above)
  for 2 s, plus the result count ("5 results", a polite live region).
- **Autocomplete** (`SearchCompletion.Suggest`): when the text before the caret ends in a token
  `label:<partial>`, `status:<partial>` or `due:<partial>` (key case ignored, a leading `"` in
  the value allowed), the list shows suggestions instead of results: label names (quoted when they
  contain a space), the six statuses, or `overdue`, `7d`, `30d`, `90d`, that start with the
  partial value (all of them for an empty value), at most 8, in that order. Up / Down move, and
  **Tab** or **Enter** puts the full token in the box followed by a space (results come back).
  `Esc` closes the suggestions first, then the palette.
- **UIA:** the box is "Search notes"; rows are named like library rows; the suggestion list is
  "Suggestions".

### 5. Tray

- Left-click opens search. Right-click menu: Open Folder Notes, Search…, Panel on/off, Settings, Quit.

## Accessibility (REQ-083)

- Every interactive element has `AutomationProperties.Name`. Chips announce "Label: client".
- Full keyboard path: Tab order follows visual order, the panel is reachable with a hotkey
  (`Ctrl+Alt+P` focuses the panel), and the date pickers can be used with the keyboard.
- Colour is never the only signal: status always has an icon and text, and labels always have text.
