using System.Diagnostics;
using System.Runtime.InteropServices;

sealed record Tab(int Index, IWebBrowser2 Browser, nint Frame, nint TabHwnd, IShellBrowser? Sb);
readonly record struct PathRead(string? Path, int Hr, string Stage);

static class Harness
{
    public static IShellWindows CreateRoot()
    {
        int hr = Native.CoCreateInstance(Ids.ShellWindows, 0, Native.CLSCTX_LOCAL_SERVER, Ids.IShellWindows, out var p);
        if (hr != 0) throw new COMException("CoCreateInstance(ShellWindows)", hr);
        try { return Com.Wrap<IShellWindows>(p); } finally { Marshal.Release(p); }
    }

    public static List<Tab> Tabs(IShellWindows root)
    {
        var list = new List<Tab>();
        if (root.get_Count(out int n) != 0) return list;
        for (int i = 0; i < n; i++)
        {
            if (root.Item(Variant.FromInt(i), out var d) != 0 || d is null) continue;
            if (d is not IWebBrowser2 b) continue;
            if (b.get_HWND(out var frame) != 0) continue;
            IShellBrowser? sb = null; nint tab = 0;
            if (b is IServiceProvider sp && sp.QueryService(Ids.SID_STopLevelBrowser, Ids.IShellBrowser, out var p) == 0 && p != 0)
            {
                try { sb = Com.Wrap<IShellBrowser>(p); } finally { Marshal.Release(p); }
                if (sb.GetWindow(out var th) == 0) tab = th;
            }
            list.Add(new Tab(i, b, frame, tab, sb));
        }
        return list;
    }

    /// Route A: IShellBrowser -> IShellView -> IFolderView -> IPersistFolder2.GetCurFolder -> SHGetNameFromIDList.
    public static PathRead ReadPathA(Tab t)
    {
        if (t.Sb is null) return new(null, unchecked((int)0x80004002), "no IShellBrowser");
        int hr = t.Sb.QueryActiveShellView(out var view);
        if (hr != 0 || view is null) return new(null, hr, "QueryActiveShellView");
        if (view is not IFolderView fv) return new(null, unchecked((int)0x80004002), "cast IFolderView");
        hr = fv.GetFolder(Ids.IPersistFolder2, out var pf);
        if (hr != 0) return new(null, hr, "GetFolder");
        IPersistFolder2 f;
        try { f = Com.Wrap<IPersistFolder2>(pf); } finally { Marshal.Release(pf); }
        hr = f.GetCurFolder(out var pidl);
        if (hr != 0) return new(null, hr, "GetCurFolder");
        try
        {
            hr = Native.SHGetNameFromIDList(pidl, unchecked((int)0x80058000) /*SIGDN_FILESYSPATH*/, out var name);
            if (hr != 0) return new(null, hr, "SHGetNameFromIDList (virtual)");
            try { return new(Marshal.PtrToStringUni(name), 0, "ok"); } finally { Native.CoTaskMemFree(name); }
        }
        finally { Native.ILFree(pidl); }
    }

    /// Route B: IWebBrowser2.LocationURL -> PathCreateFromUrl.
    public static PathRead ReadPathB(Tab t)
    {
        int hr = t.Browser.get_LocationURL(out var url);
        if (hr != 0) return new(null, hr, "get_LocationURL");
        if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return new(null, 0, "not a file: url [" + url + "]");
        var buf = new char[2048]; uint len = (uint)buf.Length; int hr2;
        unsafe { fixed (char* pb = buf) hr2 = Native.PathCreateFromUrlW(url, pb, ref len, 0); } hr = hr2;
        return hr == 0 ? new(new string(buf, 0, (int)len), 0, "ok") : new(null, hr, "PathCreateFromUrl [" + url + "]");
    }

    public static void Release(object? o) { if (o is System.Runtime.InteropServices.Marshalling.ComObject c) c.FinalRelease(); }
    public static void ReleaseTabs(IEnumerable<Tab> tabs) { foreach (var t in tabs) { Release(t.Sb); Release(t.Browser); } }

    /// Opens an Explorer window for `arg`; returns the tab (from a fresh enumeration) that was not present before.
    public static (Tab? tab, List<Tab> all, double ms) Open(IShellWindows root, string arg, int timeoutMs = 6000)
    {
        var before = Tabs(root); var seen = before.Select(t => t.TabHwnd).ToHashSet(); ReleaseTabs(before);
        var sw = Stopwatch.StartNew();
        Process.Start(new ProcessStartInfo("explorer.exe", arg.StartsWith("shell:") || arg.StartsWith("search-ms:") ? arg : "\"" + arg + "\"") { UseShellExecute = false });
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(50);
            var now = Tabs(root);
            var fresh = now.FirstOrDefault(t => t.TabHwnd != 0 && !seen.Contains(t.TabHwnd));
            if (fresh is not null) { Thread.Sleep(400); return (fresh, now, sw.Elapsed.TotalMilliseconds); }
            ReleaseTabs(now);
        }
        return (null, new(), sw.Elapsed.TotalMilliseconds);
    }

    public static void CloseFrame(nint frame) { Native.PostMessageW(frame, Native.WM_CLOSE, 0, 0); }

    public static T OnSta<T>(Func<T> f)
    {
        T result = default!; Exception? err = null;
        var th = new Thread(() =>
        {
            Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
            try { result = f(); } catch (Exception e) { err = e; } finally { Native.CoUninitialize(); }
        });
        th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
        if (err is not null) throw new AggregateException(err);
        return result;
    }
}
