using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace FolderNotes.Core.Paths;

/// <summary>Resolves a mapped network drive letter to its UNC target (<c>\\server\share[\…]</c>), or null.</summary>
public delegate string? MappedDriveResolver(char driveLetter);

/// <summary>
/// The string normalizer for folder paths (REQ-063). Rules in specs/03-data-model.md
/// § String normalization rules. Pure apart from the optional mapped-drive resolver.
/// The C++ shell extension implements the same rules (shared vectors).
/// </summary>
public static class PathNormalizer
{
    /// <summary>The <c>path_norm</c> compare key, or throws <see cref="ArgumentException"/> if the path is invalid.</summary>
    public static string Normalize(string path, MappedDriveResolver? mappedDrives = null) =>
        TryNormalize(path, out var norm, mappedDrives) ? norm : throw new ArgumentException($"Not an absolute folder path: '{path}'.", nameof(path));

    public static bool TryNormalize(string? path, [NotNullWhen(true)] out string? norm, MappedDriveResolver? mappedDrives = null)
    {
        norm = Build(path, mappedDrives)?.ToUpperInvariant();
        return norm is not null;
    }

    /// <summary>The <c>path_display</c> spelling: the same rules, original casing, upper-case drive letter.</summary>
    public static bool TryNormalizeDisplay(string? path, [NotNullWhen(true)] out string? display, MappedDriveResolver? mappedDrives = null)
    {
        display = Build(path, mappedDrives);
        return display is not null;
    }

    public static string NormalizeDisplay(string path, MappedDriveResolver? mappedDrives = null) =>
        TryNormalizeDisplay(path, out var d, mappedDrives) ? d : throw new ArgumentException($"Not an absolute folder path: '{path}'.", nameof(path));

    /// <summary>True if <paramref name="childNorm"/> is <paramref name="folderNorm"/> or inside it. Both are <c>path_norm</c> keys.</summary>
    public static bool IsSameOrUnder(string childNorm, string folderNorm)
    {
        ArgumentNullException.ThrowIfNull(childNorm);
        ArgumentNullException.ThrowIfNull(folderNorm);
        return childNorm.Length == folderNorm.Length
            ? string.Equals(childNorm, folderNorm, StringComparison.Ordinal)
            : childNorm.Length > folderNorm.Length
              && childNorm[folderNorm.Length] == '\\'
              && childNorm.StartsWith(folderNorm, StringComparison.Ordinal);
    }

    /// <summary>Parent folder key, or null at a root.</summary>
    public static string? Parent(string norm)
    {
        ArgumentNullException.ThrowIfNull(norm);
        var i = norm.LastIndexOf('\\');
        if (i <= 1)
        {
            return null; // "C:" or "\\..." with nothing to strip
        }

        var parent = norm[..i];
        // A UNC share root "\\SERVER\SHARE" has no parent; "\\SERVER" alone isn't a folder.
        return parent.StartsWith(@"\\", StringComparison.Ordinal) && parent.Count(c => c == '\\') < 3 ? null : parent;
    }

    private static string? Build(string? input, MappedDriveResolver? mappedDrives)
    {
        if (input is null)
        {
            return null;
        }

        var s = input.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            s = s[1..^1].Trim();
        }

        if (s.Length == 0)
        {
            return null;
        }

        s = s.Replace('/', '\\');

        var literal = false;
        string root;
        string rest;

        if (StartsWithAny(s, @"\\?\UNC\", @"\\.\UNC\"))
        {
            literal = s[2] == '?';
            s = @"\\" + s[8..];
            if (!TryUncRoot(s, out root!, out rest!))
            {
                return null;
            }
        }
        else if (StartsWithAny(s, @"\\?\", @"\\.\"))
        {
            literal = s[2] == '?';
            var after = s[4..];
            if (IsDriveStart(after))
            {
                if (!TryDriveRoot(after, mappedDrives, out root!, out rest!))
                {
                    return null;
                }
            }
            else
            {
                // Device / volume GUID path: keep the prefix + first segment as the root.
                var segEnd = after.IndexOf('\\', StringComparison.Ordinal);
                var first = segEnd < 0 ? after : after[..segEnd];
                if (first.Length == 0)
                {
                    return null;
                }

                root = s[..4] + first;
                rest = segEnd < 0 ? "" : after[segEnd..];
            }
        }
        else if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (!TryUncRoot(s, out root!, out rest!))
            {
                return null;
            }
        }
        else if (IsDriveStart(s))
        {
            if (!TryDriveRoot(s, mappedDrives, out root!, out rest!))
            {
                return null;
            }
        }
        else
        {
            return null; // relative, or rooted without a drive
        }

        var segments = new List<string>();
        foreach (var raw in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw;
            if (!literal)
            {
                if (seg == ".")
                {
                    continue;
                }

                if (seg == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }

                seg = seg.TrimEnd('.', ' ');
                if (seg.Length == 0)
                {
                    continue;
                }
            }

            segments.Add(seg);
        }

        var sb = new StringBuilder(root, root.Length + rest.Length);
        foreach (var seg in segments)
        {
            sb.Append('\\').Append(seg);
        }

        return sb.ToString();
    }

    private static bool StartsWithAny(string s, string a, string b) =>
        s.StartsWith(a, StringComparison.OrdinalIgnoreCase) || s.StartsWith(b, StringComparison.OrdinalIgnoreCase);

    private static bool IsDriveStart(string s) => s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':';

    private static bool TryDriveRoot(string s, MappedDriveResolver? mappedDrives, out string? root, out string? rest)
    {
        root = rest = null;
        if (s.Length > 2 && s[2] != '\\')
        {
            return false; // drive-relative "C:foo"
        }

        var letter = char.ToUpperInvariant(s[0]);
        rest = s[2..];
        var unc = mappedDrives?.Invoke(letter);
        if (!string.IsNullOrWhiteSpace(unc))
        {
            // The UNC target may itself carry a sub-path (\\server\share\sub): normalize it as a prefix.
            var target = Build(unc, null);
            if (target is null || !target.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            root = target;
            return true;
        }

        root = letter + ":";
        return true;
    }

    private static bool TryUncRoot(string s, out string? root, out string? rest)
    {
        root = rest = null;
        var parts = s[2..].Split('\\');
        var nonEmpty = new List<(string Text, int Index)>();
        for (var i = 0; i < parts.Length && nonEmpty.Count < 2; i++)
        {
            if (parts[i].Length > 0)
            {
                nonEmpty.Add((parts[i], i));
            }
        }

        if (nonEmpty.Count < 2)
        {
            return false;
        }

        var server = nonEmpty[0].Text.TrimEnd('.', ' ');
        var share = nonEmpty[1].Text.TrimEnd('.', ' ');
        if (server.Length == 0 || share.Length == 0)
        {
            return false;
        }

        root = @"\\" + server + @"\" + share;
        rest = string.Join('\\', parts.Skip(nonEmpty[1].Index + 1));
        rest = rest.Length == 0 ? "" : @"\" + rest;
        return true;
    }
}
