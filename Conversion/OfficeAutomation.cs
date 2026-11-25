using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ConvertWheel.Native;

namespace ConvertWheel.Conversion;

/// <summary>
/// Uses Microsoft Word, Excel or PowerPoint, when installed, to convert their own formats with
/// perfect fidelity. The apps run invisibly and are closed afterwards; a copy the user already
/// has open is never touched.
/// </summary>
internal static class OfficeAutomation
{
    sealed record OfficeApp(string ProgId, string Process, string Name, string WindowClass, HashSet<string> Inputs, Dictionary<string, int> Outputs)
    {
        public readonly SemaphoreSlim CreationGate = new(1, 1);
    }

    static readonly OfficeApp Word = new("Word.Application", "WINWORD", "Word", "OpusApp",
        ["doc", "docx", "odt", "rtf", "txt"],
        new() { ["pdf"] = 17, ["docx"] = 16, ["odt"] = 23, ["rtf"] = 6, ["txt"] = 7 });

    static readonly OfficeApp Excel = new("Excel.Application", "EXCEL", "Excel", "XLMAIN",
        ["xls", "xlsx", "ods", "csv"],
        new() { ["pdf"] = -1, ["xlsx"] = 51, ["ods"] = 60, ["csv"] = 62 });

    static readonly OfficeApp PowerPoint = new("PowerPoint.Application", "POWERPNT", "PowerPoint", "PPTFrameClass",
        ["ppt", "pptx", "odp"],
        new() { ["pdf"] = 32, ["pptx"] = 24, ["odp"] = 35 });

    static readonly OfficeApp[] Apps = [Word, Excel, PowerPoint];

    public static bool IsInstalled(string name) => Apps.First(a => a.Name == name) is var app && Installed(app);

    static bool Installed(OfficeApp app) => Type.GetTypeFromProgID(app.ProgId) != null;

    static OfficeApp? AppFor(ConversionPlan plan, FormatInfo target) =>
        Apps.FirstOrDefault(a => a.Inputs.Contains(plan.SourceExt) && a.Outputs.ContainsKey(target.Ext) && Installed(a));

    public static bool CanConvert(ConversionPlan plan, FormatInfo target) => AppFor(plan, target) != null;

    public static async Task ConvertAsync(ConversionPlan plan, FormatInfo target, string output, CancellationToken ct)
    {
        var app = AppFor(plan, target) ?? throw new ConversionException("Microsoft Office can't do this conversion.");
        ct.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var owned = new OwnedProcess(app);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A timed-out caller no longer awaits the STA's eventual COM failure.
        _ = tcs.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var thread = new Thread(() =>
        {
            try
            {
                Run(app, plan.SourcePath, output, app.Outputs[target.Ext], owned, token);
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
            owned.Kill();
            ct.ThrowIfCancellationRequested();
            throw new ConversionException($"Microsoft {app.Name} stopped responding.");
        }
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

    /// <summary>The process behind this very instance, from one of its own windows; null if it has none to ask.</summary>
    static int? InstanceProcessId(OfficeApp office, dynamic app, bool untouched)
    {
        dynamic? documents = null, window = null;
        try
        {
            if (office.Name == "Word")
            {
                documents = app.Documents;
                if (documents.Count > 0)
                {
                    window = app.ActiveWindow;
                    return ProcessOf((nint)(int)window.Hwnd);
                }
                // Word has no window handle to give without a document, so look for the window by a name only this instance has.
                if (!untouched) return null;
                var caption = $"Convert Wheel {Guid.NewGuid():N}";
                app.Caption = caption;
                nint found = 0;
                EnumWindows((hwnd, _) =>
                {
                    if (Win32.ClassName(hwnd) != office.WindowClass || !Title(hwnd).Contains(caption)) return true;
                    found = hwnd;
                    return false;
                }, 0);
                return found != 0 ? ProcessOf(found) : null;
            }
            return ProcessOf(office.Name == "Excel" ? (nint)(int)app.Hwnd : (nint)(int)app.HWND);
        }
        catch { return null; }
        finally
        {
            Release(window);
            Release(documents);
        }
    }

    static int? ProcessOf(nint hwnd) => hwnd != 0 && Win32.GetWindowThreadProcessId(hwnd, out uint id) != 0 ? (int)id : null;

    /// <summary>
    /// Whether nobody is using this instance: created by a program, hidden, with nothing open. A new one always
    /// is; one the user works in never is. Any doubt counts as "in use".
    /// </summary>
    static bool IsUntouched(OfficeApp office, dynamic app)
    {
        dynamic? documents = null;
        try
        {
            documents = office.Name switch { "Word" => app.Documents, "Excel" => app.Workbooks, _ => app.Presentations };
            if ((int)documents.Count > 0) return false;
            if (office.Name == "PowerPoint") return (int)app.Visible == 0; // msoFalse
            return !(bool)app.UserControl && !(bool)app.Visible;
        }
        catch { return false; }
        finally { Release(documents); }
    }

    /// <summary>The Office process a conversion started itself, proven by one of its own windows.</summary>
    sealed class OwnedProcess(OfficeApp office)
    {
        readonly object _gate = new();
        Process? _process;
        ProcessJob? _job;

        public bool Proven { get { lock (_gate) return _process != null; } }

        /// <summary>Takes ownership if that process didn't exist before we asked for a new instance.</summary>
        public void Claim(int? id, HashSet<int> before, DateTime asked, CancellationToken ct)
        {
            if (id is not { } pid || before.Contains(pid)) return;
            Process? process = null;
            try
            {
                process = Process.GetProcessById(pid);
                _ = process.Handle; // Pin the process identity so PID reuse cannot target another app.
                if (!process.ProcessName.Equals(office.Process, StringComparison.OrdinalIgnoreCase) || process.StartTime < asked.AddSeconds(-2))
                {
                    process.Dispose();
                    return;
                }
                lock (_gate)
                {
                    _process = process;
                    _job = new ProcessJob();
                    _job.Add(process); // it must not outlive a crash of this app
                }
                if (ct.IsCancellationRequested) Kill(); // Creation may finish after the caller gave up.
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process?.Dispose();
            }
        }

        /// <summary>Ends it, unless it has a window on screen: the user may have opened something in it meanwhile.</summary>
        public void Kill()
        {
            lock (_gate)
            {
                if (_process == null || HasWindow(_process.Id, office.WindowClass)) return;
                try { _process.Kill(); } catch { }
            }
        }

        public void LetGo()
        {
            lock (_gate)
            {
                _job?.LetGo();
                _job?.Dispose();
                _process?.Dispose();
                _job = null;
                _process = null;
            }
        }
    }

    static bool HasWindow(int processId, string windowClass)
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            found = IsWindowVisible(hwnd) && ProcessOf(hwnd) == processId && Win32.ClassName(hwnd) == windowClass;
            return !found;
        }, 0);
        return found;
    }

