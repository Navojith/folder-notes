using System.Reflection;
using System.Runtime.InteropServices;

/// P1c: how are source-generated RCWs released deterministically? Measures the raw proxy refcount.
static class P1c
{
    static int RefCount(nint p) { Marshal.AddRef(p); return (int)Marshal.Release(p); }

    public static int Run()
    {
        Harness.OnSta(() => { Go(); return 0; });
        return 0;
    }

    static void Go()
    {
        var root = Harness.CreateRoot();
        var tabs = Harness.Tabs(root);
        var t = tabs.First();
        var sp = (IServiceProvider)t.Browser;
        sp.QueryService(Ids.SID_STopLevelBrowser, Ids.IShellBrowser, out var p);   // we own 1 ref on p
        int n0 = RefCount(p);
        var rcw = Com.Wrap<IShellBrowser>(p);
        int n1 = RefCount(p);
        Console.WriteLine($"RCW type: {rcw.GetType().FullName}; IDisposable={rcw is IDisposable}");
        foreach (var m in rcw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) Console.WriteLine("   method: " + m.Name);
        Console.WriteLine($"refcount: before wrap {n0}, with RCW {n1}");
        try { Console.WriteLine("  ReleaseComObject -> " + Marshal.ReleaseComObject(rcw)); } catch (Exception e) { Console.WriteLine("  ReleaseComObject threw " + e.GetType().Name + ": " + e.Message); }
        Console.WriteLine($"refcount after ReleaseComObject: {RefCount(p)}");
        try { Console.WriteLine("  FinalReleaseComObject -> " + Marshal.FinalReleaseComObject(rcw)); } catch (Exception e) { Console.WriteLine("  FinalReleaseComObject threw " + e.GetType().Name + ": " + e.Message); }
        Console.WriteLine($"refcount after FinalReleaseComObject: {RefCount(p)}");
        var fr = rcw.GetType().GetMethod("FinalRelease", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (fr != null) { try { fr.Invoke(rcw, null); Console.WriteLine("  invoked FinalRelease()"); } catch (Exception e) { Console.WriteLine("  FinalRelease threw " + e.InnerException?.GetType().Name); } Console.WriteLine($"refcount after FinalRelease(): {RefCount(p)}"); }
        if (rcw is IDisposable d) { d.Dispose(); Console.WriteLine($"refcount after Dispose(): {RefCount(p)}"); }
        rcw = null!; GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"refcount after GC: {RefCount(p)}");
        Marshal.Release(p);
        Harness.ReleaseTabs(tabs); Harness.Release(root);
    }
}
