using FolderNotes.App.Labels;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;
using Serilog.Core;

namespace FolderNotes.App.Tests.Labels;

/// <summary>specs/05 § Labels page, REQ-012/013, against the real SQLite store.</summary>
public sealed class LabelsViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fn-labels-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeEvents _events = new();
    private SqliteNoteStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(_dir, "notes.db"),
            Identity = new FolderIdentityService(),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task REQ012_REQ013_Lists_StarterLabels_WithUsage()
    {
        await AddNoteAsync(@"D:\A", "client", "personal");
        await AddNoteAsync(@"D:\B", "client");
        var gone = await AddNoteAsync(@"D:\C", "client");
        await _store.DeleteAsync(gone.Id, Ct);

        using var vm = await OpenAsync();

        vm.Items.Select(i => i.Name).ShouldBe(["archive", "build-output", "client", "do-not-delete", "personal"]);
        vm.CountText.ShouldBe("5 labels");
        Item(vm, "client").UsageText.ShouldBe("Used by 2 notes");
        Item(vm, "personal").UsageText.ShouldBe("Used by 1 note");
        Item(vm, "archive").UsageText.ShouldBe("Not used");
        Item(vm, "archive").IsUsed.ShouldBeFalse();
        Item(vm, "client").AccessibleName.ShouldBe("Label client, used by 2 notes");
    }

    // ───────────── create ─────────────

    [Fact]
    public async Task REQ012_Create_PresetsTheLeastUsedColour_AndTrims()
    {
        using var vm = await OpenAsync();
        vm.NewColor.ShouldBe("orange"); // starters use blue, amber, indigo, green, red

        vm.NewName = "  invoices  ";
        (await vm.CreateAsync()).ShouldBeTrue();

        var created = (await _store.ListLabelsAsync(Ct)).Single(l => l.Name == "invoices");
        created.Color.ShouldBe("orange");
        vm.NewName.ShouldBe("");
        vm.NewError.ShouldBeNull();
        vm.NewColor.ShouldBe("yellow"); // the next least-used one
        vm.Items.Select(i => i.Name).ShouldContain("invoices");
    }

    [Fact]
    public async Task REQ010_Create_RefusesDuplicatesAndBrokenNames()
    {
        using var vm = await OpenAsync();

        vm.NewName = "CLIENT";
        (await vm.CreateAsync()).ShouldBeFalse();
        vm.NewError.ShouldBe("A label named 'CLIENT' already exists.");
        vm.NewName.ShouldBe("CLIENT"); // kept for fixing

        vm.NewName = "CLIENTS";
        vm.NewError.ShouldBeNull(); // typing clears it

        vm.NewName = "   ";
        (await vm.CreateAsync()).ShouldBeFalse();
        vm.NewError.ShouldBe("A label name is required.");

        vm.NewName = new string('x', Label.MaxNameLength + 1);
        (await vm.CreateAsync()).ShouldBeFalse();
        vm.NewError.ShouldBe($"A label name can be at most {Label.MaxNameLength} characters.");
        (await _store.ListLabelsAsync(Ct)).Count.ShouldBe(5);
    }

    [Fact]
    public async Task REQ012_Create_PickedColourIsUsed()
    {
        using var vm = await OpenAsync();
        vm.NewName = "teal one";
        vm.NewColor = "teal";
        (await vm.CreateAsync()).ShouldBeTrue();
        (await _store.ListLabelsAsync(Ct)).Single(l => l.Name == "teal one").Color.ShouldBe("teal");
    }

    // ───────────── rename ─────────────

    [Fact]
    public async Task REQ012_Rename_UpdatesEveryNote()
    {
        var note = await AddNoteAsync(@"D:\A", "client");
        using var vm = await OpenAsync();
        var client = Item(vm, "client");

        vm.BeginRename(client);
        client.IsEditing.ShouldBeTrue();
        client.EditName.ShouldBe("client");
        client.EditName = " customer ";
        (await vm.CommitRenameAsync(client)).ShouldBeTrue();

        client.IsEditing.ShouldBeFalse();
        client.Name.ShouldBe("customer");
        (await _store.GetByIdAsync(note.Id, Ct))!.Labels.Single().Name.ShouldBe("customer");
        vm.Items.Select(i => i.Name).ShouldBe(["archive", "build-output", "customer", "do-not-delete", "personal"]);
    }

    [Fact]
    public async Task REQ012_Rename_CaseOnly_IsARename()
    {
        using var vm = await OpenAsync();
        var client = Item(vm, "client");
        vm.BeginRename(client);
        client.EditName = "Client";
        (await vm.CommitRenameAsync(client)).ShouldBeTrue();
        (await _store.ListLabelsAsync(Ct)).ShouldContain(l => l.Name == "Client");
    }

    [Fact]
    public async Task REQ010_Rename_ToAnotherLabelsName_IsRefused_AndStaysInEditMode()
    {
        using var vm = await OpenAsync();
        var client = Item(vm, "client");
        vm.BeginRename(client);
        client.EditName = "Personal";

        (await vm.CommitRenameAsync(client)).ShouldBeFalse();

        client.IsEditing.ShouldBeTrue();
        client.Error.ShouldBe("A label named 'Personal' already exists.");
        client.Name.ShouldBe("client");

        LabelsViewModel.CancelRename(client);
        client.IsEditing.ShouldBeFalse();
        client.Error.ShouldBeNull();
    }

    [Fact]
    public async Task REQ012_Rename_Unchanged_JustLeavesEditMode()
    {
        using var vm = await OpenAsync();
        var client = Item(vm, "client");
        vm.BeginRename(client);
        (await vm.CommitRenameAsync(client)).ShouldBeTrue();
        client.IsEditing.ShouldBeFalse();
    }

    [Fact]
    public async Task REQ012_BeginRename_CancelsAnotherRowsRename()
    {
        using var vm = await OpenAsync();
        var a = Item(vm, "client");
        var b = Item(vm, "archive");
        vm.BeginRename(a);
        a.EditName = "half typed";
        vm.BeginRename(b);
        a.IsEditing.ShouldBeFalse();
        a.EditName.ShouldBe("client");
        b.IsEditing.ShouldBeTrue();
    }

    [Fact]
    public async Task REQ012_RowEditState_SurvivesAReload()
    {
        using var vm = await OpenAsync();
        var client = Item(vm, "client");
        vm.BeginRename(client);
        client.EditName = "typing…";

        await AddNoteAsync(@"D:\A", "client");
        _events.RaiseNote();
        await vm.WhenIdleAsync();

        Item(vm, "client").ShouldBeSameAs(client);
        client.IsEditing.ShouldBeTrue();
        client.EditName.ShouldBe("typing…");
        client.UsageText.ShouldBe("Used by 1 note");
    }

    // ───────────── recolour / delete ─────────────

    [Fact]
    public async Task REQ012_Recolor()
    {
        using var vm = await OpenAsync();
        var client = Item(vm, "client");
        (await vm.RecolorAsync(client, "pink")).ShouldBeTrue();
        client.Color.ShouldBe("pink");
        (await _store.ListLabelsAsync(Ct)).Single(l => l.Name == "client").Color.ShouldBe("pink");
    }

    [Fact]
    public async Task REQ012_Delete_RemovesItFromEveryNote()
    {
        var note = await AddNoteAsync(@"D:\A", "client", "personal");
        using var vm = await OpenAsync();
        var client = Item(vm, "client");
        LabelsViewModel.DeleteConfirmation(client).ShouldBe(("Delete label 'client'?", "It will be removed from 1 note."));
        LabelsViewModel.DeleteConfirmation(Item(vm, "archive")).ShouldBe(("Delete label 'archive'?", (string?)null));

        await vm.DeleteAsync(client);

        vm.Items.Select(i => i.Name).ShouldNotContain("client");
        vm.CountText.ShouldBe("4 labels");
        (await _store.GetByIdAsync(note.Id, Ct))!.Labels.Select(l => l.Name).ShouldBe(["personal"]);
    }

    [Fact]
    public async Task REQ013_DeletedStarterLabel_DoesNotComeBack()
    {
        using var vm = await OpenAsync();
        await vm.DeleteAsync(Item(vm, "do-not-delete"));
        await vm.ReloadAsync();
        vm.Items.Select(i => i.Name).ShouldNotContain("do-not-delete");
    }

    [Fact]
    public async Task REQ012_LabelChangedElsewhere_Reloads()
    {
        using var vm = await OpenAsync();
        await _store.CreateLabelAsync("from-editor", "lime", Ct);
        _events.RaiseLabels();
        await vm.WhenIdleAsync();
        vm.Items.Select(i => i.Name).ShouldContain("from-editor");
        vm.NewColor.ShouldBe("orange");
    }

    // ───────────── helpers ─────────────

    private async Task<LabelsViewModel> OpenAsync()
    {
        var vm = new LabelsViewModel(_store, _events, Logger.None);
        await vm.ActivateAsync();
        return vm;
    }

    private static LabelItem Item(LabelsViewModel vm, string name) => vm.Items.Single(i => i.Name == name);

    private async Task<Note> AddNoteAsync(string path, params string[] labels)
    {
        var note = Note.CreateFor(new FolderRef(path.ToUpperInvariant(), path, null, null)) with
        {
            Title = path,
            Labels = [.. labels.Select(n => Label.Create(n, "blue"))],
        };
        return await _store.UpsertAsync(note, Ct);
    }

    private sealed class FakeEvents : INoteEvents
    {
        public event EventHandler<NoteChangedEventArgs>? NoteChanged;

        public event EventHandler? LabelsChanged;

        public void RaiseLabels() => LabelsChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseNote() => NoteChanged?.Invoke(this, new NoteChangedEventArgs(Guid.Empty, NoteChangeKind.Created, ""));
    }
}
