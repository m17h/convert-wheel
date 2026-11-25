using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ConvertWheel.Conversion;

/// <summary>
/// Moves a file to the Recycle Bin, or leaves it exactly where it is. Windows' simpler calls quietly delete
/// for good when there's no bin (network folders, many USB drives, files too big for it, or a bin that's
/// switched off), so this one asks the shell what it's about to do and refuses anything but recycling.
/// </summary>
internal static class RecycleBin
{
    public enum Outcome
    {
        Recycled,
        /// <summary>It couldn't be recycled, so it's still there.</summary>
        Kept,
        /// <summary>It's gone, but the shell didn't confirm where to. Should never happen.</summary>
        Unconfirmed,
    }

    const uint AllowUndo = 0x40, NoConfirmation = 0x10, Silent = 0x4, NoErrorUi = 0x400, NoConnectedElements = 0x2000,
        RecycleOnDelete = 0x80000, EarlyFailure = 0x100000;
    const uint RecycleIfPossible = 0x80; // TSF_DELETE_RECYCLE_IF_POSSIBLE
    const int Abort = unchecked((int)0x80004004);

    /// <summary>Runs on its own STA thread, as the shell requires; call from a background thread.</summary>
    public static Outcome Send(string path)
    {
        var outcome = Outcome.Kept;
        var thread = new Thread(() =>
        {
            try
            {
                if (!HasBin(path)) return;
                var sink = new Sink();
                try { Delete(path, sink); }
                catch (Exception ex) { Log.Write($"Couldn't recycle {path}: {ex.Message}"); }
                outcome = sink.Recycled ? Outcome.Recycled : Exists(path) ? Outcome.Kept : Outcome.Unconfirmed;
                if (outcome != Outcome.Recycled) Log.Write($"{path} wasn't recycled ({outcome}{(sink.Refused ? ", no Recycle Bin for it" : "")}).");
            }
            catch (Exception ex)
            {
                Log.Write(ex);
            }
        })
        { IsBackground = true, Name = "Recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return outcome;
    }

    static void Delete(string path, Sink sink)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var item));
        var operation = (IFileOperation)new FileOperation();
        uint cookie = 0;
        try
        {
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(AllowUndo | NoConfirmation | Silent | NoErrorUi | NoConnectedElements | RecycleOnDelete | EarlyFailure));
            // Without the sink nothing would stop a permanent delete, so a failure here must end it.
            Marshal.ThrowExceptionForHR(operation.Advise(sink, out cookie));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, null));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
        }
        finally
        {
            if (cookie != 0) operation.Unadvise(cookie);
            Marshal.FinalReleaseComObject(operation);
            Marshal.FinalReleaseComObject(item);
        }
    }

    /// <summary>Rules out the places that clearly have no bin before the shell is even asked.</summary>
    static bool HasBin(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return Refuse("it's on the network");
        var type = new DriveInfo(root).DriveType;
        if (type is DriveType.Network or DriveType.CDRom or DriveType.Ram or DriveType.NoRootDirectory) return Refuse($"it's on a {type} drive");
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            if (Value(hive, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoRecycleFiles") is > 0) return Refuse("a policy turns the bin off");

        var name = new char[64];
        if (GetVolumeNameForVolumeMountPoint(root, name, name.Length))
        {
            // \\?\Volume{guid}\
            var volume = new string(name).TrimEnd('\0');
            int open = volume.IndexOf('{'), close = volume.IndexOf('}');
            if (open > 0 && close > open)
            {
                var key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + volume[open..(close + 1)];
                if (Value(Registry.CurrentUser, key, "NukeOnDelete") is > 0) return Refuse("the bin is turned off for this drive");
                if (Value(Registry.CurrentUser, key, "MaxCapacity") is { } megabytes && new FileInfo(path).Length > (long)(uint)megabytes << 20)
                    return Refuse("it's bigger than the bin");
            }
        }
        return true;

        bool Refuse(string why)
        {
            Log.Write($"{path} can't be recycled: {why}.");
            return false;
        }
    }

    static int? Value(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>File.Exists also says no when access is denied, so only a definite "not found" counts as gone.</summary>
    static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch { return true; }
    }

    /// <summary>Hears about the delete before it happens, and can veto it.</summary>
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class Sink : IFileOperationProgressSink
    {
        public bool Recycled { get; private set; }
        public bool Refused { get; private set; }

        public int PreDeleteItem(uint flags, nint item)
        {
            if ((flags & RecycleIfPossible) != 0) return 0;
            Refused = true;
            return Abort; // it would be deleted for good
        }

        public int PostDeleteItem(uint flags, nint item, int result, nint newlyCreated)
        {
            Recycled = result >= 0 && newlyCreated != 0;
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, nint item, nint name) => 0;
        public int PostRenameItem(uint flags, nint item, nint name, int result, nint created) => 0;
        public int PreMoveItem(uint flags, nint item, nint folder, nint name) => 0;
        public int PostMoveItem(uint flags, nint item, nint folder, nint name, int result, nint created) => 0;
        public int PreCopyItem(uint flags, nint item, nint folder, nint name) => 0;
        public int PostCopyItem(uint flags, nint item, nint folder, nint name, int result, nint created) => 0;
        public int PreNewItem(uint flags, nint folder, nint name) => 0;
        public int PostNewItem(uint flags, nint folder, nint name, nint template, uint attributes, int result, nint created) => 0;
        public int UpdateProgress(uint total, uint soFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    class FileOperation;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem;

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem(nint item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(nint item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        [PreserveSig] int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(nint item, nint folder, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        [PreserveSig] int MoveItems(nint items, nint folder);
        [PreserveSig] int CopyItem(nint item, nint folder, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        [PreserveSig] int CopyItems(nint items, nint folder);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(nint folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted(out bool aborted);
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint name);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint name, int result, nint created);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint folder, nint name);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint folder, nint name, int result, nint created);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint folder, nint name);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint folder, nint name, int result, nint created);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int result, nint newlyCreated);
        [PreserveSig] int PreNewItem(uint flags, nint folder, nint name);
        [PreserveSig] int PostNewItem(uint flags, nint folder, nint name, nint template, uint attributes, int result, nint created);
        [PreserveSig] int UpdateProgress(uint total, uint soFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, out IShellItem item);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] name, int length);
}
