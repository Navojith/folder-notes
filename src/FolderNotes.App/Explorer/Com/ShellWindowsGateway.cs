using System.Runtime.InteropServices;

namespace FolderNotes.App.Explorer.Com;

/// <summary>The cached reference to one tab: the smallest COM object that can read its path (04 B.3). Disposed by the <see cref="TabMap{THandle}"/>.</summary>
internal abstract class TabHandle : IDisposable
{
    public abstract void Dispose();
}

/// <summary>
/// Outcome of reading one tab's folder. <see cref="Hr"/> &lt; 0 is a COM failure to classify; a virtual folder is
/// success with a null <see cref="Path"/> (04 E).
/// </summary>
internal readonly record struct PathReadResult(string? Path, int Hr)
{
    public static PathReadResult Success(string? path) => new(path, 0);

    public static PathReadResult Failure(int hr) => new(null, hr);
}

/// <summary>
/// The only place COM exists (04 A). Every call is made on the COM worker thread, never throws for a COM failure
/// (the HRESULT comes back), and everything it hands out is released by <see cref="IDisposable.Dispose"/> of the
/// handle or of the gateway itself.
/// </summary>
internal interface IShellWindowsGateway : IDisposable
{
    /// <summary>
    /// Enumerates <c>IShellWindows</c> once and appends one entry per readable tab. Returns 0, or the HRESULT of a
    /// failure of the root object (classify it: <c>Gone</c> means recovery, 04 F). The caller owns the handles.
    /// </summary>
    int TryEnumerateTabs(List<DiscoveredTab<TabHandle>> into);

    /// <summary>Reads the folder the tab shows. Cheap (S5/P3: 0.36 ms) and does not enumerate.</summary>
    PathReadResult TryReadPath(TabHandle tab);
}

/// <summary>
/// A gateway that records its per-item operations in the worker's journal (04 § M.2): <c>ItemProbe(i)</c> while the item's
/// process is unknown, <c>ItemRead(i)</c> once its frame (and so its process) is known.
/// </summary>
internal interface IJournaledGateway
{
    void AttachJournal(CallJournal journal, Func<nint, uint> processOf);
}

/// <summary>Creates the root object. Separate so recovery (04 F) can retry creation without a gateway existing.</summary>
internal interface IShellWindowsGatewayFactory
{
    /// <summary>Returns 0 and a gateway, or an HRESULT and null.</summary>
    int TryCreate(out IShellWindowsGateway? gateway);
}

internal sealed class ComTabHandle(ComRef<IShellBrowser> browser) : TabHandle
{
    public ComRef<IShellBrowser> Browser { get; } = browser;

    public override void Dispose() => Browser.Dispose();
}

/// <summary>Production factory: <c>CoCreateInstance(CLSID_ShellWindows)</c> on the calling STA thread.</summary>
internal sealed class ComShellWindowsGatewayFactory(RcwTracker tracker) : IShellWindowsGatewayFactory
{
    public int TryCreate(out IShellWindowsGateway? gateway)
    {
        gateway = null;
        try
        {
            int hr = ComNative.CoCreateInstance(ComIds.ShellWindows, 0, ComNative.ClsctxLocalServer, ComIds.IShellWindows, out nint raw);
            if (hr < 0 || raw == 0)
            {
                return hr < 0 ? hr : ComNative.EFail;
            }

            try
            {
                var root = ComWrapper.Wrap<IShellWindows>(raw);
                gateway = new ComShellWindowsGateway(new ComRef<IShellWindows>(root, tracker), tracker);
                return 0;
            }
            finally
            {
                Marshal.Release(raw);
            }
        }
        catch (Exception ex)
        {
            return ex.HResult < 0 ? ex.HResult : ComNative.EFail;
        }
    }
}

/// <summary>The one shared wrapper strategy for the source-generated interfaces.</summary>
internal static class ComWrapper
{
    private static readonly System.Runtime.InteropServices.Marshalling.StrategyBasedComWrappers Wrappers = new();

    /// <summary>Wraps a raw pointer (the caller still owns and releases its own reference).</summary>
    public static T Wrap<T>(nint pointer)
        where T : class =>
        (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
}

/// <summary>
/// Typed implementation over S5-validated interfaces (no <c>dynamic</c>, ADR-007). Enumeration follows the S1/P1
/// route: <c>Item(i)</c> → <c>IWebBrowser2.HWND</c> (frame) → <c>QueryService(SID_STopLevelBrowser)</c> →
/// <c>IShellBrowser.GetWindow</c> (the tab). Only the <c>IShellBrowser</c> is kept, everything else is released at once.
/// </summary>
internal sealed class ComShellWindowsGateway : IShellWindowsGateway, IJournaledGateway
{
    private readonly ComRef<IShellWindows> _root;
    private readonly RcwTracker _tracker;
    private CallJournal? _journal;
    private Func<nint, uint> _processOf = static _ => 0;

