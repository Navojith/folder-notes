using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FolderNotes.App.Explorer.Com;

// Hand-declared [GeneratedComInterface] definitions (decision D1), validated against the Windows SDK 26100 headers
// and live calls by spike S5/P1 (04 § H). Every method is [PreserveSig]: the raw HRESULT comes back and is
// classified by us (ComFailureClassifier), nothing throws for an ordinary failure. Slots we never call are
// declared with plain nint signatures only to keep the vtable order right. DO NOT reorder or "tidy" members:
// the order IS the ABI. A wrong slot is a silent crash risk, so a change here needs P1 re-run.

/// <summary>VARIANT is 24 bytes on x64: vt + 3 reserved words + a 16-byte union.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Variant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public long A;
    public long B;

    public static Variant FromInt(int value) => new() { Vt = 3 /* VT_I4 */, A = value };
}

[GeneratedComInterface]
[Guid("00020400-0000-0000-C000-000000000046")]
internal partial interface IDispatchSlots
{
    [PreserveSig] int GetTypeInfoCount(out uint count);
    [PreserveSig] int GetTypeInfo(uint index, uint lcid, out nint typeInfo);
    [PreserveSig] int GetIDsOfNames(nint riid, nint names, uint count, uint lcid, nint dispIds);
    [PreserveSig] int Invoke(int dispId, nint riid, uint lcid, ushort flags, nint pDispParams, nint result, nint excep, nint argErr);
}

[GeneratedComInterface]
[Guid("00000114-0000-0000-C000-000000000046")]
internal partial interface IOleWindow
{
    [PreserveSig] int GetWindow(out nint hwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
}

[GeneratedComInterface]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal partial interface IComServiceProvider
{
    [PreserveSig] int QueryService(in Guid service, in Guid riid, out nint ppv);
}

[GeneratedComInterface]
[Guid("000214E3-0000-0000-C000-000000000046")]
internal partial interface IShellView : IOleWindow
{
    // only cast to IFolderView; its own slots are not called
}

[GeneratedComInterface]
[Guid("000214E2-0000-0000-C000-000000000046")]
internal partial interface IShellBrowser : IOleWindow
{
    [PreserveSig] int InsertMenusSB(nint hmenu, nint widths);
    [PreserveSig] int SetMenuSB(nint hmenu, nint holemenu, nint hwnd);
    [PreserveSig] int RemoveMenusSB(nint hmenu);
    [PreserveSig] int SetStatusTextSB(nint text);
    [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
    [PreserveSig] int TranslateAcceleratorSB(nint msg, ushort id);
    [PreserveSig] int BrowseObject(nint pidl, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
    [PreserveSig] int GetControlWindow(uint id, out nint hwnd);
    [PreserveSig] int SendControlMsg(uint id, uint msg, nuint w, nint l, out nint ret);
    [PreserveSig] int QueryActiveShellView(out IShellView view);
}

[GeneratedComInterface]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
internal partial interface IFolderView
{
    [PreserveSig] int GetCurrentViewMode(out uint mode);
    [PreserveSig] int SetCurrentViewMode(uint mode);
    [PreserveSig] int GetFolder(in Guid riid, out nint ppv);
}

[GeneratedComInterface]
[Guid("0000010C-0000-0000-C000-000000000046")]
internal partial interface IPersist
{
    [PreserveSig] int GetClassID(out Guid clsid);
}

[GeneratedComInterface]
[Guid("000214EA-0000-0000-C000-000000000046")]
internal partial interface IPersistFolder : IPersist
{
    [PreserveSig] int Initialize(nint pidl);
}

[GeneratedComInterface]
[Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F")]
internal partial interface IPersistFolder2 : IPersistFolder
{
    [PreserveSig] int GetCurFolder(out nint pidl);
}

[GeneratedComInterface]
[Guid("EAB22AC1-30C1-11CF-A7EB-0000C05BAE0B")]
internal partial interface IWebBrowser : IDispatchSlots
{
    [PreserveSig] int GoBack();
    [PreserveSig] int GoForward();
    [PreserveSig] int GoHome();
    [PreserveSig] int GoSearch();
    [PreserveSig] int Navigate(nint url, nint flags, nint target, nint postData, nint headers);
    [PreserveSig] int Refresh();
    [PreserveSig] int Refresh2(nint level);
    [PreserveSig] int Stop();
    [PreserveSig] int get_Application(out nint p);
    [PreserveSig] int get_Parent(out nint p);
    [PreserveSig] int get_Container(out nint p);
    [PreserveSig] int get_Document(out nint p);
    [PreserveSig] int get_TopLevelContainer(out short b);
    [PreserveSig] int get_Type(out nint s);
    [PreserveSig] int get_Left(out int v);
    [PreserveSig] int put_Left(int v);
    [PreserveSig] int get_Top(out int v);
    [PreserveSig] int put_Top(int v);
    [PreserveSig] int get_Width(out int v);
    [PreserveSig] int put_Width(int v);
    [PreserveSig] int get_Height(out int v);
    [PreserveSig] int put_Height(int v);
    [PreserveSig] int get_LocationName(out nint s);
    [PreserveSig] int get_LocationURL(out nint s);   // dropped as a path route (breaks UNC, 04 E); slot kept for the vtable
    [PreserveSig] int get_Busy(out short b);
}

[GeneratedComInterface]
[Guid("0002DF05-0000-0000-C000-000000000046")]
internal partial interface IWebBrowserApp : IWebBrowser
{
    [PreserveSig] int Quit();
    [PreserveSig] int ClientToWindow(nint pcx, nint pcy);
    [PreserveSig] int PutProperty(nint name, nint value);
    [PreserveSig] int GetProperty(nint name, nint value);
    [PreserveSig] int get_Name(out nint s);
    [PreserveSig] int get_HWND(out nint hwnd);
}

[GeneratedComInterface]
[Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E")]
internal partial interface IWebBrowser2 : IWebBrowserApp
{
}

[GeneratedComInterface]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
internal partial interface IShellWindows : IDispatchSlots
{
    [PreserveSig] int get_Count(out int count);
    [PreserveSig] int Item(Variant index, out IDispatchSlots folder);
}

internal static class ComIds
{
    public static readonly Guid ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid IShellWindows = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    public static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IPersistFolder2 = new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
}
