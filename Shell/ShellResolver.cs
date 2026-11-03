using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConvertWheel.Native;

namespace ConvertWheel.Shell;

/// <summary>
/// Finds the file under the mouse in Explorer windows (including tabs) and on the desktop.
/// UI Automation gives the hovered item's display name; the Explorer view hosting it gives the
/// folder, and together they produce a real path. COM work runs on an STA worker, replaced if it hangs.
/// </summary>
internal sealed class ShellResolver : IDisposable
{
    sealed record Work(Action Run, Action Cancel);

    sealed class Worker
    {
        public readonly BlockingCollection<Work> Queue = new(32);
        public bool Retired;
    }

    readonly object _gate = new();
    Worker? _worker;
    Work? _pendingResolve;
    bool _disposed;

    Worker StartWorker()
    {
        var worker = new Worker();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var work in worker.Queue.GetConsumingEnumerable())
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_pendingResolve, work)) _pendingResolve = null;
                        if (worker.Retired) { work.Cancel(); continue; }
                    }
                    work.Run();
                }
            }
            finally { lock (_gate) worker.Queue.Dispose(); }
        })
        { IsBackground = true, Name = "Shell resolver" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return worker;
    }

    async Task<T?> Invoke<T>(Func<T?> func, TimeSpan timeout, bool resolve = false) where T : class
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(() =>
        {
            if (tcs.Task.IsCompleted) return;
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { Log.Write(ex); tcs.TrySetResult(null); }
        }, () => tcs.TrySetResult(null));
        Worker worker;
        lock (_gate)
        {
            if (_disposed) return null;
            worker = _worker ??= StartWorker();
            if (resolve)
            {
                _pendingResolve?.Cancel(); // Superseded requests need not enter Explorer at all.
                _pendingResolve = work;
            }
            if (!worker.Queue.TryAdd(work))
            {
                if (ReferenceEquals(_pendingResolve, work)) _pendingResolve = null;
                work.Cancel();
            }
        }
        try { return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (!tcs.Task.IsCompleted && ReferenceEquals(_worker, worker))
                {
                    Retire(worker);
                    _worker = null; // The next request gets a fresh STA, not the hung provider.
                }
            }
            return null;
        }
    }

    void Retire(Worker worker)
    {
        worker.Retired = true;
        worker.Queue.CompleteAdding();
        while (worker.Queue.TryTake(out var work)) work.Cancel();
        _pendingResolve = null;
    }

    public Task<string?> ResolveFileAsync(POINT pt) => Invoke(() => ResolveFile(pt), TimeSpan.FromSeconds(3), resolve: true);

    public Task<BitmapSource?> GetThumbnailAsync(string path, int size) => Invoke(() => Thumbnail(path, size), TimeSpan.FromSeconds(5));

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

    static string? ResolveFile(POINT pt)
    {
        var defView = FindDefView(Win32.WindowFromPoint(pt));
        if (defView == 0) return null;

        var (name, itemType) = HoveredItem(pt);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var view = FindFolderView(defView);
        if (view == null) return null;
        try
        {
            var candidates = new List<string>();
            string? direct = null;
            var folder = FolderPath(view);
            if (folder != null)
            {
                direct = Path.Combine(folder, name);
                if (File.Exists(direct)) candidates.Add(direct);
                try
                {
                    // Extensions are hidden in Explorer: "song" could be song.ogg, song.mp3, ...
                    candidates.AddRange(Directory.EnumerateFiles(folder, name + ".*")
                        .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }

            // Virtual folders (Libraries, search results, the merged desktop): ask the view itself.
            if (candidates.Count == 0) candidates.AddRange(MatchViewItems(view, name));

            if (candidates.Count > 1 && !string.IsNullOrEmpty(itemType))
            {
                var typed = candidates.Where(c => string.Equals(TypeName(c), itemType, StringComparison.OrdinalIgnoreCase)).ToList();
                if (typed.Count > 0) candidates = typed;
            }
            var existing = candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (existing.Count == 1) return existing[0];
            // Still ambiguous: an exact name match (extensions shown) wins; otherwise don't guess.
            return existing.FirstOrDefault(c => string.Equals(c, direct, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Marshal.ReleaseComObject(view);
        }
    }

    static (string? name, string? itemType) HoveredItem(POINT pt)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
        var walker = TreeWalker.ControlViewWalker;
        for (int i = 0; element != null && i < 6; i++)
        {
            var type = element.Current.ControlType;
            if (type == ControlType.ListItem || type == ControlType.DataItem)
                return (Clean(element.Current.Name), Clean(element.Current.ItemType));
            if (type == ControlType.List || type == ControlType.Window || type == ControlType.Pane ||
                type == ControlType.DataGrid || type == ControlType.Tree || type == ControlType.Header)
                break;
            element = walker.GetParent(element);
        }
        return (null, null);
    }

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

    static IEnumerable<string> MatchViewItems(IFolderView2 view, string name)
    {
        var matches = new List<string>();
        if (view.ItemCount(ShellIds.SVGIO_ALLVIEW, out int count) != 0) return matches;
        var iid = ShellIds.IID_IShellItem;
        for (int i = 0; i < Math.Min(count, 20000); i++)
        {
            if (view.GetItem(i, ref iid, out var obj) != 0 || obj is not IShellItem item) continue;
            try
            {
                if (DisplayName(item, ShellIds.SIGDN_NORMALDISPLAY) == name || DisplayName(item, ShellIds.SIGDN_PARENTRELATIVE) == name)
                {
                    var path = DisplayName(item, ShellIds.SIGDN_FILESYSPATH);
                    if (path != null) matches.Add(path);
                }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        return matches;
    }

    static string? DisplayName(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var ptr) != 0 || ptr == 0) return null;
        try { return sigdn == ShellIds.SIGDN_FILESYSPATH ? Marshal.PtrToStringUni(ptr) : Clean(Marshal.PtrToStringUni(ptr)); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    static string TypeName(string path)
    {
        const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
        var info = new SHFILEINFO();
        Win32.SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        return info.szTypeName ?? "";
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_worker != null) Retire(_worker);
            _worker = null;
        }
    }
}
