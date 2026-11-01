using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;

namespace FolderNotes.Core.Storage;

/// <summary>Where a note's folder is, judged from the file system alone (03 § Orphan detection).</summary>
public enum FolderState
{
    /// <summary>At the stored path.</summary>
    Present,

    /// <summary>Followed by file ID to another path (renamed or moved on the same volume).</summary>
    Moved,

    /// <summary>The original is gone and a folder exists at the stored path on the same volume (ADR-009).</summary>
    Recreated,

    /// <summary>The original is in the Recycle Bin and nothing is at the stored path.</summary>
    InRecycleBin,

    /// <summary>The drive (or share) isn't available; the folder may come back.</summary>
    DriveUnavailable,

    /// <summary>Deleted, or moved where its ID can't follow (another drive).</summary>
    Missing,
}

/// <param name="State">The classification.</param>
/// <param name="Current">The folder to bind to (<see cref="FolderState.Present"/>, <see cref="FolderState.Moved"/>, <see cref="FolderState.Recreated"/>).</param>
/// <param name="Path">Where the folder is now: the new path, or the Recycle Bin path.</param>
public sealed record FolderCheck(FolderState State, FolderRef? Current = null, string? Path = null);

public enum OrphanOutcome
{
    /// <summary>Nothing to change.</summary>
    Unchanged,

    /// <summary>The orphan flag was cleared: the folder is back.</summary>
    Recovered,

    /// <summary>Re-bound to the folder's new path (or the recreated folder).</summary>
    Relinked,

    /// <summary>Newly marked orphaned.</summary>
    Orphaned,

    /// <summary>
    /// The folder the note should move to already has another live note. Neither note changed except
    /// that this one is (or stays) orphaned (ADR-020).
    /// </summary>
    Conflict,

    /// <summary>The note changed (relinked, deleted) between the read and the write; the next check sees the new state.</summary>
    Skipped,
}

public sealed record OrphanCheckResult(Guid NoteId, FolderCheck Check, OrphanOutcome Outcome, Guid? ConflictingNoteId = null);

public sealed record OrphanScanReport(int Checked, int Relinked, int Orphaned, int Recovered, int Conflicts = 0, int Skipped = 0);

/// <summary>
/// Orphan detection (03 § Orphan detection, REQ-060/061, ADR-020). <see cref="Classify"/> only reads
/// the file system; <see cref="CheckAsync"/> applies a result with writes that are conditional on the
/// binding the note had when it was read, so it never overwrites a relink made in between. It never
/// changes a note's content, labels, dates or <c>updated_at</c>. Scheduling lives in the app.
/// </summary>
public sealed class OrphanScanner
{
    private readonly INoteRepository _repository;
    private readonly IFolderIdentityService _identity;
    private readonly Func<string, bool> _directoryExists;

    public OrphanScanner(INoteRepository repository, IFolderIdentityService identity, Func<string, bool>? directoryExists = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    /// <summary>Every live note (orphaned ones included).</summary>
    public async Task<OrphanScanReport> ScanAsync(CancellationToken ct = default)
    {
        var notes = await _repository.ListAsync(new NoteQuery { State = NoteListState.Active, Sort = NoteSort.Path }, ct).ConfigureAwait(false);
        int relinked = 0, orphaned = 0, recovered = 0, conflicts = 0, skipped = 0;
        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            switch ((await CheckAsync(note, ct).ConfigureAwait(false)).Outcome)
            {
                case OrphanOutcome.Relinked: relinked++; break;
                case OrphanOutcome.Orphaned: orphaned++; break;
                case OrphanOutcome.Recovered: recovered++; break;
                case OrphanOutcome.Conflict: conflicts++; break;
                case OrphanOutcome.Skipped: skipped++; break;
                default: break;
            }
        }

        return new OrphanScanReport(notes.Count, relinked, orphaned, recovered, conflicts, skipped);
    }

