using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConvertWheel.Conversion;

/// <summary>
/// Uses Microsoft Word, Excel or PowerPoint, when installed, to convert their own formats with
/// perfect fidelity. The apps run invisibly and are closed afterwards; a copy the user already
/// has open is never touched.
/// </summary>
internal static class OfficeAutomation
{
    sealed record OfficeApp(string ProgId, string Process, string Name, HashSet<string> Inputs, Dictionary<string, int> Outputs);

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
        var started = new HashSet<int>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(app, plan.SourcePath, output, app.Outputs[target.Ext], started);
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
        var timeout = Task.Delay(target.Ext == "pdf" ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3), ct);
        if (await Task.WhenAny(tcs.Task, timeout) != tcs.Task)
        {
            // Hung on an invisible dialog or cancelled: end only the instance we started.
            lock (started)
                foreach (var id in started)
                    try { Process.GetProcessById(id).Kill(); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new ConversionException($"Microsoft {app.Name} stopped responding.");
        }
        await tcs.Task;
    }

    static void Run(OfficeApp office, string input, string output, int format, HashSet<int> started)
    {
        object m = Type.Missing;
        var before = Process.GetProcessesByName(office.Process).Select(p => p.Id).ToHashSet();
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID(office.ProgId)!)!;
        var ours = Process.GetProcessesByName(office.Process).Where(p => !before.Contains(p.Id)).Select(p => p.Id).ToList();
        lock (started) started.UnionWith(ours);
        bool weStarted = ours.Count > 0;
        dynamic? file = null, files = null;

        try
        {
            try { app.AutomationSecurity = 3; } catch { } // never run macros in the file
            switch (office.Name)
            {
                case "Word":
                    app.Visible = false;
                    app.DisplayAlerts = 0;
                    // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, …, Visible (12th), …, NoEncodingDialog (15th)
                    files = app.Documents;
                    file = files.Open(input, false, true, false, m, m, m, m, m, m, m, false, m, m, true);
                    if (format == 7) file.SaveAs2(output, format, m, m, false, m, m, m, m, m, m, 65001); // UTF-8 text
                    else file.SaveAs2(output, format);
                    file.Close(0);
                    break;

                case "Excel":
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    try { app.AskToUpdateLinks = false; app.EnableEvents = false; } catch { }
                    files = app.Workbooks;
                    file = files.Open(input, 0, true);
                    if (format == -1) file.ExportAsFixedFormat(0, output);
                    else
                    {
                        try { file.SaveAs(output, format); }
                        catch (COMException) when (format == 62) { file.SaveAs(output, 6); } // older Excel: no UTF-8 CSV
                    }
                    file.Close(false);
                    break;

                case "PowerPoint":
                    files = app.Presentations;
                    file = files.Open(input, -1, 0, 0); // ReadOnly, not Untitled, no window
                    file.SaveAs(output, format);
                    file.Close();
                    break;
            }
        }
        finally
        {
            // Every COM object we touched must be released, or the hidden Office app never exits.
            if (file != null) Marshal.FinalReleaseComObject(file);
            if (files != null) Marshal.FinalReleaseComObject(files);
            if (weStarted)
            {
                try { app.Quit(); } catch { }
            }
            Marshal.FinalReleaseComObject(app);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