    static string Title(nint hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder text, int max);

    static void Run(OfficeApp office, string input, string output, int format, OwnedProcess owned, CancellationToken ct)
    {
        // A wrong password makes a protected file fail at once instead of asking on a screen nobody sees;
        // Office ignores it for files that don't have one.
        const string NoPassword = "\u0001convert-wheel";
        object m = Type.Missing;
        bool mayQuit = false;
        int? macroSetting = null;
        dynamic? app = null, file = null, files = null;

        try
        {
            office.CreationGate.Wait(ct);
            var before = ProcessIds(office);
            var asked = DateTime.Now;
            try
            {
                ct.ThrowIfCancellationRequested();
                app = Activator.CreateInstance(Type.GetTypeFromProgID(office.ProgId)!)!;
                bool untouched = IsUntouched(office, app);
                owned.Claim(InstanceProcessId(office, app, untouched), before, asked, ct);
                // PowerPoint is a single instance, so it being idle doesn't make it ours.
                mayQuit = owned.Proven || untouched && office.Name != "PowerPoint";
            }
            finally { office.CreationGate.Release(); }
            ct.ThrowIfCancellationRequested();
            // Word and Excel get hidden and silenced below, which must never happen to the user's own window.
            // PowerPoint is single-instance and opens our file windowless without changing the app, so sharing is fine.
            if (!mayQuit && office.Name != "PowerPoint")
                throw new ConversionException($"Microsoft {office.Name} is already in use; using another converter.");
            // Never run macros in the file. The user may share this PowerPoint, so its setting goes back afterwards.
            try
            {
                macroSetting = (int)app.AutomationSecurity;
                app.AutomationSecurity = 3;
            }
            catch { }
            switch (office.Name)
            {
                case "Word":
                    app.Visible = false;
                    app.DisplayAlerts = 0;
                    files = app.Documents;
                    // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate, Revert,
                    // WritePasswordDocument, WritePasswordTemplate, Format, Encoding, Visible (12th), …, NoEncodingDialog (15th)
                    file = files.Open(input, false, true, false, NoPassword, NoPassword, m, NoPassword, NoPassword, m, m, false, m, m, true);
                    if (!owned.Proven) owned.Claim(InstanceProcessId(office, app, false), before, asked, ct);
                    ct.ThrowIfCancellationRequested();
                    if (format == 7) file.SaveAs2(output, format, m, m, false, m, m, m, m, m, m, 65001); // UTF-8 text
                    else file.SaveAs2(output, format);
                    break;

                case "Excel":
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    try { app.AskToUpdateLinks = false; app.EnableEvents = false; } catch { }
                    files = app.Workbooks;
                    // Filename, UpdateLinks, ReadOnly, Format, Password, WriteResPassword, IgnoreReadOnlyRecommended
                    file = files.Open(input, 0, true, m, NoPassword, NoPassword, true);
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
                    if (macroSetting is { } setting)
                        try { app.AutomationSecurity = setting; } catch { }
                    // Windows may have opened one of the user's files in our hidden instance meanwhile: then it's theirs now.
                    if (mayQuit && IsUntouched(office, app))
                    {
                        try { app.Quit(); } catch { }
                    }
                    else if (mayQuit)
                    {
                        Log.Write($"Left Microsoft {office.Name} open: something else is using it.");
                        try { app.Visible = office.Name == "PowerPoint" ? (object)-1 : true; } catch { }
                        if (office.Name != "PowerPoint") try { app.DisplayAlerts = office.Name == "Word" ? (object)-1 : true; } catch { }
                    }
                    Release(app);
                }
            }
            finally
            {
                owned.LetGo();
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
