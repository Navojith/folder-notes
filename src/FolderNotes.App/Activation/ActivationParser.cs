using System.Runtime.InteropServices;

namespace FolderNotes.App.Activation;

/// <summary>
/// Turns command-line tokens into an <see cref="ActivationCommand"/> (specs/02 § Command-line /
/// activation contract, "Resolved in T2.1"). Pure apart from <see cref="SplitCommandLine"/>.
/// </summary>
internal static partial class ActivationParser
{
    /// <summary>Parses the tokens after the program name (what <c>Main(string[] args)</c> receives).</summary>
    public static ActivationCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new ActivationCommand.ShowMain();
        }

        if (!TryGetSwitch(args[0], out var name))
        {
            return new ActivationCommand.Invalid($"Expected a switch but got '{args[0]}'.");
        }

        var rest = args.Skip(1).ToList();
        switch (name)
        {
            case "background":
                return rest.Count == 0 || IsSwitch(rest[0])
                    ? new ActivationCommand.Background()
                    : new ActivationCommand.Invalid($"--background takes no value but got '{rest[0]}'.");

            case "edit":
                return rest.Count > 0 && !string.IsNullOrWhiteSpace(rest[0]) && !IsSwitch(rest[0])
                    ? new ActivationCommand.Edit(RepairQuotedRoot(rest[0]))
                    : new ActivationCommand.Invalid("--edit needs a folder path.");

            case "search":
                // Every remaining token is the query, so `--search label:client acme` works unquoted.
                return new ActivationCommand.Search(string.Join(' ', rest.Where(t => t.Length > 0)).Trim());

            case "open-note":
                return rest.Count > 0 && Guid.TryParse(rest[0], out var id)
                    ? new ActivationCommand.OpenNote(id)
                    : new ActivationCommand.Invalid("--open-note needs a note ID (GUID).");

            default:
                return new ActivationCommand.Invalid($"Unknown switch '{args[0]}'.");
        }
    }

    /// <summary>
    /// Parses a whole command-line string, as forwarded by <c>AppInstance</c> redirection. The first
    /// token is dropped when it is the executable (same file name as <paramref name="exePath"/>).
    /// </summary>
    public static ActivationCommand ParseCommandLine(string? commandLine, string exePath)
    {
        var tokens = SplitCommandLine(commandLine ?? string.Empty);
        if (tokens.Count > 0 && IsExecutable(tokens[0], exePath))
        {
            tokens.RemoveAt(0);
        }

        return Parse(tokens);
    }

    /// <summary>Splits with the Windows rules (<c>CommandLineToArgvW</c>).</summary>
    public static List<string> SplitCommandLine(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        // CommandLineToArgvW treats an empty string as "the current exe", so whitespace is handled above.
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var result = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty);
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    /// <summary>
    /// <c>"%V"</c> for a drive root is <c>"D:\"</c>, which Windows splits as <c>D:"</c> (backslash
    /// escapes the quote). A Windows path can't contain <c>"</c>, so a trailing one is always this.
    /// </summary>
    /// The forwarded <c>Arguments</c> string also ends with a space, which lands inside the open
    /// quote; Windows names can't end in a space either, so it is trimmed first.
    private static string RepairQuotedRoot(string path)
    {
        var trimmed = path.TrimEnd();
        return trimmed.EndsWith('"') ? trimmed.TrimEnd('"') + "\\" : path;
    }

    private static bool IsExecutable(string token, string exePath)
    {
        if (IsSwitch(token))
        {
            return false;
        }

        string fileName;
        try
        {
            fileName = Path.GetFileName(token);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return fileName.Equals(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSwitch(string token) => TryGetSwitch(token, out _);

    private static bool TryGetSwitch(string token, out string name)
    {
        name = string.Empty;
        string body;
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            body = token[2..];
        }
        else if (token.StartsWith('/') && token.Length > 1 && !token.Contains('\\', StringComparison.Ordinal))
        {
            body = token[1..];
        }
        else
        {
            return false;
        }

        if (body.Length == 0)
        {
            return false;
        }

        name = body.ToLowerInvariant();
        return true;
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
