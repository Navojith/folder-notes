using System.Text;

namespace FolderNotes.Core.Domain;

/// <summary>
/// Length limits in REQ-002 count Unicode characters (scalar values). That is what SQLite's
/// <c>length()</c> counts in the <c>CHECK</c> constraint, so an emoji counts as one, not two UTF-16 units.
/// </summary>
public static class TextLength
{
    public static int Of(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var n = 0;
        foreach (var _ in s.EnumerateRunes())
        {
            n++;
        }

        return n;
    }

    public static string Truncate(string s, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (n++ == maxChars)
            {
                break;
            }

            sb.Append(r.ToString());
        }

        return sb.ToString();
    }
}
