using System.Runtime.InteropServices;
using ConvertWheel.Native;

namespace ConvertWheel.Shell;

// Minimal COM declarations for talking to Explorer windows. Placeholder methods exist only to keep
// vtable slots in the right order; they are never called.

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    int Count { get; }
    [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
    [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
    void Register();
    void RegisterPending();
    void Revoke();
    void OnNavigate();
    void OnActivated();
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object loc, [MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int hwnd, int options);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    [PreserveSig] int GetWindow(out nint hwnd);
    [PreserveSig] int ContextSensitiveHelp(int enter);
    [PreserveSig] int InsertMenusSB(nint a, nint b);
    [PreserveSig] int SetMenuSB(nint a, nint b, nint c);
    [PreserveSig] int RemoveMenusSB(nint a);
    [PreserveSig] int SetStatusTextSB(nint a);
    [PreserveSig] int EnableModelessSB(int a);
    [PreserveSig] int TranslateAcceleratorSB(nint a, ushort b);
    [PreserveSig] int BrowseObject(nint a, uint b);
    [PreserveSig] int GetViewStateStream(uint a, out nint b);
    [PreserveSig] int GetControlWindow(uint a, out nint b);
    [PreserveSig] int SendControlMsg(uint a, uint b, nint c, nint d, out nint e);
    [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    [PreserveSig] int GetWindow(out nint hwnd);
}

[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // IFolderView
    [PreserveSig] int GetCurrentViewMode(out uint mode);
    [PreserveSig] int SetCurrentViewMode(uint mode);
    [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int Item(int index, out nint pidl);
    [PreserveSig] int ItemCount(uint flags, out int count);
    [PreserveSig] int Items(uint flags, ref Guid riid, out nint ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int item);
    [PreserveSig] int GetFocusedItem(out int item);
    [PreserveSig] int GetItemPosition(nint pidl, out POINT pt);
    [PreserveSig] int GetSpacing(nint pt);
    [PreserveSig] int GetDefaultSpacing(nint pt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int item, uint flags);
    [PreserveSig] int SelectAndPositionItems(uint count, nint pidls, nint pts, uint flags);
    // IFolderView2
    [PreserveSig] int SetGroupBy(nint key, int ascending);
    [PreserveSig] int GetGroupBy(nint key, nint ascending);
    [PreserveSig] int SetViewProperty(nint pidl, nint key, nint value);
    [PreserveSig] int GetViewProperty(nint pidl, nint key, nint value);
    [PreserveSig] int SetTileViewProperties(nint pidl, nint list);
    [PreserveSig] int SetExtendedTileViewProperties(nint pidl, nint list);
    [PreserveSig] int SetText(int type, nint text);
    [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
    [PreserveSig] int GetCurrentFolderFlags(out uint flags);
    [PreserveSig] int GetSortColumnCount(out int count);
    [PreserveSig] int SetSortColumns(nint columns, int count);
    [PreserveSig] int GetSortColumns(nint columns, int count);
    [PreserveSig] int GetItem(int index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
}

[ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFolder2
{
    [PreserveSig] int GetClassID(out Guid clsid);
    [PreserveSig] int Initialize(nint pidl);
    [PreserveSig] int GetCurFolder(out nint pidl);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint sigdn, out nint name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}

[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, int flags, out nint hbitmap);
}

internal static class ShellIds
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    public static readonly Guid IID_IPersistFolder2 = new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
    public static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    public const uint SIGDN_NORMALDISPLAY = 0;
    public const uint SIGDN_PARENTRELATIVE = 0x80080001;
    public const uint SIGDN_FILESYSPATH = 0x80058000;
    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;
    public const uint SVGIO_ALLVIEW = 2;
}
