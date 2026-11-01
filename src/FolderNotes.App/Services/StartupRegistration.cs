using Microsoft.Win32;

namespace FolderNotes.App.Services;

/// <summary>"Start with Windows" (REQ-080, ADR-022). The registration itself is the truth; nothing is stored.</summary>
internal interface IStartupRegistration
{
    /// <summary>An entry exists (for this exe or another copy of it).</summary>
    bool IsEnabled { get; }

    void Enable();

    void Disable();

    /// <summary>An existing entry that points elsewhere is rewritten to this exe. True when it changed something.</summary>
    bool RepairPath();
}

/// <summary>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, value <c>FolderNotes</c> =
/// <c>"&lt;exe&gt;" --background</c> (02 § Settings). Tests pass a scratch key path.
/// </summary>
internal sealed class RunKeyStartupRegistration(string exePath, string keyPath = RunKeyStartupRegistration.RunKey, string valueName = "FolderNotes")
    : IStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _exePath = string.IsNullOrWhiteSpace(exePath) ? throw new ArgumentException("No exe path.", nameof(exePath)) : exePath;

    /// <summary>What's written: the quoted exe, started in the tray only.</summary>
    public string Command => "\"" + _exePath + "\" --background";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string s && s.Length > 0;
        }
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, Command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public bool RepairPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue(valueName) is not string current || current.Length == 0
            || string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        key.SetValue(valueName, Command, RegistryValueKind.String);
        return true;
    }
}
