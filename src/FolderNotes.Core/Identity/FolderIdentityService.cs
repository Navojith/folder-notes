using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Paths;
using Microsoft.Win32.SafeHandles;

namespace FolderNotes.Core.Identity;

/// <summary>
/// Win32 implementation of folder identity, from spike S4 (specs/03-data-model.md, ADR-009).
/// Thread-safe. Never writes to the file system.
/// </summary>
public sealed class FolderIdentityService : IFolderIdentityService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, string> _volumeRoots = [];

    public FolderRef Resolve(string path) =>
        TryResolve(path, out var folder) ? folder : throw new ArgumentException($"Not a folder path: '{path}'.", nameof(path));

    public bool TryResolve(string path, [NotNullWhen(true)] out FolderRef? folder)
    {
        folder = null;
        if (!PathNormalizer.TryNormalizeDisplay(path, out var display, NativeMethods.GetMappedDriveTarget))
        {
            return false;
        }

        using var handle = NativeMethods.OpenForAttributes(display);
        if (handle is null)
        {
            // Doesn't exist (or no access): string normalization only, tracked by path.
            if (File.Exists(display))
            {
                return false;
            }

            folder = FromPath(display, NativeMethods.GetVolumeKind(display), null, null);
            return true;
        }

        if (!NativeMethods.IsDirectory(handle))
        {
            return false;
        }

        // Canonical spelling: resolves subst drives, 8.3 names, \\?\ and casing (S4).
        var canonical = NativeMethods.GetFinalPath(handle);
        if (canonical is not null && PathNormalizer.TryNormalizeDisplay(canonical, out var canonicalDisplay))
        {
            display = canonicalDisplay;
        }

        var kind = NativeMethods.GetVolumeKind(display);
        string? serial = null;
        string? fileId = null;
        if (kind == VolumeKind.Fixed && NativeMethods.HasStableFileIds(handle) && NativeMethods.TryGetFileId(handle, out var vol, out var lo, out var hi) && (lo | hi) != 0)
        {
            serial = FormatSerial(vol);
            fileId = FormatFileId(lo, hi);
            RememberVolume(vol, display);
        }

        folder = FromPath(display, kind, serial, fileId);
        return true;
    }

    public LocateResult Locate(FolderRef storedRef)
    {
        ArgumentNullException.ThrowIfNull(storedRef);

        if (!storedRef.HasFileId || !TryParseIds(storedRef, out var vol, out var lo, out var hi))
        {
            // Tracked by path only (REQ-062).
            return Directory.Exists(storedRef.PathDisplay)
                ? Classify(storedRef.PathDisplay)
                : LocateResult.NotFound;
        }

        foreach (var root in CandidateRoots(vol, storedRef.PathDisplay))
        {
            using var hint = NativeMethods.OpenForAttributes(root);
            if (hint is null || !NativeMethods.TryGetFileId(hint, out var rootVol, out _, out _) || rootVol != vol)
            {
                continue;
            }

            RememberVolume(vol, root);
            using var h = NativeMethods.OpenById(hint, lo, hi);
            if (h is null || !NativeMethods.IsDirectory(h))
            {
                return LocateResult.NotFound; // right volume, ID gone: deleted
            }

            var final = NativeMethods.GetFinalPath(h);
            return final is not null && PathNormalizer.TryNormalizeDisplay(final, out var display)
                ? Classify(display)
                : LocateResult.NotFound;
        }

        return LocateResult.VolumeNotMounted; // no mounted volume has this serial (ADR-020)
    }

    internal static string FormatSerial(ulong volumeSerial) => volumeSerial.ToString("X16", CultureInfo.InvariantCulture);

    internal static string FormatFileId(ulong lo, ulong hi) =>
        hi.ToString("X16", CultureInfo.InvariantCulture) + lo.ToString("X16", CultureInfo.InvariantCulture);

    internal static bool TryParseIds(FolderRef r, out ulong vol, out ulong lo, out ulong hi)
    {
        vol = lo = hi = 0;
        return r.VolumeSerial is { Length: 16 } s && r.FileId is { Length: 32 } f
            && ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vol)
            && ulong.TryParse(f.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hi)
            && ulong.TryParse(f.AsSpan(16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out lo);
    }

    internal static bool IsInRecycleBin(string displayPath) =>
        displayPath.Contains(@"\$RECYCLE.BIN\", StringComparison.OrdinalIgnoreCase)
        || displayPath.EndsWith(@"\$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase);

    private static LocateResult Classify(string displayPath) =>
        new(IsInRecycleBin(displayPath) ? LocateStatus.InRecycleBin : LocateStatus.Found, displayPath);

    private static FolderRef FromPath(string display, VolumeKind kind, string? serial, string? fileId) =>
        new(display.ToUpperInvariant(), display, serial, fileId, kind);

    private void RememberVolume(ulong vol, string anyPath)
    {
        var root = Path.GetPathRoot(anyPath);
        if (root is null)
        {
            return;
        }

        lock (_gate)
        {
            _volumeRoots[vol] = root;
        }
    }

    private IEnumerable<string> CandidateRoots(ulong vol, string storedPath)
    {
        string? cached;
        lock (_gate)
        {
            _volumeRoots.TryGetValue(vol, out cached);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { cached, Path.GetPathRoot(storedPath) }.Concat(NativeMethods.FixedDriveRoots()))
        {
            if (root is not null && seen.Add(root))
            {
                yield return root.EndsWith('\\') ? root : root + @"\";
            }
        }
    }
}
