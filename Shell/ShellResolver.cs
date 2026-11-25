using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConvertWheel.Native;

namespace ConvertWheel.Shell;

/// <summary>
/// Finds the file under the mouse in Explorer windows (including tabs) and on the desktop.
/// UI Automation gives the hovered item's name and place in the view; the Explorer view hosting it gives the
/// real item. COM work runs on STA workers, replaced if they hang, so a stuck Explorer can't hold anything up.
/// </summary>
internal sealed class ShellResolver : IDisposable
{
    static readonly TimeSpan ResolveTimeout = TimeSpan.FromMilliseconds(1500);
    static readonly TimeSpan ThumbnailTimeout = TimeSpan.FromSeconds(5);

    // Asking Explorer for every item of a huge view would take seconds.
    const int MaxViewItems = 4000;
    const int ViewBudgetMs = 600;
    const int ViewCacheMs = 2000;

    readonly StaLane _resolveLane = new("Shell resolver");
    readonly StaLane _thumbnailLane = new("Shell thumbnails"); // a slow thumbnail mustn't delay the next hover
    volatile ViewListing? _listing;

    readonly record struct ViewItem(string Name, string Path, bool IsFolder);

    sealed record ViewListing(nint View, string? Folder, int Count, long Time, IReadOnlyList<ViewItem> Items);

    public Task<HoveredFile?> ResolveAsync(POINT pt) => _resolveLane.Invoke(() => Resolve(pt), ResolveTimeout);

    public Task<BitmapSource?> GetThumbnailAsync(string path, int size) => _thumbnailLane.Invoke(() => Thumbnail(path, size), ThumbnailTimeout);

    /// <summary>Cheap pre-check: is the point over a shell item view (Explorer or desktop)?</summary>
    public static bool IsOverShellView(POINT pt) => FindDefView(Win32.WindowFromPoint(pt)) != 0;

    static nint FindDefView(nint hwnd)
    {
        for (int i = 0; hwnd != 0 && i < 8; i++)
        {
            if (Win32.ClassName(hwnd) == "SHELLDLL_DefView") return hwnd;
            hwnd = Win32.GetAncestor(hwnd, Win32.GA_PARENT);
        }
        return 0;
    }

    HoveredFile? Resolve(POINT pt)
    {
        var defView = FindDefView(Win32.WindowFromPoint(pt));
        if (defView == 0) return null;
        var element = HoveredItem(pt);
        return element == null ? null : Resolve(defView, element);
    }

    HoveredFile? Resolve(nint defView, AutomationElement element)
    {
        var name = Clean(element.Current.Name);
        if (name.Length == 0) return null;
        var view = FindFolderView(defView);
        if (view == null) return null;
        try
        {
            var path = FindPath(defView, view, element, name);
            if (path == null || !File.Exists(path)) return null;
            var (selection, count) = Selection(view, path);
            return new HoveredFile(path, selection, count);
        }
        finally
        {
            Marshal.ReleaseComObject(view);
        }
    }

    string? FindPath(nint defView, IFolderView2 view, AutomationElement element, string name)
    {
        // Explorer numbers its items, which is exact whatever they're called. (Grouped views number each group.)
        if (int.TryParse(element.Current.AutomationId, out int index) && index >= 0 && !IsGrouped(view)
            && ItemAt(view, index) is { } item && SameText(item.Name, name))
            return item.IsFolder ? null : item.Path;

        var type = new Lazy<string>(() => ShownType(element));
        var folder = FolderPath(view);
        // The desktop shows two folders at once (yours and everyone's), so there only the view itself knows.
        bool merged = folder == null || SameText(folder, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (!merged && OnDisk(folder!, name, type.Value) is { } onDisk) return onDisk;
        return InView(defView, view, folder, name, type);
    }

    /// <summary>Looks the name up in the folder itself: a couple of direct checks, however big the folder is.</summary>
    static string? OnDisk(string folder, string name, string type)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        const int enough = 100; // more than this many files called "name.something" is too many to tell apart
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(folder, name) };
        try
        {
            // Extensions may be hidden: "song" could be song.ogg, song.mp3, ...
            paths.UnionWith(Directory.EnumerateFileSystemEntries(folder, name + ".*").Take(enough));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (paths.Count >= enough) return null;

        var candidates = new List<ItemCandidate>();
        foreach (var path in paths)
        {
            bool isFolder = Directory.Exists(path);
            if (isFolder || File.Exists(path))
                candidates.Add(new ItemCandidate(path, ShownName(path), TypeName(path, isFolder), isFolder));
        }
        return HoverMatch.Choose(name, type, candidates, complete: false);
    }

