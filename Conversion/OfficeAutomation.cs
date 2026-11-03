using System.Diagnostics;
using System.Runtime.InteropServices;
using ConvertWheel.Native;

namespace ConvertWheel.Conversion;

/// <summary>
/// Uses Microsoft Word, Excel or PowerPoint, when installed, to convert their own formats with
/// perfect fidelity. The apps run invisibly and are closed afterwards; a copy the user already
/// has open is never touched.
/// </summary>
internal static class OfficeAutomation
{
    sealed record OfficeApp(string ProgId, string Process, string Name, HashSet<string> Inputs, Dictionary<string, int> Outputs)
    {
        public readonly SemaphoreSlim CreationGate = new(1, 1);
    }

    static readonly OfficeApp Word = new("Word.Application", "WINWORD", "Word",
        ["doc", "docx", "odt", "rtf", "txt"],
        new() { ["pdf"] = 17, ["docx"] = 16, ["odt"] = 23, ["rtf"] = 6, ["txt"] = 7, ["html"] = 10 });

    static readonly OfficeApp Excel = new("Excel.Application", "EXCEL", "Excel",
        ["xls", "xlsx", "ods", "csv"],
        new() { ["pdf"] = -1, ["xlsx"] = 51, ["ods"] = 60, ["csv"] = 62, ["html"] = 44 });

    static readonly OfficeApp PowerPoint = new("PowerPoint.Application", "POWERPNT", "PowerPoint",
        ["ppt", "pptx", "odp"],
        new() { ["pdf"] = 32, ["pptx"] = 24, ["odp"] = 35 });

    static readonly OfficeApp[] Apps = [Word, Excel, PowerPoint];

    public static bool IsInstalled(string name) => Apps.First(a => a.Name == name) is var app && Installed(app);

    static bool Installed(OfficeApp app) => Type.GetTypeFromProgID(app.ProgId) != null;

    static OfficeApp? AppFor(ConversionPlan plan, FormatInfo target) =>
        Apps.FirstOrDefault(a => a.Inputs.Contains(plan.SourceExt) && a.Outputs.ContainsKey(target.Ext) && Installed(a));

    public static bool CanOpen(ConversionPlan plan) => Apps.Any(a => a.Inputs.Contains(plan.SourceExt) && Installed(a));

    public static bool CanConvert(ConversionPlan plan, FormatInfo target) => AppFor(plan, target) != null;

