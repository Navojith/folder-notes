using System.Globalization;
using System.Text;
using FolderNotes.Core.Domain;
using Microsoft.Data.Sqlite;

namespace FolderNotes.Core.Storage;

/// <summary>Turns a <see cref="NoteQuery"/> into a parameterized WHERE clause over <c>notes n</c>. Shared by list and search.</summary>
internal static class NoteFilterSql
{
    public static string Where(NoteQuery q, IReadOnlyList<Label> allLabels, out Action<SqliteCommand> bind)
    {
        var sb = new StringBuilder();
        var binds = new List<Action<SqliteCommand>>();

        sb.Append(q.State switch
        {
            NoteListState.Active => "n.deleted_at IS NULL",
            NoteListState.Orphaned => "n.deleted_at IS NULL AND n.orphaned_at IS NOT NULL",
            NoteListState.Deleted => "n.deleted_at IS NOT NULL",
            _ => "1",
        });

        for (var i = 0; i < q.Labels.Count; i++)
        {
            var label = allLabels.FirstOrDefault(l => Label.NameComparer.Equals(l.Name, q.Labels[i].Trim()));
            if (label is null)
            {
                sb.Append(" AND 0"); // unknown label: nothing matches
                continue;
            }

            var p = Param("lab", i);
            sb.Append(CultureInfo.InvariantCulture, $" AND EXISTS (SELECT 1 FROM note_labels nl WHERE nl.note_id = n.id AND nl.label_id = {p})");
            var id = StorageFormat.Id(label.Id);
            binds.Add(c => c.Parameters.AddWithValue(p, id));
        }

        if (q.Statuses.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < q.Statuses.Count; i++)
            {
                var p = Param("st", i);
                names.Add(p);
                var key = q.Statuses[i].ToKey();
                binds.Add(c => c.Parameters.AddWithValue(p, key));
            }

            sb.Append(" AND n.status IN (").Append(string.Join(", ", names)).Append(')');
        }

        if (q.PathsUnder.Count > 0)
        {
            var parts = new List<string>();
            for (var i = 0; i < q.PathsUnder.Count; i++)
            {
                var p = Param("path", i);
                var prefix = q.PathsUnder[i];
                // Same rule as PathNormalizer.IsSameOrUnder: equal, or starts with "<path>\".
                parts.Add($"(n.path_norm = {p} OR substr(n.path_norm, 1, length({p}) + 1) = {p} || '\\')");
                binds.Add(c => c.Parameters.AddWithValue(p, prefix));
            }

            sb.Append(" AND (").Append(string.Join(" OR ", parts)).Append(')');
        }

        AppendRange(sb, binds, SqliteNoteStore.DueExpression, q.Due, "due");
        AppendRange(sb, binds, "n.keep_until", q.Keep, "keep");

        if (q.Ids is { } ids)
        {
            sb.Append(" AND n.id IN (SELECT value FROM json_each($ids))");
            var json = System.Text.Json.JsonSerializer.Serialize(ids.Select(StorageFormat.Id));
            binds.Add(c => c.Parameters.AddWithValue("$ids", json));
        }

        bind = c =>
        {
            foreach (var b in binds)
            {
                b(c);
            }
        };
        return sb.ToString();
    }

    public static string OrderBy(NoteSort sort) => sort switch
    {
        NoteSort.Title => "n.title COLLATE NOCASE, n.id",
        NoteSort.DueDate => $"({SqliteNoteStore.DueExpression}) IS NULL, {SqliteNoteStore.DueExpression}, n.title COLLATE NOCASE, n.id",
        // Hierarchical (03 § Repository rules, O-9): '\' sorts below every other character.
        NoteSort.Path => @"replace(n.path_norm, '\', char(1)), n.id",
        _ => "n.updated_at DESC, n.id",
    };

    private static void AppendRange(StringBuilder sb, List<Action<SqliteCommand>> binds, string expr, DateRange? range, string name)
    {
        if (range is null)
        {
            return;
        }

        sb.Append(CultureInfo.InvariantCulture, $" AND ({expr}) IS NOT NULL");
        if (range.From is { } from)
        {
            var p = "$" + name + "_from";
            sb.Append(CultureInfo.InvariantCulture, $" AND ({expr}) >= {p}");
            binds.Add(c => c.Parameters.AddWithValue(p, StorageFormat.Date(from)));
        }

        if (range.To is { } to)
        {
            var p = "$" + name + "_to";
            sb.Append(CultureInfo.InvariantCulture, $" AND ({expr}) <= {p}");
            binds.Add(c => c.Parameters.AddWithValue(p, StorageFormat.Date(to)));
        }
    }

    private static string Param(string prefix, int i) => "$" + prefix + i.ToString(CultureInfo.InvariantCulture);
}
