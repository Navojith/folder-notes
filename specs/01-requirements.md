# 01 — Requirements

Format: user stories, then requirements written in EARS style
(*When / While / If / Where … the system shall …*). Each requirement has testable acceptance
criteria (AC). IDs are stable: never renumber, only deprecate.

Priority: **M** = must (v1), **S** = should (v1 if time), **C** = could (later).

---

## A. Notes: create, edit, delete

**Story:** As a user, I want to attach a note to a folder so its purpose and rules are recorded.

| ID | Pri | Requirement |
|---|---|---|
| REQ-001 | M | The system shall allow at most **one note per folder**. |
| REQ-002 | M | A note shall contain: **title** (≤120 chars, required), **body** (Markdown, ≤50k chars, optional), **labels** (0..n), **status** (one of: none, keep, safe-to-delete, archive, in-progress, review), **dates** (see REQ-020), **created/updated timestamps**, and an optional **accent color**. |
| REQ-003 | M | When the user picks "Add folder note…" on a folder without a note, the system shall open the editor pre-filled with the folder name as the suggested title. |
| REQ-004 | M | When the user picks "Edit folder note…" on a folder with a note, the system shall open that note in the editor. |
| REQ-005 | M | The editor shall autosave 500 ms after the last keystroke and on close. Nothing shall be lost on crash beyond the last 500 ms. |
| REQ-006 | M | When the user deletes a note, the system shall ask for confirmation and offer **Undo** for 10 seconds. |
| REQ-007 | S | The body editor shall support basic Markdown (headings, bold, italic, lists, checkboxes, links, inline code) with a live preview. |
| REQ-008 | C | Templates: the user can save a note as a template ("Client folder", "Build output"). |

**AC (REQ-001…006):**
- Adding a second note to the same folder opens the existing note instead.
- A title longer than 120 characters can't be saved and shows an inline error.
- If the app is killed during editing, the note reopens with all text typed more than 500 ms before the kill.
- After a delete, clicking Undo restores the note with identical content and ID.

## B. Labels

| ID | Pri | Requirement |
|---|---|---|
| REQ-010 | M | Labels shall be global (shared across notes), each with a name (unique, case-insensitive) and a color from a fixed palette of 12 Fluent-friendly colors. |
| REQ-011 | M | The editor shall offer label autocomplete and allow creating a new label inline. |
| REQ-012 | M | The user shall be able to rename, recolor and delete labels from Settings → Labels. Renaming updates every note. |
| REQ-013 | S | Built-in starter labels: `client`, `build-output`, `archive`, `personal`, `do-not-delete`. All of them are editable. |

## C. Dates & reminders

| ID | Pri | Requirement |
|---|---|---|
| REQ-020 | M | A note may have these optional dates: **keep until**, **review on**, and **expires/safe after**. |
| REQ-021 | M | When a note's *review on* or *safe after* date arrives, the system shall show a Windows toast notification with actions **Open folder**, **Open note** and **Snooze 7 days**. |
| REQ-022 | M | While a note's *keep until* date is in the future, the panel and tooltip shall show a "Keep until <date>" badge. |
| REQ-023 | S | Search shall support date filters: `due:overdue`, `due:30d`, `keep:<2027`. |
| REQ-024 | M | If the app wasn't running when a reminder came due, the system shall show that reminder on next start (each one shown once). |

## D. Companion side panel

**Story:** As a user, when I open a folder in Explorer, I want to see its note without clicking anything.

| ID | Pri | Requirement |
|---|---|---|
| REQ-030 | M | While a File Explorer window is in the foreground, the system shall show a companion panel attached to that window's right edge (or left, or floating, per setting). |
| REQ-031 | M | When the active Explorer folder changes (navigation, tab switch, window switch), the panel shall update within **150 ms**. |
| REQ-032 | M | When the current folder has no note, the panel shall show a compact "Add a note" state, or collapse to a slim tab (setting: *Hide when empty*, default **on**). |
| REQ-033 | M | The panel shall follow the Explorer window when it moves, resizes, minimizes, maximizes or snaps, and shall hide when no Explorer window is in the foreground. |
| REQ-034 | M | The panel shall support Windows 11 Explorer **tabs** and show the note for the active tab. |
| REQ-035 | S | The panel shall show **inherited context**: if the folder has no note but an ancestor does, it shows "Inside: <ancestor title>" with a link. |
| REQ-036 | M | The user shall be able to edit the note inline in the panel (same autosave as REQ-005). |
| REQ-037 | M | The user shall be able to turn the panel off globally and pin or unpin it (pinned = always visible, unpinned = auto-hide when empty). |
| REQ-038 | M | The panel shall never take keyboard focus from Explorer unless the user clicks into it. |

