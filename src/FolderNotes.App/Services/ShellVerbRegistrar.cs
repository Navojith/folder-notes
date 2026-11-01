using Microsoft.Win32;

namespace FolderNotes.App.Services;

internal enum ShellVerbState
{
    /// <summary>Neither verb key exists.</summary>
    Off,

    /// <summary>Both keys exist and match what this exe would write.</summary>
    On,

    /// <summary>Our keys exist but differ (another exe, a missing value, or only one key).</summary>
    Stale,
}

/// <summary>The Explorer "Folder note…" verbs (04 § 4.1, ADR-024). The registry is the truth; nothing is stored.</summary>
internal interface IShellVerbRegistrar
{
    ShellVerbState GetState();

    /// <summary>Writes both keys for this exe. Also the repair for a stale registration.</summary>
    void Enable();

    /// <summary>Deletes our <c>FolderNotes</c> keys. Nothing else is touched.</summary>
    void Disable();

    /// <summary>The exe the registered command points at, when there is one (for the Stale message).</summary>
    string? RegisteredExe();
}

/// <summary>
/// <c>HKCU\Software\Classes\Directory\shell\FolderNotes</c> and <c>…\Directory\Background\shell\FolderNotes</c>.
/// Tests pass a scratch <paramref name="classesRoot"/>.
/// </summary>
internal sealed class RegistryShellVerbRegistrar(string exePath, string classesRoot = RegistryShellVerbRegistrar.ClassesRoot)
    : IShellVerbRegistrar
{
    public const string ClassesRoot = @"Software\Classes";
    public const string VerbName = "FolderNotes";
    public const string Label = "Folder note…";

    private static readonly string[] s_parents = [@"Directory\shell", @"Directory\Background\shell"];

    private readonly string _exePath = string.IsNullOrWhiteSpace(exePath) ? throw new ArgumentException("No exe path.", nameof(exePath)) : exePath;

    /// <summary>What the command value is: the quoted exe, <c>--edit</c> and the quoted folder (<c>%V</c>).</summary>
    public string Command => "\"" + _exePath + "\" --edit \"%V\"";

    public string IconValue => "\"" + _exePath + "\",0";

    public ShellVerbState GetState()
    {
        var present = 0;
        var matching = 0;
        foreach (var parent in s_parents)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath(parent));
            if (key is null)
            {
                continue;
            }

            present++;
            using var command = key.OpenSubKey("command");
            if (key.GetValue(null) is string label && label == Label
                && key.GetValue("Icon") is string icon && string.Equals(icon, IconValue, StringComparison.OrdinalIgnoreCase)
                && command?.GetValue(null) is string cmd && string.Equals(cmd, Command, StringComparison.OrdinalIgnoreCase))
            {
                matching++;
            }
        }

        return present == 0 ? ShellVerbState.Off
            : matching == s_parents.Length ? ShellVerbState.On
            : ShellVerbState.Stale;
    }

    public void Enable()
    {
        foreach (var parent in s_parents)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath(parent), writable: true);
            key.SetValue(null, Label, RegistryValueKind.String);
            key.SetValue("Icon", IconValue, RegistryValueKind.String);
            using var command = key.CreateSubKey("command", writable: true);
            command.SetValue(null, Command, RegistryValueKind.String);
        }
    }

    public void Disable()
    {
        foreach (var parent in s_parents)
        {
            using var shell = Registry.CurrentUser.OpenSubKey(classesRoot + @"\" + parent, writable: true);
            shell?.DeleteSubKeyTree(VerbName, throwOnMissingSubKey: false);
        }
    }

    public string? RegisteredExe()
    {
        foreach (var parent in s_parents)
        {
            using var command = Registry.CurrentUser.OpenSubKey(KeyPath(parent) + @"\command");
            if (command?.GetValue(null) is string cmd && cmd.StartsWith('"'))
            {
                var end = cmd.IndexOf('"', 1);
                if (end > 1)
                {
                    return cmd[1..end];
                }
            }
        }

        return null;
    }

    private string KeyPath(string parent) => classesRoot + @"\" + parent + @"\" + VerbName;
}

/// <summary>
/// <c>--register-shell</c> / <c>--unregister-shell</c>: explicit, headless switches for the installer (ADR-024).
/// Handled before anything else in <c>Main</c>; nothing in the app calls them by itself.
/// </summary>
internal static class ShellVerbCommand
{
    public static bool TryHandle(IReadOnlyList<string> args, IShellVerbRegistrar registrar, out int exitCode)
    {
        exitCode = 0;
        if (args.Count != 1)
        {
            return false;
        }

        bool enable;
        if (string.Equals(args[0], "--register-shell", StringComparison.OrdinalIgnoreCase))
        {
            enable = true;
        }
        else if (string.Equals(args[0], "--unregister-shell", StringComparison.OrdinalIgnoreCase))
        {
            enable = false;
        }
        else
        {
            return false;
        }

        try
        {
            if (enable)
            {
                registrar.Enable();
            }
            else
            {
                registrar.Disable();
            }
        }
#pragma warning disable CA1031 // The installer only needs the exit code.
        catch (Exception)
#pragma warning restore CA1031
        {
            exitCode = 1;
        }

        return true;
    }
}
