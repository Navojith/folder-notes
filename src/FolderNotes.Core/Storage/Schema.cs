using FolderNotes.Core.Domain;
using Microsoft.Data.Sqlite;

namespace FolderNotes.Core.Storage;

/// <summary>One forward-only migration (03 § Schema versions).</summary>
internal sealed record Migration(int Version, Action<SqliteConnection, SqliteTransaction> Apply);

/// <summary>The schema from specs/03-data-model.md § Tables, as migrations.</summary>
internal static class Schema
{
    public static IReadOnlyList<Migration> Migrations { get; } = [new(1, V1)];

    public static int CurrentVersion => Migrations[^1].Version;

    private const string V1Sql = """
        CREATE TABLE notes (
          id              TEXT PRIMARY KEY,
          path_norm       TEXT NOT NULL,
          path_display    TEXT NOT NULL,
          volume_serial   TEXT,
          file_id         TEXT,
          volume_kind     TEXT NOT NULL DEFAULT 'fixed',
          title           TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 120),
          body_md         TEXT NOT NULL DEFAULT '',
          status          TEXT NOT NULL DEFAULT 'none'
                          CHECK (status IN ('none','keep','safe-to-delete','archive','in-progress','review')),
          accent_color    TEXT,
          keep_until      TEXT,
          review_on       TEXT,
          safe_after      TEXT,
          travel_with_folder INTEGER NOT NULL DEFAULT 0,
          created_at      TEXT NOT NULL,
          updated_at      TEXT NOT NULL,
          deleted_at      TEXT,
          orphaned_at     TEXT
        );
        CREATE UNIQUE INDEX ux_notes_path   ON notes(path_norm) WHERE deleted_at IS NULL;
        CREATE UNIQUE INDEX ux_notes_fileid ON notes(volume_serial, file_id)
               WHERE deleted_at IS NULL AND file_id IS NOT NULL;

        CREATE TABLE labels (
          id     TEXT PRIMARY KEY,
          name   TEXT NOT NULL COLLATE NOCASE UNIQUE,
          color  TEXT NOT NULL
        );

        CREATE TABLE note_labels (
          note_id  TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
          label_id TEXT NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
          PRIMARY KEY (note_id, label_id)
        );
        CREATE INDEX ix_note_labels_label ON note_labels(label_id);

        CREATE TABLE reminders_shown (
          note_id   TEXT NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
          kind      TEXT NOT NULL CHECK (kind IN ('review_on','safe_after')),
          due_date  TEXT NOT NULL,
          shown_at  TEXT NOT NULL,
          snoozed_until TEXT,
          PRIMARY KEY (note_id, kind, due_date)
        );

        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE VIRTUAL TABLE notes_fts USING fts5(
          title, body_md, labels, path_display,
          tokenize='unicode61 remove_diacritics 2', prefix='2 3'
        );
        """;

    private static void V1(SqliteConnection c, SqliteTransaction tx)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = V1Sql;
            cmd.ExecuteNonQuery();
        }

        // REQ-013 starter labels.
        foreach (var (name, color) in Label.StarterLabels)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO labels(id, name, color) VALUES ($id, $name, $color)";
            ins.Parameters.AddWithValue("$id", StorageFormat.Id(Guid.NewGuid()));
            ins.Parameters.AddWithValue("$name", name);
            ins.Parameters.AddWithValue("$color", color);
            ins.ExecuteNonQuery();
        }
    }
}
