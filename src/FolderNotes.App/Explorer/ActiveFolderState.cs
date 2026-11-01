namespace FolderNotes.App.Explorer;

/// <summary>
/// What the tracker publishes (04 § ExplorerTracker design D.8): plain data, no COM objects, no bounds.
/// <c>FrameHwnd == 0</c> means "no Explorer folder is active" (<see cref="IsInactive"/>).
/// </summary>
internal readonly record struct ActiveFolderState(nint FrameHwnd, nint TabHwnd, string? Path, long Sequence)
{
    public bool IsInactive => FrameHwnd == 0;

    public static ActiveFolderState Inactive(long sequence = 0) => new(0, 0, null, sequence);

    /// <summary>
    /// The no-op test (04 D.6): same frame and same path (ordinal, ignoring case; null equals null). The tab
    /// and the sequence are deliberately not compared: two tabs on one folder show the same panel.
    /// </summary>
    public bool SameFolderAs(ActiveFolderState other) =>
        FrameHwnd == other.FrameHwnd && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
}