    /// <summary>Looks through the view's own items (libraries, search results, the desktop, Home).</summary>
    string? InView(nint defView, IFolderView2 view, string? folder, string name, Lazy<string> type)
    {
        if (view.ItemCount(ShellIds.SVGIO_ALLVIEW, out int count) != 0) return null;
        if (_listing is { } cached && cached.View == defView && cached.Folder == folder && cached.Count == count
            && Environment.TickCount64 - cached.Time < ViewCacheMs && Pick(cached) is { } known)
            return known;

        var fresh = List(defView, view, folder, count);
        _listing = fresh;
        return fresh == null ? null : Pick(fresh); // not read in full: a match might not be the only one

        string? Pick(ViewListing listing)
        {
            var named = listing.Items.Where(i => SameText(i.Name, name)).ToList();
            bool needType = named.Count > 1;
            return HoverMatch.Choose(name, needType ? type.Value : "",
                named.Select(i => new ItemCandidate(i.Path, i.Name, needType ? TypeName(i.Path, i.IsFolder) : "", i.IsFolder)).ToList(),
                complete: true);
        }
    }

    static ViewListing? List(nint defView, IFolderView2 view, string? folder, int count)
    {
        if (count > MaxViewItems) return null;
        var clock = Stopwatch.StartNew();
        var items = new List<ViewItem>(count);
        for (int i = 0; i < count; i++)
        {
            if (clock.ElapsedMilliseconds > ViewBudgetMs) return null;
            if (ItemAt(view, i) is { } item) items.Add(item);
        }
        return new ViewListing(defView, folder, count, Environment.TickCount64, items);
    }

    static ViewItem? ItemAt(IFolderView2 view, int index)
    {
        var iid = ShellIds.IID_IShellItem;
        if (view.GetItem(index, ref iid, out var obj) != 0 || obj == null) return null;
        try
        {
            return obj is IShellItem item && DisplayName(item, ShellIds.SIGDN_NORMALDISPLAY) is { Length: > 0 } name
                ? new ViewItem(name, DisplayName(item, ShellIds.SIGDN_FILESYSPATH) ?? "", IsFolder(item))
                : null;
        }
        finally { Marshal.ReleaseComObject(obj); }
    }

    /// <summary>A real folder. (ZIPs and the like also open as folders, but they're files.)</summary>
    static bool IsFolder(IShellItem item)
    {
        const uint mask = ShellIds.SFGAO_FOLDER | ShellIds.SFGAO_STREAM;
        return item.GetAttributes(mask, out var attributes) >= 0 && (attributes & mask) == ShellIds.SFGAO_FOLDER;
    }

    static bool IsGrouped(IFolderView2 view) => view.GetGroupBy(out var key, out _) < 0 || key.fmtid != Guid.Empty;

