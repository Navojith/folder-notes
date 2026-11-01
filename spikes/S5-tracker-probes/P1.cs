using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

/// P1: validate every interface/method declaration (D1): vtable order vs SDK headers, arity, live calls,
/// HRESULTs, casting, VARIANT, lifetime/release.
static class P1
{
    const string Inc = @"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\";
    static int _fail;
    static int RefCount(nint p) { Marshal.AddRef(p); return (int)Marshal.Release(p); }
    static void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) _fail++; }

    public static int Run()
    {
        Console.WriteLine("== P1a: vtable order + arity vs SDK headers ==");
        StaticCheck("IOleWindow", "oleidl.h", typeof(IOleWindow));
        StaticCheck("IServiceProvider", "servprov.h", typeof(IServiceProvider));
        StaticCheck("IShellBrowser", "ShObjIdl_core.h", typeof(IShellBrowser), typeof(IOleWindow));
        StaticCheck("IShellView", "ShObjIdl_core.h", typeof(IShellView), typeof(IOleWindow));
        StaticCheck("IFolderView", "ShObjIdl_core.h", typeof(IFolderView));
        StaticCheck("IPersistFolder2", "ShObjIdl_core.h", typeof(IPersistFolder2), typeof(IPersist), typeof(IPersistFolder));
        StaticCheck("IWebBrowser2", "ExDisp.h", typeof(IWebBrowser2), typeof(IDispatchSlots), typeof(IWebBrowser), typeof(IWebBrowserApp));
        StaticCheck("IShellWindows", "ExDisp.h", typeof(IShellWindows), typeof(IDispatchSlots));
        Console.WriteLine($"  sizeof(Variant) = {Marshal.SizeOf<Variant>()} (VARIANT on x64 = 24)");
        Check(Marshal.SizeOf<Variant>() == 24, "Variant struct is 24 bytes");

        Console.WriteLine("== P1b: live calls, HRESULTs, casting, VARIANT ==");
        Harness.OnSta(() => { Live(); return 0; });
        Console.WriteLine($"== P1 result: {(_fail == 0 ? "ALL PASS" : _fail + " FAIL")} ==");
        return _fail;
    }

    static List<string> HeaderNames(string file, string iface, out List<int> arity)
    {
        var text = File.ReadAllText(Inc + file);
        int i = text.IndexOf($"typedef struct {iface}Vtbl", StringComparison.Ordinal);
        int e = text.IndexOf("END_INTERFACE", i, StringComparison.Ordinal);
        var body = text[i..e];
        var names = new List<string>(); arity = new();
        foreach (Match m in Regex.Matches(body, @"\(\s*STDMETHODCALLTYPE\s*\*\s*(\w+)\s*\)\s*\((.*?)\);", RegexOptions.Singleline))
        {
            names.Add(m.Groups[1].Value);
            var args = Regex.Replace(m.Groups[2].Value, @"\([^()]*\)", "").Trim();
            arity.Add(args.Length == 0 ? 0 : args.Count(c => c == ',') + 1);   // includes the leading "This"
        }
        return names;
    }

    static void StaticCheck(string iface, string header, Type t, params Type[] bases)
    {
        var expected = HeaderNames(header, iface, out var arity);
        var mine = new List<(string name, int args)> { ("QueryInterface", 3), ("AddRef", 1), ("Release", 1) };
        foreach (var ty in bases.Append(t))
            foreach (var m in ty.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.MetadataToken))
                { if (!mine.Any(x => x.name == m.Name)) mine.Add((m.Name, m.GetParameters().Length + 1)); }
        bool orderOk = true, arityOk = true; string detail = "";
        for (int k = 0; k < mine.Count; k++)
        {
            if (k >= expected.Count || expected[k] != mine[k].name) { orderOk = false; detail = $"slot {k}: mine={mine[k].name} header={(k < expected.Count ? expected[k] : "-")}"; break; }
            if (arity[k] != mine[k].args) { arityOk = false; detail = $"slot {k} {mine[k].name}: mine={mine[k].args} header={arity[k]} params"; }
        }
        Check(orderOk && arityOk, $"{iface}: {mine.Count} slots declared of {expected.Count} in header, order{(orderOk ? "" : " WRONG")} arity{(arityOk ? "" : " WRONG")} {detail}");
    }

    static void Live()
    {
        var root = Harness.CreateRoot();
        string dir = Path.Combine(Path.GetTempPath(), "fn-s5", "p1 Ünï"); Directory.CreateDirectory(dir);
        var (tab, all, ms) = Harness.Open(root, dir);
        Check(tab is not null, $"opened an Explorer window ({ms:F0} ms)");
        if (tab is null) return;
        try
        {
            Check(root.get_Count(out int n) == 0 && n >= 1, $"IShellWindows.get_Count -> {n}");
            Check(tab.Frame != 0 && Native.ClassName(tab.Frame) == "CabinetWClass", $"IWebBrowser2.get_HWND (via IWebBrowserApp slot 6) = frame {Native.ClassName(tab.Frame)}");
            Check(tab.TabHwnd != 0 && Native.ClassName(tab.TabHwnd) == "ShellTabWindowClass", $"QueryService + IShellBrowser.GetWindow (inherited IOleWindow) = {Native.ClassName(tab.TabHwnd)}");
            var a = Harness.ReadPathA(tab); var b = Harness.ReadPathB(tab);
            Check(string.Equals(a.Path, dir, StringComparison.OrdinalIgnoreCase), $"route A path = '{a.Path}' ({a.Stage})");
            Check(string.Equals(b.Path, dir, StringComparison.OrdinalIgnoreCase), $"route B path = '{b.Path}' ({b.Stage})");

            // VARIANT + HRESULT
            int hrBad = root.Item(Variant.FromInt(9999), out var none);
            Check(hrBad != 0 && none is null, $"Item(9999) -> {Com.Hr(hrBad)} with no exception and null out");
            int hrBadVt = root.Item(new Variant { vt = 0 /*VT_EMPTY*/ }, out var none2);
            Console.WriteLine($"  info: Item(VT_EMPTY) -> {Com.Hr(hrBadVt)} out null={none2 is null}");

            // casting
            var d = tab.Browser as object;
            Check(d is IServiceProvider, "cast IWebBrowser2 -> IServiceProvider (QI succeeds)");
            Check(d is not IShellBrowser, "cast IWebBrowser2 -> IShellBrowser correctly fails (is-pattern)");
            bool threw = false; try { _ = (IShellBrowser)d; } catch (InvalidCastException) { threw = true; }
            Check(threw, "explicit (IShellBrowser) cast throws InvalidCastException");

            // lifetime: ComObject.FinalRelease() must drop every reference the RCW holds (raw proxy refcount returns to baseline)
            var spare = Harness.Tabs(root).Last();
            ((IServiceProvider)spare.Browser).QueryService(Ids.SID_STopLevelBrowser, Ids.IShellBrowser, out var raw);   // we own one ref
            int c0 = RefCount(raw);
            var sbx = Com.Wrap<IShellBrowser>(raw); sbx.GetWindow(out _); sbx.QueryActiveShellView(out var vw); _ = vw as IFolderView;
            int c1 = RefCount(raw);
            Harness.Release(vw); Harness.Release(sbx);
            int c2 = RefCount(raw);
            Console.WriteLine($"  info: raw proxy refcount: before RCW {c0}, RCW alive (+QI casts) {c1}, after ComObject.FinalRelease {c2}");
            Check(c1 > c0 && c2 == c0, "ComObject.FinalRelease() returns the proxy refcount to its baseline");
            bool dead = false; try { sbx.GetWindow(out _); } catch (ObjectDisposedException) { dead = true; } catch (Exception ex) { Console.WriteLine("  info: threw " + ex.GetType().Name); }
            Check(dead, "using a released RCW throws ObjectDisposedException (not a crash)");
            bool mrcThrows = false; try { Marshal.FinalReleaseComObject(spare.Browser); } catch (ArgumentException) { mrcThrows = true; }
            Check(mrcThrows, "FINDING: Marshal.(Final)ReleaseComObject is NOT usable on generated RCWs (ArgumentException)");
            Marshal.Release(raw);
            Harness.ReleaseTabs(new[] { spare });

            // Gone codes: close the window, then call the old objects
            var t2 = Harness.Tabs(root).First(x => x.TabHwnd == tab.TabHwnd);
            Harness.CloseFrame(tab.Frame);
            Thread.Sleep(1200);
            int hrGone = t2.Browser.get_HWND(out _);
            int hrGone2 = t2.Sb!.GetWindow(out _);
            var pr = Harness.ReadPathA(t2);
            Console.WriteLine($"  info: after window closed: get_HWND {Com.Hr(hrGone)}, GetWindow {Com.Hr(hrGone2)}, path route A {Com.Hr(pr.Hr)} at '{pr.Stage}'");
            Check(hrGone != 0 || hrGone2 != 0 || pr.Hr != 0, "calls on a closed window's objects report an HRESULT (no crash)");
            Harness.ReleaseTabs(new[] { t2 });
            Harness.ReleaseTabs(all);

            // churn: create/enumerate/release 300 times; handles and memory stay flat
            var p = Process.GetCurrentProcess(); p.Refresh(); int h0 = p.HandleCount; long m0 = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++) { var ts = Harness.Tabs(root); foreach (var t in ts) Harness.ReadPathA(t); Harness.ReleaseTabs(ts); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); p.Refresh();
            Console.WriteLine($"  info: 300 enumerate+read+release cycles in {sw.ElapsedMilliseconds} ms; handles {h0}->{p.HandleCount}, managed {m0 / 1024}->{GC.GetTotalMemory(true) / 1024} KB");
            Check(p.HandleCount - h0 <= 10, "no handle growth over 300 cycles");
        }
        finally { Harness.CloseFrame(tab.Frame); Harness.Release(root); }
    }
}
