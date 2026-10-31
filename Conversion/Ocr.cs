using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConvertWheel.Conversion;

/// <summary>
/// Text recognition with the OCR engine built into Windows 10/11 (Windows.Media.Ocr), called directly
/// through its COM interfaces so no WinRT projection library has to ship with the app.
/// </summary>
internal static unsafe class Ocr
{
    public sealed record Word(string Text, Rect Bounds);
    public sealed record Line(string Text, List<Word> Words)
    {
        public Rect Bounds => Words.Count == 0 ? Rect.Empty : Words.Select(w => w.Bounds).Aggregate(Rect.Union);
    }

    static readonly Guid IidEngineStatics = new("5bffa85a-3384-3540-9940-699120d428a8");
    static readonly Guid IidBitmapStatics = new("df0385db-672f-4a9d-806e-c2442f343e86");
    static readonly Guid IidBufferStatics = new("320b7e22-3cb0-4cdf-8663-1d28910065eb");
    static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    static readonly object Gate = new();
    static int _maxDimension;
    static string? _unavailable;

    /// <summary>Whether Windows has a recognizer installed for at least one language.</summary>
    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                var engine = Engine();
                if (engine == 0) return false;
                Marshal.Release(engine);
                return true;
            }
        }
    }

    /// <summary>Recognises the text in a picture. Pixel coordinates in the result match the picture.</summary>
    public static List<Line> Recognize(BitmapSource image)
    {
        lock (Gate)
        {
            var engine = Engine();
            if (engine == IntPtr.Zero) throw new ConversionException(_unavailable ?? "Windows text recognition isn't available on this PC.");

            // The engine has a size limit, and reads small print better when it is enlarged a little.
            double scale = 1;
            int longest = Math.Max(image.PixelWidth, image.PixelHeight);
            if (longest > _maxDimension) scale = (double)_maxDimension / longest;
            else if (longest < 1400) scale = Math.Min(3, 1400.0 / longest);
            var source = scale == 1 ? image : new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            // A fresh engine per call: conversions run on short-lived threads, and COM objects belong to their apartment.
            IntPtr buffer = 0, bitmap = 0, operation = 0, result = 0, lines = 0;
            try
            {
                var bufferStatics = Factory("Windows.Security.Cryptography.CryptographicBuffer", IidBufferStatics);
                try
                {
                    fixed (byte* p = pixels)
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)Slot(bufferStatics, 9))(bufferStatics, (uint)pixels.Length, p, &buffer));
                }
                finally { Marshal.Release(bufferStatics); }

                var bitmapStatics = Factory("Windows.Graphics.Imaging.SoftwareBitmap", IidBitmapStatics);
                try
                {
                    const int Bgra8 = 87;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, int, IntPtr*, int>)Slot(bitmapStatics, 9))(bitmapStatics, buffer, Bgra8, w, h, &bitmap));
                }
                finally { Marshal.Release(bitmapStatics); }

                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(engine, 6))(engine, bitmap, &operation));
                Wait(operation);
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(operation, 8))(operation, &result));
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(result, 6))(result, &lines));

                var output = new List<Line>();
                foreach (var line in Vector(lines))
                {
                    try
                    {
                        string text = GetString(line, 7);
                        IntPtr words = 0;
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(line, 6))(line, &words));
                        var list = new List<Word>();
                        try
                        {
                            foreach (var word in Vector(words))
                            {
                                try
                                {
                                    WinRect r;
                                    Check(((delegate* unmanaged[Stdcall]<IntPtr, WinRect*, int>)Slot(word, 6))(word, &r));
                                    list.Add(new Word(GetString(word, 7), new Rect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale)));
                                }
                                finally { Marshal.Release(word); }
                            }
                        }
                        finally { Marshal.Release(words); }
                        if (text.Trim().Length > 0) output.Add(new Line(text, list));
                    }
                    finally { Marshal.Release(line); }
                }
                return output;
            }
            finally
            {
                foreach (var p in new[] { lines, result, operation, bitmap, buffer, engine })
                    if (p != 0) Marshal.Release(p);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinRect { public float X, Y, Width, Height; }

    /// <summary>A new recognizer for the user's language (or any installed one); 0 when there is none.</summary>
    static IntPtr Engine()
    {
        if (_unavailable != null) return 0;
        IntPtr result = 0;
        try
        {
            var statics = Factory("Windows.Media.Ocr.OcrEngine", IidEngineStatics);
            try
            {
                int max;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(statics, 6))(statics, &max));
                _maxDimension = max > 0 ? max : 2600;
                IntPtr engine;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(statics, 10))(statics, &engine));
                if (engine == 0)
                {
                    // None of the user's languages has a recognizer: use any installed one.
                    IntPtr languages;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(statics, 7))(statics, &languages));
                    try
                    {
                        var all = Vector(languages);
                        if (all.Count > 0) Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 9))(statics, all[0], &engine));
                        foreach (var language in all) Marshal.Release(language);
                    }
                    finally { Marshal.Release(languages); }
                }
                if (engine == 0) _unavailable = "Windows has no text-recognition language installed. Add one in Settings › Time & language › Language.";
                result = engine;
            }
            finally { Marshal.Release(statics); }
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException or TypeLoadException)
        {
            Log.Write($"Windows OCR unavailable: {ex.Message}");
            _unavailable = "Windows text recognition isn't available on this PC.";
        }
        return result;
    }

    /// <summary>Waits for a WinRT async operation by polling its status (no callback needed).</summary>
    static void Wait(IntPtr operation)
    {
        Check(Marshal.QueryInterface(operation, in IidAsyncInfo, out var info));
        try
        {
            for (int waited = 0; ; waited += 5)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status));
                if (status == 1) return;
                if (status is 2 or 3)
                {
                    int error;
                    ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 8))(info, &error);
                    throw new ConversionException($"Text recognition failed (0x{error:X8}).");
                }
                if (waited > 120_000) throw new ConversionException("Text recognition took too long.");
                Thread.Sleep(5);
            }
        }
        finally { Marshal.Release(info); }
    }

    /// <summary>The items of an IVectorView (each returned pointer is owned by the caller).</summary>
    static List<IntPtr> Vector(IntPtr view)
    {
        uint size;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(view, 7))(view, &size));
        var items = new List<IntPtr>((int)size);
        for (uint i = 0; i < size; i++)
        {
            IntPtr item;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(view, 6))(view, i, &item));
            items.Add(item);
        }
        return items;
    }

    static string GetString(IntPtr obj, int slot)
    {
        IntPtr hstring;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &hstring));
        try
        {
            uint length;
            var chars = WindowsGetStringRawBuffer(hstring, &length);
            return new string(chars, 0, (int)length);
        }
        finally { WindowsDeleteString(hstring); }
    }

    static IntPtr Factory(string className, Guid iid)
    {
        IntPtr name;
        fixed (char* p = className) Check(WindowsCreateString(p, (uint)className.Length, &name));
        try
        {
            IntPtr factory;
            Check(RoGetActivationFactory(name, &iid, &factory));
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, Guid* iid, IntPtr* factory);
    [DllImport("combase.dll")] static extern int WindowsCreateString(char* source, uint length, IntPtr* hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);
}
