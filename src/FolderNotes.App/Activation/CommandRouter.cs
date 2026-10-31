using Serilog;

namespace FolderNotes.App.Activation;

/// <summary>The window-level actions a command can trigger. Implemented by <see cref="AppShell"/>.</summary>
internal interface IAppShell
{
    void ShowMainWindow();

    void OpenEditor(string folderPath);

    void OpenSearch(string query);

    void OpenNote(Guid noteId);
}

/// <summary>Where an activation came from; only affects logging and <c>--background</c>.</summary>
internal enum ActivationSource
{
    /// <summary>This process's own command line.</summary>
    Startup,

    /// <summary>A second launch redirected to this instance.</summary>
    Redirected,

    /// <summary>The tray icon or its menu.</summary>
    Tray,
}

/// <summary>Runs an <see cref="ActivationCommand"/> against the <see cref="IAppShell"/> (specs/02).</summary>
internal sealed class CommandRouter(IAppShell shell, ILogger log)
{
    private readonly IAppShell _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    private readonly ILogger _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<CommandRouter>();

    public void Route(ActivationCommand command, ActivationSource source)
    {
        ArgumentNullException.ThrowIfNull(command);
        _log.Information("Activation {Source}: {Command}", source, command);

        switch (command)
        {
            case ActivationCommand.Background:
                // Startup: tray only. Already running (e.g. the login entry fires twice): nothing to do.
                break;
            case ActivationCommand.Edit edit:
                _shell.OpenEditor(edit.FolderPath);
                break;
            case ActivationCommand.Search search:
                _shell.OpenSearch(search.Query);
                break;
            case ActivationCommand.OpenNote open:
                _shell.OpenNote(open.NoteId);
                break;
            case ActivationCommand.Invalid invalid:
                _log.Warning("Ignoring invalid activation args: {Reason}", invalid.Reason);
                _shell.ShowMainWindow();
                break;
            default:
                _shell.ShowMainWindow();
                break;
        }
    }
}
