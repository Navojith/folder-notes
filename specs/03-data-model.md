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
   - If the folder is on a **different volume** (`volume_serial` differs: another drive mounted
     at the same letter, a reformatted drive), it's a different folder. Return **no note** and
     re-bind nothing (ADR-020).
   - If the stored `file_id` still resolves (`OpenFileById`) to a live folder outside the Recycle
     Bin, the original folder **moved away** and this is a different folder at the old path.
     Return **no note**.
   - Otherwise the original folder is gone and this folder was **recreated at the same path**
     (for example a build tool wiping and rebuilding `bin\`). Return the note, and the app
     re-binds the note's `file_id` to the new folder. This is what makes "build output, safe to
     wipe" notes survive a rebuild.
3. Otherwise, there's no note.

The shell extension runs steps 1 and 2 read-only. For the step 2 decision, it returns the note
only if the volume serial matches and the stored `file_id` doesn't resolve. Re-binding is left to
the app. Every re-bind is conditional on the note's binding not having changed since it was read
(see § Relink rules).

**Orphan detection** (T2.7, `OrphanScanner`; at startup, then every 6 h, when the Orphaned page
opens, and on demand with *Check again*). Each live note is first **classified** from the file
system alone (no writes), then the result is **applied** with conditional writes:

| Folder state | How it's decided | Applied |
|---|---|---|
| `Present` | the folder is at the stored path (by file ID, or by path for path-only notes) | clear `orphaned_at` if set |
| `Moved` | `Locate` finds the file ID at another path, and resolving that path gives the same volume + file ID | re-bind to the new path |
| `Recreated` | the file ID is gone (`NotFound`) or in the Recycle Bin, and a folder exists at the stored path **on the same volume** (ADR-009) | re-bind to that folder |
| `InRecycleBin` | the file ID resolves under `\$Recycle.Bin\` and nothing is at the stored path | orphan (reason: in the Recycle Bin) |
| `DriveUnavailable` | the volume with the stored serial isn't mounted (`Locate` → `VolumeNotMounted`), or a path-only note's drive root / share doesn't exist | orphan (reason: drive not connected) |
| `Missing` | anything else: the folder was deleted, or moved where its ID can't follow (another drive) | orphan (reason: not found) |

- A re-bind that hits another live note on the target path or file ID (`DuplicateFolderNoteException`)
  changes **neither** note: the moving note is orphaned instead (outcome `Conflict`; reason
  "another note is attached there"). Nothing is merged or taken over automatically.
- A note whose binding or orphan state changed between the read and the write (a concurrent
  relink, an editor) is skipped; the next check sees the new state.
- An orphan whose folder comes back (drive reconnected, folder restored from the Recycle Bin) is
  recovered by the next check.
- The scan never touches title, body, labels, status, dates or `updated_at`.

**Relink rules** (T2.7, REQ-061 *Relink…*):
- `RelinkAsync(id, newFolder, expectedFolder)` binds the note to `newFolder` and clears
  `orphaned_at`, in one transaction. With `expectedFolder`, it first checks that the stored binding
  (`path_norm`, `volume_serial`, `file_id`) still equals it, else throws
  `NoteBindingChangedException` and changes nothing. All app callers pass it.
- Refused (nothing changes): the note is soft-deleted or missing (`KeyNotFoundException`); another
  live note has the target path or file ID (`DuplicateFolderNoteException`, REQ-001).
- Checked before the write by `RelinkTarget.Check` (Core): the picked path must be a valid,
  existing folder (not a file) outside the Recycle Bin. If its name differs from the note's
  current folder name (ignoring case), the app asks first.
- `SetOrphanedAsync(id, orphaned, expectedFolder)` follows the same `expectedFolder` rule.
- **Undo** of a relink = relink back to the previous binding (expecting the new one), then mark it
  orphaned again if it was. `orphaned_at` then holds the time of the undo.
- Neither changes `updated_at` (a relink isn't an edit), title, body, labels, status or dates.

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

-- Full-text search (REQ-051). Stores its own copy of the text (ADR-010); rowid = notes.rowid.
CREATE VIRTUAL TABLE notes_fts USING fts5(
  title, body_md, labels, path_display,
  tokenize='unicode61 remove_diacritics 2', prefix='2 3'
);
```

`notes_fts` rows are keyed by `notes.rowid` and written by the repository (no triggers) on every
upsert, rebind, label rename/delete and purge. Soft-deleted notes keep their FTS row, so Undo is
cheap, and search filters them out with a join. `notes` rows are only ever changed with
`UPDATE` / `INSERT … ON CONFLICT DO UPDATE` (never `INSERT OR REPLACE`), so the rowid is stable.
Running `VACUUM` requires rebuilding `notes_fts` afterwards.
`notes_fts.labels` is a space-joined list of label names (simpler than triggers across the
junction table). Ranking is `bm25(notes_fts, 10, 1, 5, 2)`
(title > labels > path > body). Typo tolerance: if FTS returns fewer than 5 hits, fall back to a
trigram/Levenshtein pass over titles and labels in memory (fine up to 10k notes).

### Search query grammar (T1.6, REQ-051/052/023)

The query is split on whitespace, and `"…"` groups words (also inside a filter value:
`label:"do not touch"`). Filter keys ignore case. Every filter and term must match (AND).

| Token | Meaning |
|---|---|
| `word` | Prefix match in title, body, labels or path (FTS5 `"word"*`) |
| `"two words"` | Phrase, with a prefix match on the last word |
| `label:<name>` | Has that label (name ignores case). Repeat it to require several labels. |
| `status:<key>` | One of `none, keep, safe-to-delete, archive, in-progress, review`. Repeating it means *any of* them. |
| `path:<folder>` | The note's folder is that folder or inside it. The value is normalized (03 § String normalization). Repeating it means any of them. |
| `due:overdue` | Due date (earliest of *review on* / *safe after*) is before today |
| `due:<n>d` / `<n>w` / `<n>m` / `<n>y` | Due from today through today + n days / weeks / months / years |
| `due:<op><period>`, `keep:<op><period>` | Compare the due date or *keep until* with a period `yyyy`, `yyyy-mm` or `yyyy-mm-dd`. `op` is `<`, `<=`, `>`, `>=`, or nothing (inside the period). `keep:<2027` means before 2027-01-01. `keep:>2027` means after 2027-12-31. `keep:<n>d` (relative ranges) also works. |

A `key:value` with an unknown key is plain text (so `C:\Temp` still searches). A known key with a
bad value (`status:bogus`, `due:soon`) is ignored and reported back as a parse error, which the
palette shows under the box. "Today" is the local calendar date.

**Ranking:** FTS hits come first, ordered by `bm25`, with ties broken by `updated_at` (newest
first). With no text terms, the filters alone list notes newest first. **Typo fallback:** if
there are fewer than 5 FTS hits, every term with at least 4 characters may match a title word or
label name in memory within Damerau-Levenshtein distance 1 (4–7 characters) or 2 (8 or more).
Comparison ignores case and diacritics. A term shorter than 4 characters must prefix-match. A phrase's words must match consecutive words. Fuzzy
hits are appended after the FTS hits, ordered by total distance. The candidate cache is rebuilt
after any `NoteChanged` / `LabelsChanged`.

## Storage conventions (T1.5)

| Value | Stored as |
|---|---|
| `id` | GUID, lower-case `D` format (`8f0c1a2b-…`) |
| Timestamps | UTC ISO 8601 with milliseconds, `yyyy-MM-ddTHH:mm:ss.fffZ` (fixed width, sorts as text) |
| Dates | `yyyy-MM-dd` |
| `status`, `volume_kind`, colors | the lower-case keys from `NoteStatus`, `VolumeKind`, `Palette` |

**Schema versions** (`PRAGMA user_version`): `0` = empty file, `1` = the tables above plus the
REQ-013 starter labels. Before migrating a database that has `user_version ≥ 1`, the app copies it
with the SQLite online-backup API to `%LOCALAPPDATA%\FolderNotes\backups\notes-v<from>-<UTC yyyyMMddHHmmss>.db`.
A database with a *newer* version than the app knows is opened read-only and reported, never downgraded.

**Settings** (T2.8, `settings` table, 02 § Settings). Keys and values (strings; invalid values are
read as the default):

| Key | Values | Default |
|---|---|---|
| `theme` | `system` / `light` / `dark` | `system` |
| `search.hotkey` | `Ctrl+Alt+N` form: modifiers `Ctrl`, `Alt`, `Shift`, `Win` then one key (`A`–`Z`, `0`–`9`, `F1`–`F24`, `Space`) | `Ctrl+Alt+N` |
| `library.sort` | `updated` / `title` / `due` / `path` | `updated` |
| `library.view` | `list` / `cards` | `list` |
| `library.labels` | label names, JSON array | `[]` |
| `library.statuses` | status keys (`NoteStatus` keys), JSON array | `[]` |

*Start with Windows* isn't stored here: its truth is the Run entry (ADR-022). Later features add
their keys (panel, tooltip, reminders).

**Repository rules:**
- Upsert sets `updated_at` to now. On first insert it keeps `created_at` from the note. Import (T1.7)
  keeps both timestamps.
- **Upsert never changes an existing note's folder binding or orphan state** (T2.7, ADR-020): for a
  note that's already stored, `path_norm`, `path_display`, `volume_serial`, `file_id`,
  `volume_kind` and `orphaned_at` stay as stored, and the returned note carries the stored values.
  Only the lookup's re-binding, the orphan scan, `RelinkAsync` and `SetOrphanedAsync` change them.
  So an editor that was open while the note was relinked can't put the old folder back. Import
  (T1.7) is the one exception: it's an explicit restore, so a matched note takes the imported
  binding (§ Export format; matching by `id` can move it).
- Labels on an upserted note are matched to existing labels by name, ignoring case (the existing
  label's color wins). Unknown names create the label (inline create, REQ-011).
- A second live note for the same `path_norm` or `(volume_serial, file_id)` gives
  `DuplicateFolderNoteException` with the existing note's id (REQ-001). The editor opens that note.
- Upserting a soft-deleted note fails. Restore it first.
- Restore fails with `DuplicateFolderNoteException` if another live note has taken the folder in the meantime.
- Soft-deleted notes are purged 30 days after `deleted_at` (`PurgeDeletedAsync`).
- **Label usage** (`CountNotesByLabelAsync`, T2.5) counts live notes per label (orphaned included,
  soft-deleted not); a label on no live note is missing from the result (= 0). Deleting a label
  removes it from every note, soft-deleted ones included (the `note_labels` cascade).
- **Path sort is hierarchical** (`NoteSort.Path`, REQ-054, O-9): it orders by `path_norm` with every
  `\` replaced by `char(1)`, so a folder's subfolders come right after it and before any sibling
  whose name merely starts the same (`D:\CLIENTS\ACME` before `D:\CLIENTSOLD`). Ties go by `id`.

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

Full shape (T1.7). It's UTF-8, snake_case, and null fields are written out:

```json
{
  "schema": "foldernotes-export/1",
  "exported_at": "2026-10-31T10:00:00.000Z",
  "labels": [{ "name": "client", "color": "blue" }],
  "notes": [{
    "id": "8f0c…", "path_display": "D:\\Clients\\Acme",
    "title": "Client: Acme Ltd", "body_md": "…", "status": "keep", "accent_color": null,
    "labels": ["client"],
    "keep_until": "2027-01-01", "review_on": null, "safe_after": null,
    "travel_with_folder": false,
    "created_at": "2026-01-02T08:00:00.000Z", "updated_at": "2026-10-31T10:00:00.000Z"
  }]
}
```

- Export skips soft-deleted notes. A full export includes every label. A selection includes only
  the labels its notes use. Volume and file IDs aren't exported because they're machine-specific.
  Import re-reads them from the folder if it exists.
- **Import is one transaction**: either all valid notes go in or (on an I/O or schema error) none
  do. Entries that fail validation are skipped and listed in the report. Unknown `schema` → refused.
- **Labels:** created if missing. If a label with that name exists, it takes the **exported color**
  (import is a restore, and keeping colors is what makes the round trip lossless).
- **Matching (REQ-070, by path):** a note matches the live note at the same `path_norm`, or else the
  live note with the same `id`. Otherwise it's new. It keeps its exported `id` unless that id is
  already used (for example by a deleted note), in which case it gets a new one.
- **Conflicts** (a match exists) follow `ImportConflictPolicy`. The default, `NewerWins`, matches the
  sidecar rule: the import replaces the note only if its `updated_at` is newer. The other policies
  are `Overwrite` and `KeepExisting`. A replaced note keeps its existing `id` and `created_at`.
- Imported timestamps are kept as they are (not "now").
