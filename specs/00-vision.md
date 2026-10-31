# 00 — Vision

## Problem

Folders carry meaning that's invisible in the file system: "client files, keep until 2027", "build
output, safe to wipe", "Mum's tax docs, ask before touching". Today that context lives in your
head, a stray `README.txt`, or nowhere.

Windows has a built-in folder comment mechanism (`desktop.ini` `InfoTip`), but it's buried. You
have to hand-edit a hidden file and set folder attributes, it only shows plain text, and it
doesn't show up reliably across views. You can't search it, it has no dates or labels, and it
disappears when the folder is copied without hidden files.

## Product

**Folder Notes** lets you pin a rich note to any folder and see it right where you are working:

- **Pin**: right-click a folder → "Add folder note…" → a polished editor with title, body,
  labels, dates and status.
- **See**: a slim companion panel docks next to File Explorer and shows the note for whichever
  folder is open. Hovering a folder in Explorer shows a short tooltip summary.
- **Find**: a global search (hotkey) across every note, label and date. Results jump straight to
  the folder.
- **Remember**: date-based reminders ("keep until 2027-01-01" → "this folder is now safe to
  review").

## Target users

1. **Freelancers / small businesses**: client folders with retention obligations.
2. **Developers**: build/cache/output folders, "safe to wipe" vs "do not touch".
3. **Home power users**: family documents, archives, backups, photo dumps.

## Goals (v1)

- G1. Adding a note takes under 10 seconds from right-click to saved.
- G2. The note for the current folder is visible without any extra click.
- G3. Any note can be found in under 2 seconds with search.
- G4. Notes survive folder renames and moves on the same drive.
- G5. It looks and feels native to Windows 11 (Fluent, Mica, dark/light, accessible).

## Non-goals (v1)

- Notes on individual **files** (folders only; files may come in v2).
- Cloud sync or multi-user sharing (export/import only in v1).
- macOS/Linux.
- Replacing Explorer or acting as a file manager.

## Principles (non-negotiable)

1. **Do no harm to Explorer.** Explorer must never crash, hang or slow down because of us.
2. **Non-invasive by default.** We don't write into user folders unless the user opts in per folder.
3. **Local-first and private.** All data stays on the machine. No telemetry without opt-in.
4. **Fast.** The panel updates within 150 ms of a folder change. The tooltip lookup takes under 20 ms.
5. **Beautiful but quiet.** Information appears when it's relevant and gets out of the way otherwise.