    public static async Task ConvertAsync(ConversionPlan plan, FormatInfo target, string output, CancellationToken ct)
    {
        var app = AppFor(plan, target) ?? throw new ConversionException("Microsoft Office can't do this conversion.");
        ct.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var started = new List<Process>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A timed-out caller no longer awaits the STA's eventual COM failure.
        _ = tcs.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var thread = new Thread(() =>
        {
            try
            {
                Run(app, plan.SourcePath, output, app.Outputs[target.Ext], started, token);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "Office automation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // PDF export goes through Office's print pipeline, which can stall for minutes on some PCs.
        try
        {
            await tcs.Task.WaitAsync(target.Ext == "pdf" ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Hung on an invisible dialog or cancelled: end only the instance we started.
            stop.Cancel();
            lock (started)
                foreach (var process in started) Kill(process);
            ct.ThrowIfCancellationRequested();
            throw new ConversionException($"Microsoft {app.Name} stopped responding.");
        }
    }

    static void Kill(Process process)
    {
        try { process.Kill(); } catch { }
    }

    static void Release(object? value)
    {
        if (value != null)
            try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    static HashSet<int> ProcessIds(OfficeApp office)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(office.Process))
            using (process) ids.Add(process.Id);
        return ids;
    }

    static int? InstanceProcessId(OfficeApp office, dynamic app)
    {
        dynamic? documents = null, window = null;
        try
        {
            nint hwnd;
            if (office.Name == "Word")
            {
                documents = app.Documents;
                if (documents.Count == 0) return null;
                window = app.ActiveWindow;
                hwnd = (nint)(int)window.Hwnd;
            }
            else hwnd = office.Name == "Excel" ? (nint)(int)app.Hwnd : (nint)(int)app.HWND;
            return Win32.GetWindowThreadProcessId(hwnd, out uint id) != 0 ? (int)id : null;
        }
        catch { return null; }
        finally
        {
            Release(window);
            Release(documents);
        }
    }

    static void Run(OfficeApp office, string input, string output, int format, List<Process> started, CancellationToken ct)
    {
        object m = Type.Missing;
        bool weStarted = false;
        dynamic? app = null, file = null, files = null;

        try
        {
            office.CreationGate.Wait(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var before = ProcessIds(office);
                app = Activator.CreateInstance(Type.GetTypeFromProgID(office.ProgId)!)!;
                var after = ProcessIds(office);
                int? actual = InstanceProcessId(office, app);
                var added = after.Except(before).ToArray();
                int? owned = actual.HasValue
                    ? (!before.Contains(actual.Value) && after.Contains(actual.Value) ? actual : null)
                    : added.Length == 1 ? added[0] : null;
                if (owned is { } id)
                {
                    weStarted = true;
                    var process = Process.GetProcessById(id);
                    try { _ = process.Handle; } // Pin the process identity so PID reuse cannot target another app.
                    catch { process.Dispose(); throw; }
                    lock (started)
                    {
                        started.Add(process);
                        if (ct.IsCancellationRequested) Kill(process); // Creation may finish after the caller gave up.
                    }
                }
            }
            finally { office.CreationGate.Release(); }
            ct.ThrowIfCancellationRequested();
            // Word and Excel get hidden and silenced below, which must never happen to the user's own window.
            // PowerPoint is single-instance and opens our file windowless without changing the app, so sharing is fine.
            if (!weStarted && office.Name != "PowerPoint")
                throw new ConversionException($"Microsoft {office.Name} is already in use; using another converter.");
            try { app.AutomationSecurity = 3; } catch { } // never run macros in the file
            switch (office.Name)
            {
                case "Word":
                    app.Visible = false;
                    app.DisplayAlerts = 0;
                    // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, …, Visible (12th), …, NoEncodingDialog (15th)
                    files = app.Documents;
                    file = files.Open(input, false, true, false, m, m, m, m, m, m, m, false, m, m, true);
                    ct.ThrowIfCancellationRequested();
                    if (format == 7) file.SaveAs2(output, format, m, m, false, m, m, m, m, m, m, 65001); // UTF-8 text
                    else file.SaveAs2(output, format);
                    break;

                case "Excel":
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    try { app.AskToUpdateLinks = false; app.EnableEvents = false; } catch { }
                    files = app.Workbooks;
                    file = files.Open(input, 0, true);
                    ct.ThrowIfCancellationRequested();
                    if (format == -1) file.ExportAsFixedFormat(0, output);
                    else
                    {
                        try { file.SaveAs(output, format); }
                        catch (COMException) when (format == 62) { file.SaveAs(output, 6); } // older Excel: no UTF-8 CSV
                    }
                    break;

                case "PowerPoint":
                    files = app.Presentations;
                    file = files.Open(input, -1, 0, 0); // ReadOnly, not Untitled, no window
                    ct.ThrowIfCancellationRequested();
                    file.SaveAs(output, format);
                    break;
            }
        }
        finally
        {
            // Every COM object we touched must be released, or the hidden Office app never exits.
            try
            {
                if (file != null)
                {
                    try
                    {
                        if (office.Name == "Word") file.Close(0);
                        else if (office.Name == "Excel") file.Close(false);
                        else file.Close();
                    }
                    catch { }
                    Release(file);
                }
                Release(files);
                if (app != null)
                {
                    if (weStarted)
                    {
                        try { app.Quit(); } catch { }
                    }
                    Release(app);
                }
            }
            finally
            {
                lock (started)
                {
                    foreach (var process in started) process.Dispose();
                    started.Clear();
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