    /// <summary>Classifies one note and applies the result (03 § Orphan detection).</summary>
    public async Task<OrphanCheckResult> CheckAsync(Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        var check = Classify(note);
        var expected = note.Folder;
        try
        {
            switch (check.State)
            {
                case FolderState.Present when check.Current is { } here && here.PathDisplay != expected.PathDisplay:
                    // Same folder, new spelling (a case-only rename): refresh the display path.
                    await _repository.RelinkAsync(note.Id, here, expected, ct).ConfigureAwait(false);
                    return new(note.Id, check, note.IsOrphaned ? OrphanOutcome.Recovered : OrphanOutcome.Unchanged);

                case FolderState.Present:
                    return new(note.Id, check, note.IsOrphaned && await _repository.SetOrphanedAsync(note.Id, false, expected, ct).ConfigureAwait(false)
                        ? OrphanOutcome.Recovered
                        : OrphanOutcome.Unchanged);

                case FolderState.Moved or FolderState.Recreated:
                    try
                    {
                        await _repository.RelinkAsync(note.Id, check.Current!, expected, ct).ConfigureAwait(false);
                        return new(note.Id, check, OrphanOutcome.Relinked);
                    }
                    catch (DuplicateFolderNoteException ex)
                    {
                        // Another note has that folder. Take nothing over; leave this one orphaned (ADR-020).
                        await _repository.SetOrphanedAsync(note.Id, true, expected, ct).ConfigureAwait(false);
                        return new(note.Id, check, OrphanOutcome.Conflict, ex.ExistingNoteId);
                    }

                default:
                    return new(note.Id, check, await _repository.SetOrphanedAsync(note.Id, true, expected, ct).ConfigureAwait(false)
                        ? OrphanOutcome.Orphaned
                        : OrphanOutcome.Unchanged);
            }
        }
        catch (Exception ex) when (ex is NoteBindingChangedException or KeyNotFoundException)
        {
            return new(note.Id, check, OrphanOutcome.Skipped);
        }
    }

    /// <summary>Where the note's folder is now. Reads the file system only.</summary>
    public FolderCheck Classify(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var stored = note.Folder;
        var loc = _identity.Locate(stored);

        if (loc is { Status: LocateStatus.Found, Path: { } path })
        {
            if (!_identity.TryResolve(path, out var now))
            {
                return new(FolderState.Missing);
            }

            if (!stored.HasFileId)
            {
                return new(FolderState.Present, now with { VolumeSerial = null, FileId = null }); // stays path-only (REQ-062)
            }

            // What's at that path must be the very folder we followed; anything else (a race with
            // another move) counts as missing until the next check.
            if (!SameVolume(now, stored) || !string.Equals(now.FileId, stored.FileId, StringComparison.Ordinal))
            {
                return new(FolderState.Missing);
            }

            return string.Equals(now.PathNorm, stored.PathNorm, StringComparison.Ordinal)
                ? new(FolderState.Present, now, now.PathDisplay)
                : new(FolderState.Moved, now, now.PathDisplay);
        }

        if (loc.Status == LocateStatus.VolumeNotMounted)
        {
            return new(FolderState.DriveUnavailable);
        }

        // The ID is gone or in the Recycle Bin. A folder at the stored path on the same volume was
        // recreated there (ADR-009); on another volume it's somebody else's folder (ADR-020).
        if (stored.HasFileId && _directoryExists(stored.PathDisplay) && _identity.TryResolve(stored.PathDisplay, out var atPath)
            && atPath.HasFileId && SameVolume(atPath, stored))
        {
            return string.Equals(atPath.FileId, stored.FileId, StringComparison.Ordinal)
                ? new(FolderState.Present, atPath, atPath.PathDisplay)
                : new(FolderState.Recreated, atPath, atPath.PathDisplay);
        }

        if (loc.Status == LocateStatus.InRecycleBin)
        {
            return new(FolderState.InRecycleBin, null, loc.Path);
        }

        if (!stored.HasFileId || !_directoryExists(stored.PathDisplay))
        {
            // Path-only notes (network, removable) and drives that are gone: is the root there at all?
            var root = Path.GetPathRoot(stored.PathDisplay);
            if (string.IsNullOrEmpty(root) || !_directoryExists(root))
            {
                return new(FolderState.DriveUnavailable);
            }
        }

        return new(FolderState.Missing);
    }

    private static bool SameVolume(FolderRef a, FolderRef b) => string.Equals(a.VolumeSerial, b.VolumeSerial, StringComparison.Ordinal);
}
