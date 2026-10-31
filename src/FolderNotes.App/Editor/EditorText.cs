namespace FolderNotes.App.Editor;

/// <summary>Text clean-up between WinUI controls and the note model.</summary>
internal static class EditorText
{
    /// <summary>
    /// A WinUI TextBox reports line breaks as a bare CR; notes are stored with LF so search,
    /// export and tooltips see normal text (05 § Editor behaviour, Fields).
    /// </summary>
    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
