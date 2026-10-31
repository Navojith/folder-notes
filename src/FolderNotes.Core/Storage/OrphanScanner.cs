using FolderNotes.Core.Identity;

namespace FolderNotes.Core.Storage;

public sealed record OrphanScanReport(int Checked, int Relinked, int Orphaned, int Recovered);

/// <summary>
/// The orphan detection pass (03 § Orphan detection, REQ-061). It runs at startup, every 6 h and on
/// demand (the scheduling lives in the app).
/// </summary>
public sealed class OrphanScanner(INoteRepository repository, IFolderIdentityService identity)
{
    public async Task<OrphanScanReport> ScanAsync(CancellationToken ct = default)
    {
        var notes = await repository.ListAsync(new NoteQuery { State = NoteListState.Active, Sort = NoteSort.Path }, ct).ConfigureAwait(false);
        int relinked = 0, orphaned = 0, recovered = 0;

        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            var loc = identity.Locate(note.Folder);

            if (loc.Status == LocateStatus.Found && loc.Path is not null)
            {
                // Still there, or moved (followed by file ID).
                if (identity.TryResolve(loc.Path, out var now) && now.PathNorm != note.Folder.PathNorm)
                {
                    if (await TryRelinkAsync(note.Id, now, ct).ConfigureAwait(false))
                    {
                        relinked++;
                    }
                }
                else if (note.IsOrphaned)
                {
                    await repository.SetOrphanedAsync(note.Id, false, ct).ConfigureAwait(false);
                    recovered++;
                }

                continue;
            }

            // The ID is gone (or in the Recycle Bin). A folder recreated at the same path takes the note (ADR-009).
            if (Directory.Exists(note.Folder.PathDisplay) && identity.TryResolve(note.Folder.PathDisplay, out var recreated))
            {
                if (recreated.FileId != note.Folder.FileId || recreated.VolumeSerial != note.Folder.VolumeSerial)
                {
                    if (await TryRelinkAsync(note.Id, recreated, ct).ConfigureAwait(false))
                    {
                        relinked++;
                    }
                }
                else if (note.IsOrphaned)
                {
                    await repository.SetOrphanedAsync(note.Id, false, ct).ConfigureAwait(false);
                    recovered++;
                }

                continue;
            }

            if (!note.IsOrphaned)
            {
                await repository.SetOrphanedAsync(note.Id, true, ct).ConfigureAwait(false);
                orphaned++;
            }
        }

        return new OrphanScanReport(notes.Count, relinked, orphaned, recovered);
    }

    private async Task<bool> TryRelinkAsync(Guid id, Domain.FolderRef folder, CancellationToken ct)
    {
        try
        {
            await repository.RelinkAsync(id, folder, ct).ConfigureAwait(false);
            return true;
        }
        catch (DuplicateFolderNoteException)
        {
            return false;
        }
    }
}