## E. Hover tooltip

| ID | Pri | Requirement |
|---|---|---|
| REQ-040 | M | When the user hovers a folder that has a note in Explorer (any view, including the navigation pane where supported), Explorer's infotip shall include: the note title, status, labels (as text), the key date, and the first 2 lines of the body. |
| REQ-041 | M | The tooltip lookup shall finish in under **20 ms**. If the data isn't available within **50 ms**, the extension shall return the default Explorer tooltip (fail open). |
| REQ-042 | M | The tooltip extension shall be read-only and shall never write to disk or the network. |
| REQ-043 | S | The user can turn the tooltip integration off in Settings. This unregisters the handler. |

## F. Search

| ID | Pri | Requirement |
|---|---|---|
| REQ-050 | M | A global hotkey (default `Ctrl+Alt+N`, configurable) shall open a search palette from anywhere. |
| REQ-051 | M | Search shall match title, body, labels and folder path, with prefix matching and typo-tolerant ranking, and return results in under **100 ms** for 10,000 notes. |
| REQ-052 | M | Search shall support filters: `label:client`, `status:safe-to-delete`, `path:D:\Projects`, plus REQ-023 date filters. |
| REQ-053 | M | Each result shall offer: **Open folder in Explorer** (Enter), **Open note** (Ctrl+Enter) and **Copy path** (Ctrl+C). |
| REQ-054 | M | The main window shall have a "All notes" library view with sort (updated, title, due date, path) and filter by label/status. |

## G. Folder identity & robustness

| ID | Pri | Requirement |
|---|---|---|
| REQ-060 | M | Notes shall stay attached when a folder is **renamed or moved within the same NTFS/ReFS volume**. |
| REQ-061 | M | When a noted folder no longer exists at its path and can't be re-found, the note shall be marked **orphaned** (not deleted) and listed in the "Orphaned" view with actions *Relink…*, *Delete*. |
| REQ-062 | S | Folders on network shares and removable drives shall be supported by path (with no rename tracking), and shall be marked as such in the UI. |
| REQ-063 | M | Path matching shall be case-insensitive and normalized (trailing slashes, `\\?\` prefixes, mapped drive vs UNC). |

## H. Portability (opt-in)

| ID | Pri | Requirement |
|---|---|---|
| REQ-070 | M | The user shall be able to export all notes (or a selection) to JSON and import them again, matching by path. |
| REQ-071 | S | **Where** the user enables "Travel with folder" on a note, the system shall also write a hidden sidecar file `.foldernote.json` in that folder and keep it in sync, so the note survives copying to another machine. |
| REQ-072 | S | When the app finds a `.foldernote.json` in a folder with no note, the panel shall offer "Import note found in this folder". |
| REQ-073 | C | Optional mirror to `desktop.ini` `InfoTip` for compatibility with machines that don't have the app. |

## I. App shell & settings

| ID | Pri | Requirement |
|---|---|---|
| REQ-080 | M | The app shall run as a lightweight tray process that starts with Windows (setting, default on) and uses under 60 MB RAM while idle. |
| REQ-081 | M | Settings: panel on/off/position/width, hide-when-empty, tooltip on/off, hotkey, start with Windows, theme (system/light/dark), reminder time of day. |
| REQ-082 | M | The UI shall follow the system theme and accent color and use Mica/Acrylic backdrops where available. |
| REQ-083 | M | All UI shall be keyboard-navigable and screen-reader labelled (UIA names), with WCAG AA contrast. |
| REQ-084 | S | Automatic local backup of the database, daily, keeping the last 7 copies. |

## Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | Explorer stability: zero crashes or hangs caused by our code (verified with the stress tests in `06-testing.md`). |
| NFR-02 | Cold start of the tray app under 1.5 s. The panel appears within 300 ms of Explorer getting focus. |
| NFR-03 | All data lives in `%LOCALAPPDATA%\FolderNotes\`. No network access at all in v1. |
| NFR-04 | Per-user install, no admin rights required. |
| NFR-05 | Scales to 10,000 notes without degrading REQ-031/041/051 targets. |