    public ComShellWindowsGateway(ComRef<IShellWindows> root, RcwTracker tracker)
    {
        _root = root;
        _tracker = tracker;
    }

    public void AttachJournal(CallJournal journal, Func<nint, uint> processOf)
    {
        _journal = journal;
        _processOf = processOf ?? throw new ArgumentNullException(nameof(processOf));
    }

    public int TryEnumerateTabs(List<DiscoveredTab<TabHandle>> into)
    {
        try
        {
            int hr = _root.Value.get_Count(out int count);
            if (hr < 0)
            {
                return hr;
            }

            for (int i = 0; i < count; i++)
            {
                TryAddTab(i, into);
            }

            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult < 0 ? ex.HResult : ComNative.EFail;
        }
    }

    private void TryAddTab(int index, List<DiscoveredTab<TabHandle>> into)
    {
        // Every call below goes out of process, including the casts (QueryInterface) and the releases of the item (04 § M.2).
        long probe = _journal?.Open(CallSite.ItemProbe, itemIndex: index) ?? 0;
        try
        {
            if (_root.Value.Item(Variant.FromInt(index), out var dispatch) != 0 || dispatch is null)
            {
                return;                                    // out of range is S_FALSE; a window closing is a failure: skip either
            }

            using var item = new ComRef<IDispatchSlots>(dispatch, _tracker);
            if (item.Value is not IWebBrowser2 browser || browser.get_HWND(out nint frame) != 0)
            {
                return;
            }

            _journal?.Close(probe);
            probe = _journal?.Open(CallSite.ItemRead, _processOf(frame), frame, index) ?? 0;
            ReadItem(item, frame, into);
        }
        finally
        {
            _journal?.Close(probe);
        }
    }

    private void ReadItem(ComRef<IDispatchSlots> item, nint frame, List<DiscoveredTab<TabHandle>> into)
    {
        if (item.Value is not IComServiceProvider services
            || services.QueryService(ComIds.SidSTopLevelBrowser, ComIds.IShellBrowser, out nint raw) != 0
            || raw == 0)
        {
            return;
        }

        ComRef<IShellBrowser> shellBrowser;
        try
        {
            shellBrowser = new ComRef<IShellBrowser>(ComWrapper.Wrap<IShellBrowser>(raw), _tracker);
        }
        finally
        {
            Marshal.Release(raw);
        }

        if (shellBrowser.Value.GetWindow(out nint tab) != 0 || (tab == 0 && frame == 0))
        {
            shellBrowser.Dispose();
            return;
        }

        // Without a tab window (Windows 10, no tabs) the frame is the single implicit tab (04 C).
        into.Add(new DiscoveredTab<TabHandle>(frame, tab != 0 ? tab : frame, new ComTabHandle(shellBrowser)));
    }

    public PathReadResult TryReadPath(TabHandle tab)
    {
        try
        {
            if (tab is not ComTabHandle handle || handle.Browser.IsReleased)
            {
                return PathReadResult.Failure(ComNative.EFail);
            }

            int hr = handle.Browser.Value.QueryActiveShellView(out var viewObject);
            if (hr < 0 || viewObject is null)
            {
                return PathReadResult.Failure(hr < 0 ? hr : ComNative.EFail);
            }

            using var view = new ComRef<IShellView>(viewObject, _tracker);
            if (view.Value is not IFolderView folderView)
            {
                return PathReadResult.Failure(ComNative.ENoInterface);
            }

            hr = folderView.GetFolder(ComIds.IPersistFolder2, out nint rawFolder);
            if (hr < 0 || rawFolder == 0)
            {
                return PathReadResult.Failure(hr < 0 ? hr : ComNative.EFail);
            }

            ComRef<IPersistFolder2> folder;
            try
            {
                folder = new ComRef<IPersistFolder2>(ComWrapper.Wrap<IPersistFolder2>(rawFolder), _tracker);
            }
            finally
            {
                Marshal.Release(rawFolder);
            }

            using (folder)
            {
                hr = folder.Value.GetCurFolder(out nint pidl);
                if (hr < 0 || pidl == 0)
                {
                    return PathReadResult.Failure(hr < 0 ? hr : ComNative.EFail);
                }

                try
                {
                    // A virtual folder (This PC, Home, Recycle Bin...) has no file-system path: success with null (S5/P3).
                    if (ComNative.SHGetNameFromIDList(pidl, ComNative.SigdnFilesysPath, out nint name) < 0 || name == 0)
                    {
                        return PathReadResult.Success(null);
                    }

                    try
                    {
                        return PathReadResult.Success(Marshal.PtrToStringUni(name));
                    }
                    finally
                    {
                        ComNative.CoTaskMemFree(name);
                    }
                }
                finally
                {
                    ComNative.ILFree(pidl);
                }
            }
        }
        catch (Exception ex)
        {
            return PathReadResult.Failure(ex.HResult < 0 ? ex.HResult : ComNative.EFail);
        }
    }

    public void Dispose() => _root.Dispose();
}