    /// <summary>The selected files, when the hovered one is part of the selection.</summary>
    static (IReadOnlyList<string> Files, int Count) Selection(IFolderView2 view, string hovered)
    {
        const int limit = 200;
        if (view.GetSelection(0, out var obj) != 0 || obj == null) return ([], 0);
        try
        {
            if (obj is not IShellItemArray array || array.GetCount(out int count) != 0 || count < 2) return ([], 0);
            var files = new List<string>();
            for (int i = 0; i < Math.Min(count, limit); i++)
            {
                if (array.GetItemAt(i, out var item) != 0 || item == null) continue;
                try
                {
                    if (!IsFolder(item) && DisplayName(item, ShellIds.SIGDN_FILESYSPATH) is { } path) files.Add(path);
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            return files.Contains(hovered, StringComparer.OrdinalIgnoreCase) ? (files, count) : ([], 0);
        }
        finally { Marshal.ReleaseComObject(obj); }
    }

    static AutomationElement? HoveredItem(POINT pt)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
        var walker = TreeWalker.ControlViewWalker;
        for (int i = 0; element != null && i < 6; i++)
        {
            var type = element.Current.ControlType;
            if (type == ControlType.ListItem || type == ControlType.DataItem) return element;
            if (type == ControlType.List || type == ControlType.Window || type == ControlType.Pane ||
                type == ControlType.DataGrid || type == ControlType.Tree || type == ControlType.Header)
                break;
            element = walker.GetParent(element);
        }
        return null;
    }

    /// <summary>The item's "Type" text (details and tiles views show it), or "".</summary>
    static string ShownType(AutomationElement element)
    {
        var cell = element.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "System.ItemTypeText"));
        return cell != null && cell.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
            ? Clean(((ValuePattern)value).Current.Value)
            : Clean(element.Current.ItemType);
    }

    static bool SameText(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Without the invisible direction marks Explorer adds.</summary>
    static string Clean(string? text) =>
        new string((text ?? "").Where(c => c is not ('‎' or '‏' or (>= '‪' and <= '‮'))).ToArray()).Trim();

    static IFolderView2? FindFolderView(nint defView)
    {
        var type = Type.GetTypeFromCLSID(ShellIds.CLSID_ShellWindows)!;
        var windows = (IShellWindows)Activator.CreateInstance(type)!;
        try
        {
            var root = Win32.ClassName(Win32.GetAncestor(defView, Win32.GA_ROOT));
            if (root is "Progman" or "WorkerW")
            {
                object loc = null!, locRoot = null!;
                var desktop = windows.FindWindowSW(ref loc, ref locRoot, ShellIds.SWC_DESKTOP, out _, ShellIds.SWFO_NEEDDISPATCH);
                return ViewIfMatches(desktop, defView);
            }

            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                object? window = null;
                try { window = windows.Item(i); } catch (COMException) { }
                var view = ViewIfMatches(window, defView);
                if (view != null) return view;
            }
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(windows);
        }
    }

    static IFolderView2? ViewIfMatches(object? window, nint defView)
    {
        if (window == null) return null;
        object? browserObj = null, viewObj = null;
        try
        {
            if (window is not IComServiceProvider provider) return null;
            var sid = ShellIds.SID_STopLevelBrowser;
            var iid = ShellIds.IID_IShellBrowser;
            if (provider.QueryService(ref sid, ref iid, out browserObj) != 0) return null;
            if (((IShellBrowser)browserObj).QueryActiveShellView(out viewObj) != 0 || viewObj == null) return null;
            if (((IShellView)viewObj).GetWindow(out var hwnd) != 0 || hwnd != defView) return null;
            var result = (IFolderView2)viewObj;
            viewObj = null; // ownership moves to the caller
            return result;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (viewObj != null) Marshal.ReleaseComObject(viewObj);
            if (browserObj != null) Marshal.ReleaseComObject(browserObj);
            Marshal.ReleaseComObject(window);
        }
    }

    static string? FolderPath(IFolderView2 view)
    {
        var iid = ShellIds.IID_IPersistFolder2;
        if (view.GetFolder(ref iid, out var obj) != 0 || obj == null) return null;
        try
        {
            if (((IPersistFolder2)obj).GetCurFolder(out var pidl) != 0 || pidl == 0) return null;
            try
            {
                if (Win32.SHGetNameFromIDList(pidl, ShellIds.SIGDN_FILESYSPATH, out var name) != 0) return null;
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        finally { Marshal.ReleaseComObject(obj); }
    }

    static string? DisplayName(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var ptr) != 0 || ptr == 0) return null;
        try { return sigdn == ShellIds.SIGDN_FILESYSPATH ? Marshal.PtrToStringUni(ptr) : Clean(Marshal.PtrToStringUni(ptr)); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>The name Explorer shows for a file, following the "hide extensions" setting.</summary>
    static string ShownName(string path)
    {
        const uint SHGFI_DISPLAYNAME = 0x200;
        var info = new SHFILEINFO();
        Win32.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
        return Clean(info.szDisplayName);
    }

    static string TypeName(string path, bool isFolder)
    {
        const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;
        var info = new SHFILEINFO();
        Win32.SHGetFileInfo(path, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        return Clean(info.szTypeName);
    }

    static BitmapSource? Thumbnail(string path, int size)
    {
        var iid = ShellIds.IID_IShellItemImageFactory;
        if (Win32.SHCreateItemFromParsingName(path, 0, ref iid, out var obj) != 0 || obj is not IShellItemImageFactory factory)
            return null;
        try
        {
            if (factory.GetImage(new SIZE(size, size), 0, out var hbitmap) != 0 || hbitmap == 0) return null;
            try { return FromHBitmap(hbitmap); }
            finally { Win32.DeleteObject(hbitmap); }
        }
        finally { Marshal.ReleaseComObject(obj); }
    }

    /// <summary>Copies a 32bpp HBITMAP keeping its alpha channel (Imaging.CreateBitmapSourceFromHBitmap drops it).</summary>
    static BitmapSource? FromHBitmap(nint hbitmap)
    {
        if (Win32.GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        var pixels = new byte[width * height * 4];
        var hdc = Win32.GetDC(0);
        try
        {
            if (Win32.GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;
        }
        finally { Win32.ReleaseDC(0, hdc); }

        // Thumbnails of opaque images sometimes come back with an all-zero alpha channel.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    public void Dispose()
    {
        _resolveLane.Dispose();
        _thumbnailLane.Dispose();
    }
}
