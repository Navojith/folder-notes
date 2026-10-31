# 03 — Data Model

Location: `%LOCALAPPDATA%\FolderNotes\notes.db` (SQLite 3.45+, `journal_mode=WAL`,
`foreign_keys=ON`). Schema version is tracked in `PRAGMA user_version`. Migrations are forward-only,
applied at app start, and the database is backed up before each migration.

## Folder identity (REQ-060..063)

A folder is identified by a **FolderRef**:

| Field | Source | Notes |
|---|---|---|
| `path_norm` | normalized full path | uppercase-invariant compare key, no trailing `\`, `\\?\` stripped, mapped drives resolved to UNC |
| `path_display` | path as the user sees it | original casing |
| `volume_serial` | `GetFileInformationByHandleEx(FileIdInfo).VolumeSerialNumber` | null for network/removable |
| `file_id` | `FILE_ID_INFO.FileId` (128-bit, hex) | stable across rename/move on the same volume (NTFS & ReFS) |
| `volume_kind` | `fixed` / `removable` / `network` | from `GetDriveType` |

**Lookup algorithm** `GetByFolder(path)`:
1. Normalize the path, then look up by `path_norm`. If found and (no file_id, or file_id matches), return the note.
2. Otherwise, if the volume supports file IDs, open the folder (`FILE_FLAG_BACKUP_SEMANTICS`, no
   share restrictions), read `(volume_serial, file_id)`, and look up by that pair. If found, the
   folder was renamed or moved: **update `path_norm`/`path_display`** (the app does this; the shell
   extension just returns the note) and return it.
3. Otherwise, there's no note.

**Orphan detection** (background, at startup, then every 6 h, and on demand): for each note, if the
path doesn't exist, try `OpenFileById(volume, file_id)` → `GetFinalPathNameByHandle` to find the
new path. If that fails, set `orphaned_at`.

## Tables

```sql
CREATE TABLE notes (
  id              TEXT PRIMARY KEY,          -- GUID
  path_norm       TEXT NOT NULL,
  path_display    TEXT NOT NULL,
  volume_serial   TEXT,                      -- hex, nullable
  file_id         TEXT,                      -- hex 128-bit, nullable
  volume_kind     TEXT NOT NULL DEFAULT 'fixed',
  title           TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 120),
  body_md         TEXT NOT NULL DEFAULT '',
  status          TEXT NOT NULL DEFAULT 'none'
                  CHECK (status IN ('none','keep','safe-to-delete','archive','in-progress','review')),
  accent_color    TEXT,                      -- palette key, nullable
  keep_until      TEXT,                      -- ISO date yyyy-mm-dd
  review_on       TEXT,
  safe_after      TEXT,
  travel_with_folder INTEGER NOT NULL DEFAULT 0,   -- REQ-071
  created_at      TEXT NOT NULL,             -- ISO 8601 UTC
  updated_at      TEXT NOT NULL,
  deleted_at      TEXT,                      -- soft delete (undo, REQ-006); purged after 30 days
  orphaned_at     TEXT                       -- REQ-061
);
CREATE UNIQUE INDEX ux_notes_path   ON notes(path_norm) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_notes_fileid ON notes(volume_serial, file_id)
       WHERE deleted_at IS NULL AND file_id IS NOT NULL;

CREATE TABLE labels (
  id     TEXT PRIMARY KEY,
  name   TEXT NOT NULL COLLATE NOCASE UNIQUE,
  color  TEXT NOT NULL                        -- palette key
);

CREATE TABLE note_labels (
  note_id  TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
  label_id TEXT NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
  PRIMARY KEY (note_id, label_id)
);

CREATE TABLE reminders_shown (                -- REQ-024: show each due reminder once
  note_id   TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
  kind      TEXT NOT NULL CHECK (kind IN ('review_on','safe_after')),
  due_date  TEXT NOT NULL,
  shown_at  TEXT NOT NULL,
  snoozed_until TEXT,
  PRIMARY KEY (note_id, kind, due_date)
);

CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

-- Full-text search (REQ-051). External content, kept in sync by triggers.
CREATE VIRTUAL TABLE notes_fts USING fts5(
  title, body_md, labels, path_display,
  content='', tokenize='unicode61 remove_diacritics 2', prefix='2 3'
);
```

`notes_fts.labels` is a space-joined list of label names, maintained by the repository on every
upsert (simpler than triggers across the junction table). Ranking is `bm25(notes_fts, 10, 1, 5, 2)`
(title > labels > path > body). Typo tolerance: if FTS returns fewer than 5 hits, fall back to a
trigram/Levenshtein pass over titles and labels in memory (fine up to 10k notes).

## Tooltip read path (shell extension, REQ-040/041)

The shell extension runs only this query (prepared once per connection):

```sql
SELECT n.title, n.status, n.keep_until, n.review_on, n.safe_after,
       substr(n.body_md, 1, 400),
       (SELECT group_concat(l.name, ', ') FROM note_labels nl JOIN labels l ON l.id = nl.label_id
         WHERE nl.note_id = n.id)
FROM notes n
WHERE n.deleted_at IS NULL AND (n.path_norm = ?1 OR (n.volume_serial = ?2 AND n.file_id = ?3))
LIMIT 1;
```

The path is normalized in C++ using the same rules as Core. A shared test-vector file
(`tests/shared/path-normalization-vectors.json`) is checked by both test suites.

## Sidecar file (opt-in, REQ-071)

`<folder>\.foldernote.json`, with Hidden attribute set:

```json
{
  "schema": "foldernotes/1",
  "id": "8f0c…",
  "title": "Client: Acme Ltd",
  "body_md": "…",
  "status": "keep",
  "labels": [{"name": "client", "color": "blue"}],
  "keep_until": "2027-01-01",
  "review_on": null,
  "safe_after": null,
  "updated_at": "2026-10-31T10:00:00Z"
}
```

Conflict rule: the newer `updated_at` wins. If both copies changed since the last sync, the app
keeps the DB version and saves the sidecar as a revision in the note history (v2) or asks the user
(v1: dialog).

## Export format (REQ-070)

`foldernotes-export-YYYYMMDD.json` = `{ "schema": "foldernotes-export/1", "labels": [...], "notes": [...sidecar shape + path_display...] }`.
