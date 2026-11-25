using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConvertWheel.Conversion;

/// <summary>
/// A Windows job object that ends its processes when this app ends, even after a crash, so a converter
/// we started can never be left running on its own.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    const int ExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;

    static readonly Lazy<ProcessJob> Tools = new(() => new ProcessJob());
    readonly nint _handle;

    /// <summary>Ties a command-line tool to this app's lifetime.</summary>
    public static void Attach(Process process) => Tools.Value.Add(process);

    public ProcessJob()
    {
        _handle = CreateJobObject(0, null);
        if (_handle == 0 || !SetLimit(KillOnJobClose)) Log.Write($"No job object (error {Marshal.GetLastWin32Error()}).");
    }

    public void Add(Process process)
    {
        try
        {
            if (!AssignProcessToJobObject(_handle, process.Handle) && !process.HasExited)
                Log.Write($"Couldn't tie {process.Id} to this app (error {Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // already gone
    }

    /// <summary>Lets go of the processes: closing the job no longer ends them.</summary>
    public void LetGo() => SetLimit(0);

    /// <summary>Ends whatever is still in the job, unless <see cref="LetGo"/> was called.</summary>
    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
    }

    bool SetLimit(uint flags)
    {
        var info = new ExtendedLimits { Flags = flags };
        return SetInformationJobObject(_handle, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimits>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint Flags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateJobObject(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
