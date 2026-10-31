namespace FolderNotes.App.Activation;

/// <summary>What a launch or a forwarded activation asks for (specs/02 § Command-line / activation contract).</summary>
internal abstract record ActivationCommand
{
    private ActivationCommand()
    {
    }

    /// <summary>No args: open (or bring forward) the main window.</summary>
    public sealed record ShowMain : ActivationCommand;

    /// <summary><c>--background</c>: tray only, no window.</summary>
    public sealed record Background : ActivationCommand;

    /// <summary><c>--edit "&lt;folderPath&gt;"</c> (REQ-003/004).</summary>
    public sealed record Edit(string FolderPath) : ActivationCommand;

    /// <summary><c>--search [query]</c>; the query may be empty.</summary>
    public sealed record Search(string Query) : ActivationCommand;

    /// <summary><c>--open-note &lt;noteId&gt;</c>.</summary>
    public sealed record OpenNote(Guid NoteId) : ActivationCommand;

    /// <summary>Unparseable args. The router logs <see cref="Reason"/> and treats it as <see cref="ShowMain"/>.</summary>
    public sealed record Invalid(string Reason) : ActivationCommand;
}
