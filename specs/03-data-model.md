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

Hex format (shared with the C++ reader): upper-case, no separators. `volume_serial` is the 64-bit
`VolumeSerialNumber` as 16 digits. `file_id` is 32 digits: the 16 `Identifier` bytes read as two
little-endian `uint64`s, `hi = bytes[8..15]` printed first, then `lo = bytes[0..7]`. Volumes that
don't give a usable ID store both as `NULL` and are tracked by path only. That covers
network and removable drives, file systems other than NTFS/ReFS (checked with
`GetVolumeInformationByHandleW`, because exFAT/FAT IDs aren't stable), and an all-zero ID.
| `volume_kind` | `fixed` / `removable` / `network` | from `GetDriveType` |

**Canonical path (from S4).** Before any lookup, open the folder (`FILE_READ_ATTRIBUTES`,
`FILE_FLAG_BACKUP_SEMANTICS`, share all) and use `GetFinalPathNameByHandle` as the canonical
path. This resolves `subst` drives, 8.3 short names, `\\?\` prefixes and casing to one spelling
(it costs about 0.01–0.04 ms). The string normalizer (shared vectors) is only the fallback for
paths that can't be opened.

**String normalization rules (T1.3, REQ-063).** `PathNormalizer.Normalize` (C#) and its C++ twin
(T4.2) turn any spelling into `path_norm`. Both must pass every vector in
`tests/shared/path-normalization-vectors.json`. In order:
1. Trim whitespace, strip one pair of surrounding `"`, and trim again. If the result is empty, it's invalid.
2. Change `/` to `\`.
3. Prefixes: `\\?\UNC\x` and `\\.\UNC\x` become `\\x`. `\\?\C:…` and `\\.\C:…` become `C:…`. Any
   other `\\?\` / `\\.\` path (e.g. `\\?\Volume{guid}\…`) keeps its prefix and first segment as the
   root. A `\\?\` path is **literal**, so rule 6 doesn't apply to it (Win32 doesn't apply it either).
4. Root: `X:` followed by the end or `\` gives the drive root `X:` (upper-case letter). If the
   letter is a **mapped network drive**, the root is replaced by its UNC target (resolved with
   `WNetGetConnection` in the app; tests inject a fixed map from the vectors file). `\\server\share`
   gives a UNC root, and both parts are required. Anything else is **invalid**: relative paths,
   drive-relative `C:foo`, and rooted-without-drive `\foo`.
5. Split the rest on `\` and drop empty segments, so `\\` collapses.
6. (Not literal paths) Strip trailing dots and spaces from each segment and drop segments that
   become empty. Resolve `.` and `..`, where `..` at the root stays at the root.
7. Join as `root\seg\seg`. There's never a trailing `\`, so a drive root is `C:` and a share root is
   `\\SERVER\SHARE`.
8. `path_norm` is upper-cased with invariant simple case mapping (`ToUpperInvariant`, and in C++
   `LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE)`). `path_display` uses the same steps
   without upper-casing (only the drive letter is upper-cased).

A path is "under" a folder (for `path:` filters and inherited context) if its `path_norm` equals
the folder's or starts with the folder's `path_norm + "\"`.

**Lookup algorithm** `GetByFolder(path)`, revised after S4:
1. Read `(volume_serial, file_id)` for the folder. If a note has that pair, **return it** (and,
   in the app only, update `path_norm`/`path_display` if they differ: the folder was renamed or moved).
2. Otherwise, look up by `path_norm`. If a note matches by path but has a *different* `file_id`:
   - If the stored `file_id` still resolves (`OpenFileById`) to a live folder outside the Recycle
     Bin, the original folder **moved away** and this is a different folder at the old path.
     Return **no note**.
   - Otherwise the original folder is gone and this folder was **recreated at the same path**
     (for example a build tool wiping and rebuilding `bin\`). Return the note, and the app
     re-binds the note's `file_id` to the new folder. This is what makes "build output, safe to
     wipe" notes survive a rebuild.
3. Otherwise, there's no note.

The shell extension runs steps 1 and 2 read-only. For the step 2 decision, it returns the note
only if the stored `file_id` doesn't resolve. Re-binding is left to the app.

**Orphan detection** (background, at startup, then every 6 h, and on demand): for each note, if the
path doesn't exist, try `OpenFileById(volume, file_id)` → `GetFinalPathNameByHandle` to find the
new path. If it resolves under `\$Recycle.Bin\`, mark the note **orphaned (in Recycle Bin)**
rather than relinking it. If it doesn't resolve, set `orphaned_at`.

### S4 findings (2026-10-31, Windows 11 23H2 build 22631, NTFS)

| Case | Result |
|---|---|
| Rename / move to another parent / ancestor renamed | file ID unchanged ✅ |
| `OpenFileById` relink after move | finds the new path ✅, 0.04 ms |
| Copy | new file ID (a copy doesn't inherit the note) ✅ |
| Delete + recreate at the same path | **new file ID**, so the lookup needs the step 2 rule above |
| Deleted folder | `OpenFileById` fails, so the note is orphaned ✅ |
| Sent to Recycle Bin | ID still resolves, to `C:\$Recycle.Bin\<SID>\$R…`, so it must be detected explicitly |
| `subst` alias, 8.3 short name, `\\?\`, upper-case | same file ID ✅ |
| `GetId` cost | 0.01 ms |
| **Not tested** (no hardware or access): exFAT/FAT32 (no stable 128-bit IDs expected), ReFS, network shares, OneDrive Files-On-Demand folders (deliberately avoided, since they would sync to the cloud), cross-volume moves (expected new ID; path fallback applies) |

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
