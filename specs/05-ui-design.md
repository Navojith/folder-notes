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

### 3. Main window: NavigationView (left rail)

- **All notes**: a list or card grid toggle. Each row shows the accent bar, title, path (trimmed
  in the middle), status, labels, next date and updated time. Sort and filter commands are in a
  CommandBar.
- **Due & reminders**: overdue, next 30 days, later.
- **Labels**: a label manager (REQ-012).
- **Orphaned**: notes whose folders can't be found, with Relink… / Delete (REQ-061).
- **Settings** (REQ-081).
- The list and editor use a master/detail layout above 1000 px width and stack below it.

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

### 5. Tray

- Left-click opens search. Right-click menu: Open Folder Notes, Search…, Panel on/off, Settings, Quit.

## Accessibility (REQ-083)

- Every interactive element has `AutomationProperties.Name`. Chips announce "Label: client".
- Full keyboard path: Tab order follows visual order, the panel is reachable with a hotkey
  (`Ctrl+Alt+P` focuses the panel), and the date pickers can be used with the keyboard.
- Colour is never the only signal: status always has an icon and text, and labels always have text.
