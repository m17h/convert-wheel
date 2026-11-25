using System.Text.RegularExpressions;

namespace ConvertWheel.Conversion;

/// <summary>
/// Holds the name a conversion will get, and the hidden folder next to it where the file is really made.
/// The finished file moves into place in one step, so a crash or cancel never leaves half a file that looks
/// done, and nothing but what this conversion made is ever deleted.
/// </summary>
internal sealed partial class OutputReservation : IDisposable
{
    const int MaxPath = 260;

    readonly string _folder, _stem, _ext, _staging;
    readonly Journal _journal;
    FileStream? _hold;
    int _number;

    /// <summary>Where the converter writes. Starts out as an empty file.</summary>
    public string WorkPath { get; }

    /// <param name="ext">The extension, which may have two parts ("tar.gz").</param>
    public OutputReservation(string folder, string stem, string ext)
    {
        (_folder, _stem, _ext) = (folder, stem, ext);
        _hold = Reserve();
        try
        {
            do _staging = Path.Combine(folder, $".cw-{Random.Shared.Next():x8}");
            while (Directory.Exists(_staging) || File.Exists(_staging));
            _journal = new Journal(_staging);
            try
            {
                Directory.CreateDirectory(_staging);
                File.SetAttributes(_staging, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.NotContentIndexed);
                WorkPath = Path.Combine(_staging, Path.GetFileName(_hold.Name));
                // The staging folder mustn't push a name Windows would accept over the limit, and "clip%03d" is a
                // numbered series to some encoders.
                if (ext != "md" && (WorkPath.Length >= MaxPath - 12 || stem.Contains('%'))) WorkPath = Path.Combine(_staging, $"o.{ext}");
                using (File.Create(WorkPath)) { }
            }
            catch
            {
                Remove(_staging);
                _journal.Done();
                throw;
            }
        }
        catch
        {
            _hold.Dispose();
            throw;
        }
    }

    string Name => _number == 0 ? _stem : $"{_stem} ({_number})";

    /// <summary>
    /// Creates the file now, so two simultaneous conversions can't pick the same name, and keeps it open:
    /// Windows removes it when the handle closes, even if the app crashes.
    /// </summary>
    FileStream Reserve()
    {
        for (int denied = 0; ; _number++)
        {
            var path = Path.Combine(_folder, $"{Name}.{_ext}");
            // Markdown's pictures go in a folder named after it, which the text refers to by name.
            if (_ext == "md" && Taken(Path.Combine(_folder, Name + "_images"))) continue;
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (NameExists(ex) || Taken(path)) { }
            catch (UnauthorizedAccessException) when (Taken(path)) { } // a folder has that name
            catch (UnauthorizedAccessException) when (++denied < 12)
            {
                // A file that's just being deleted says "access denied" for a moment.
                Thread.Sleep(40);
                if (denied % 4 != 0) _number--;
            }
        }
    }

    /// <summary>The error itself says so; by the time anyone looks, the other file may be gone again.</summary>
    static bool NameExists(Exception ex) => (ex.HResult & 0xFFFF) is 80 or 183;

    static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Moves the finished file (or folder of pages) into place and returns where it ended up.</summary>
    /// <param name="made">What the converter returned: <see cref="WorkPath"/>, or a folder next to it.</param>
    public string Commit(string made)
    {
        if (Directory.Exists(made)) return CommitFolder(made);
        for (int tries = 0; ; )
        {
            var final = Path.Combine(_folder, $"{Name}.{_ext}");
            Release();
            try
            {
                File.Move(made, final, overwrite: false);
                MoveCompanions();
                return final;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (NameExists(ex) || Taken(final))
                {
                    // Someone took the name in the instant it was free: use the next one.
                    _number++;
                    _hold = Reserve();
                }
                else if (++tries < 20) Thread.Sleep(100); // a virus scanner still has it open
                else throw;
            }
        }
    }

    /// <summary>Anything else the converter put next to its file (the pictures of a Markdown document).</summary>
    void MoveCompanions()
    {
        foreach (var extra in Directory.GetFileSystemEntries(_staging))
        {
            var final = Path.Combine(_folder, Path.GetFileName(extra));
            for (int tries = 0; ; tries++)
            {
                try
                {
                    if (Directory.Exists(extra)) Directory.Move(extra, final);
                    else File.Move(extra, final, overwrite: false);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (tries < 10 && !NameExists(ex) && !Taken(final))
                    {
                        Thread.Sleep(100);
                        continue;
                    }
                    Log.Write($"Couldn't put {final} in place: {ex.Message}");
                    throw new ConversionException($"The file was saved, but its pictures couldn't be put in \"{Path.GetFileName(final)}\".", ex.ToString());
                }
            }
        }
    }

    /// <summary>Several pages: a folder "name (PNG)" holding name-01.png, name-02.png…</summary>
    string CommitFolder(string made)
    {
        var workStem = Path.GetFileNameWithoutExtension(WorkPath);
        if (workStem != Name)
            foreach (var file in Directory.GetFiles(made, $"{workStem}-*"))
                File.Move(file, Path.Combine(made, Name + Path.GetFileName(file)[workStem.Length..]));
        var label = $"{Name} ({_ext.ToUpperInvariant()})";
        for (int i = 1, tries = 0; ; )
        {
            var folder = Path.Combine(_folder, i == 1 ? label : $"{label} ({i})");
            try
            {
                Directory.Move(made, folder);
                return folder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (NameExists(ex) || Taken(folder)) i++;
                else if (++tries < 20) Thread.Sleep(100);
                else throw;
            }
        }
    }

    void Release()
    {
        _hold?.Dispose();
        _hold = null;
    }

    public void Dispose()
    {
        Release();
        if (Remove(_staging)) _journal.Done();
        else _journal.Leave(); // still in use (a converter that never let go): cleaned up next time
    }

    static bool Remove(string staging)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (!Directory.Exists(staging)) return true;
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(staging, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ after a crash

    static string JournalDir => Path.Combine(Log.DataDir, "working");

    /// <summary>Removes working folders left by a copy of the app that crashed or was killed.</summary>
    public static void CleanLeftovers()
    {
        try
        {
            if (!Directory.Exists(JournalDir)) return;
            foreach (var entry in Directory.GetFiles(JournalDir, "*.txt"))
            {
                try
                {
                    string staging;
                    // Fails while the conversion is still running, in this copy or another.
                    using (var file = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None))
                    using (var reader = new StreamReader(file)) staging = reader.ReadToEnd();
                    if (StagingName().IsMatch(Path.GetFileName(staging)) && !Remove(staging)) continue;
                    File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    [GeneratedRegex(@"^\.cw-[0-9a-f]{8}$")]
    private static partial Regex StagingName();

    /// <summary>A note of the working folder, kept open while it's in use.</summary>
    sealed class Journal
    {
        readonly string? _path;
        FileStream? _file;

        public Journal(string staging)
        {
            try
            {
                Directory.CreateDirectory(JournalDir);
                _path = Path.Combine(JournalDir, Guid.NewGuid().ToString("N") + ".txt");
                _file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _file.Write(System.Text.Encoding.UTF8.GetBytes(staging));
                _file.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"No note of {staging}: {ex.Message}");
            }
        }

        public void Leave()
        {
            _file?.Dispose();
            _file = null;
        }

        public void Done()
        {
            Leave();
            for (int i = 0; _path != null && i < 8; i++)
            {
                try
                {
                    File.Delete(_path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(25); } // being scanned; a note left over does no harm
            }
        }
    }
}
