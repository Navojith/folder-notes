using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FolderNotes.App.Services;

/// <summary>
/// A global shortcut: <c>RegisterHotKey</c> modifiers + virtual key (03 § Settings, 05 § Settings
/// page). Storage form "Ctrl+Alt+N"; display form "Ctrl + Alt + N".
/// </summary>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    private const uint AllModifiers = ModAlt | ModControl | ModShift | ModWin;

    /// <summary>REQ-050's default, Ctrl+Alt+N.</summary>
    public static HotkeyGesture Default { get; } = new(ModControl | ModAlt, 0x4E);

    /// <summary>Has Ctrl, Alt or Win (Shift alone isn't enough), no other bits, and an allowed key.</summary>
    public bool IsValid => (Modifiers & ~AllModifiers) == 0 && (Modifiers & (ModControl | ModAlt | ModWin)) != 0 && KeyName(VirtualKey) is not null;

    public string DisplayText => string.Join(" + ", Parts());

    public override string ToString() => string.Join("+", Parts());

    /// <summary>A key press while capturing (05): null when it isn't a usable shortcut.</summary>
    public static HotkeyGesture? FromKeyPress(uint modifiers, uint virtualKey)
    {
        var g = new HotkeyGesture(modifiers & AllModifiers, virtualKey);
        return g.IsValid ? g : null;
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(p => p.Length == 0))
        {
            return false;
        }

        uint mods = 0;
        foreach (var p in parts[..^1])
        {
            var bit = p.ToUpperInvariant() switch
            {
                "CTRL" => ModControl,
                "ALT" => ModAlt,
                "SHIFT" => ModShift,
                "WIN" => ModWin,
                _ => 0u,
            };
            if (bit == 0 || (mods & bit) != 0)
            {
                return false;
            }

            mods |= bit;
        }

        if (KeyFromName(parts[^1]) is not { } vk)
        {
            return false;
        }

        var g = new HotkeyGesture(mods, vk);
        if (!g.IsValid)
        {
            return false;
        }

        gesture = g;
        return true;
    }

    private IEnumerable<string> Parts()
    {
        if ((Modifiers & ModControl) != 0)
        {
            yield return "Ctrl";
        }

        if ((Modifiers & ModAlt) != 0)
        {
            yield return "Alt";
        }

        if ((Modifiers & ModShift) != 0)
        {
            yield return "Shift";
        }

        if ((Modifiers & ModWin) != 0)
        {
            yield return "Win";
        }

        yield return KeyName(VirtualKey) ?? "0x" + VirtualKey.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>A–Z, 0–9, F1–F24 and Space (03 § Settings); null for anything else.</summary>
    internal static string? KeyName(uint vk) => vk switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F).ToString(CultureInfo.InvariantCulture),
        0x20 => "Space",
        _ => null,
    };

    private static uint? KeyFromName(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (string.Equals(name, "Space", StringComparison.OrdinalIgnoreCase))
        {
            return 0x20;
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f')
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
        {
            return (uint)(0x6F + n);
        }

        return null;
    }
}

/// <summary>Who owns the global hotkey (the tray window, ADR-019). 02 § Settings.</summary>
internal interface IHotkeyHost
{
    /// <summary>The shortcut that's registered now, or null (none, or registration failed).</summary>
    HotkeyGesture? Registered { get; }

    /// <summary>Registers <paramref name="gesture"/>. False when another app owns it; the previous one is registered again.</summary>
    bool TryChange(HotkeyGesture gesture);
}
