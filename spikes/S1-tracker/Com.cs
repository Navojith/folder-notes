using System.Reflection;

/// IDispatch late binding via reflection. The `dynamic` COM binder is NOT thread-safe
/// (ComTypeLibDesc cache race between two STA threads → ArgumentException), so avoid it.
static class Com
{
    public static object Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId)!)!;
    public static object? Get(object o, string name) => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
    public static object? Call(object o, string name, params object?[] args) => o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);

    public static IEnumerable<object> Browsers(object shell)
    {
        var ws = Call(shell, "Windows")!;
        int n = (int)Get(ws, "Count")!;
        for (int i = 0; i < n; i++)
        {
            object? w = null;
            try { w = Call(ws, "Item", i); } catch { }
            if (w is not null) yield return w;
        }
    }

    public static IntPtr Hwnd(object browser) => (IntPtr)Convert.ToInt64(Get(browser, "HWND"));

    public static string? RawPath(object browser)
    {
        var doc = Get(browser, "Document");
        var folder = doc is null ? null : Get(doc, "Folder");
        var self = folder is null ? null : Get(folder, "Self");
        return self is null ? null : (string?)Get(self, "Path");
    }
}
