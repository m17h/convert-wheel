using System.Runtime.InteropServices;
using System.Text;

namespace ConvertWheel.Conversion.Media;

// Minimal interop for Windows Media Foundation, the codec engine built into Windows.
// [ComImport] interfaces must list every inherited method in vtable order.

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem(ref Guid key, IntPtr value);
    void GetItemType(ref Guid key, out int type);
    void CompareItem(ref Guid key, IntPtr value, out bool result);
    void Compare(IMFAttributes theirs, int matchType, out bool result);
    [PreserveSig] int GetUINT32(ref Guid key, out int value);
    [PreserveSig] int GetUINT64(ref Guid key, out long value);
    void GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    void GetStringLength(ref Guid key, out int length);
    void GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size, out int length);
    void GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(ref Guid key, out int size);
    void GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size, out int blobSize);
    void GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    void GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
    void SetItem(ref Guid key, IntPtr value);
    void DeleteItem(ref Guid key);
    void DeleteAllItems();
    void SetUINT32(ref Guid key, int value);
    void SetUINT64(ref Guid key, long value);
    void SetDouble(ref Guid key, double value);
    void SetGUID(ref Guid key, ref Guid value);
    void SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(ref Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size);
    void SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    void LockStore();
    void UnlockStore();
    void GetCount(out int count);
    void GetItemByIndex(int index, out Guid key, IntPtr value);
    void CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    new void GetItem(ref Guid key, IntPtr value);
    new void GetItemType(ref Guid key, out int type);
    new void CompareItem(ref Guid key, IntPtr value, out bool result);
    new void Compare(IMFAttributes theirs, int matchType, out bool result);
    [PreserveSig] new int GetUINT32(ref Guid key, out int value);
    [PreserveSig] new int GetUINT64(ref Guid key, out long value);
    new void GetDouble(ref Guid key, out double value);
    [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
    new void GetStringLength(ref Guid key, out int length);
    new void GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size, out int length);
    new void GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
    new void GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size, out int blobSize);
    new void GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    new void GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
    new void SetItem(ref Guid key, IntPtr value);
    new void DeleteItem(ref Guid key);
    new void DeleteAllItems();
    new void SetUINT32(ref Guid key, int value);
    new void SetUINT64(ref Guid key, long value);
    new void SetDouble(ref Guid key, double value);
    new void SetGUID(ref Guid key, ref Guid value);
    new void SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    new void SetBlob(ref Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size);
    new void SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out int count);
    new void GetItemByIndex(int index, out Guid key, IntPtr value);
    new void CopyAllItems(IMFAttributes destination);
    void GetMajorType(out Guid type);
    void IsCompressedFormat(out bool compressed);
    [PreserveSig] int IsEqual(IMFMediaType type, out int flags);
    void GetRepresentation(Guid representation, out IntPtr value);
    void FreeRepresentation(Guid representation, IntPtr value);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    new void GetItem(ref Guid key, IntPtr value);
    new void GetItemType(ref Guid key, out int type);
    new void CompareItem(ref Guid key, IntPtr value, out bool result);
    new void Compare(IMFAttributes theirs, int matchType, out bool result);
    [PreserveSig] new int GetUINT32(ref Guid key, out int value);
    [PreserveSig] new int GetUINT64(ref Guid key, out long value);
    new void GetDouble(ref Guid key, out double value);
    [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
    new void GetStringLength(ref Guid key, out int length);
    new void GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size, out int length);
    new void GetAllocatedString(ref Guid key, out IntPtr value, out int length);
    [PreserveSig] new int GetBlobSize(ref Guid key, out int size);
    new void GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size, out int blobSize);
    new void GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
    new void GetUnknown(ref Guid key, ref Guid riid, out IntPtr value);
    new void SetItem(ref Guid key, IntPtr value);
    new void DeleteItem(ref Guid key);
    new void DeleteAllItems();
    new void SetUINT32(ref Guid key, int value);
    new void SetUINT64(ref Guid key, long value);
    new void SetDouble(ref Guid key, double value);
    new void SetGUID(ref Guid key, ref Guid value);
    new void SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    new void SetBlob(ref Guid key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int size);
    new void SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out int count);
    new void GetItemByIndex(int index, out Guid key, IntPtr value);
    new void CopyAllItems(IMFAttributes destination);
    void GetSampleFlags(out int flags);
    void SetSampleFlags(int flags);
    [PreserveSig] int GetSampleTime(out long time);
    void SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    void SetSampleDuration(long duration);
    void GetBufferCount(out int count);
    void GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex(int index);
    void RemoveAllBuffers();
    void GetTotalLength(out int length);
    void CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    void Unlock();
    void GetCurrentLength(out int length);
    void SetCurrentLength(int length);
    void GetMaxLength(out int length);
}

[ComImport, Guid("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    void Lock2D(out IntPtr scanline0, out int pitch);
    void Unlock2D();
    void GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
    void IsContiguousFormat(out bool contiguous);
    void GetContiguousLength(out int length);
    void ContiguousCopyTo(IntPtr destination, int length);
    void ContiguousCopyFrom(IntPtr source, int length);
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    void GetStreamSelection(int streamIndex, out bool selected);
    void SetStreamSelection(int streamIndex, bool selected);
    [PreserveSig] int GetNativeMediaType(int streamIndex, int typeIndex, out IMFMediaType type);
    [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFMediaType type);
    [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType type);
    void SetCurrentPosition(ref Guid timeFormat, IntPtr position);
    void ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample? sample);
    void Flush(int streamIndex);
    void GetServiceForStream(int streamIndex, ref Guid service, ref Guid riid, out IntPtr obj);
    [PreserveSig] int GetPresentationAttribute(int streamIndex, ref Guid key, IntPtr value);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    void AddStream(IMFMediaType targetType, out int streamIndex);
    void SetInputMediaType(int streamIndex, IMFMediaType inputType, IMFAttributes? encodingParameters);
    void BeginWriting();
    void WriteSample(int streamIndex, IMFSample sample);
    void SendStreamTick(int streamIndex, long timestamp);
    void PlaceMarker(int streamIndex, IntPtr context);
    void NotifyEndOfSegment(int streamIndex);
    void Flush(int streamIndex);
    void DoFinalize();
    void GetServiceForStream(int streamIndex, ref Guid service, ref Guid riid, out IntPtr obj);
    void GetStatistics(int streamIndex, IntPtr stats);
}

[ComImport, Guid("5BC8A76B-869A-46a3-9B03-FA218A66AEBE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCollection
{
    void GetElementCount(out int count);
    void GetElement(int index, [MarshalAs(UnmanagedType.IUnknown)] out object element);
    void AddElement([MarshalAs(UnmanagedType.IUnknown)] object element);
    void RemoveElement(int index, [MarshalAs(UnmanagedType.IUnknown)] out object element);
    void InsertElementAt(int index, [MarshalAs(UnmanagedType.IUnknown)] object element);
    void RemoveAllElements();
}

internal static class MF
{
    public const int SourceReaderFirstVideo = unchecked((int)0xFFFFFFFC);
    public const int SourceReaderFirstAudio = unchecked((int)0xFFFFFFFD);
    public const int SourceReaderAllStreams = unchecked((int)0xFFFFFFFE);
    public const int SourceReaderAnyStream = unchecked((int)0xFFFFFFFE);
    public const int SourceReaderMediaSource = unchecked((int)0xFFFFFFFF);
    public const int ReaderError = 0x1, ReaderEndOfStream = 0x2, ReaderStreamTick = 0x100;
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_INVALIDSTREAMNUMBER = unchecked((int)0xC00D36B3);

    // Media types
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioPCM = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFloat = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioMP3 = new("00000055-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioAAC = new("00001610-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioWMAV8 = new("00000161-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioWMAV9 = new("00000162-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFLAC = new("0000F1AC-0000-0010-8000-00aa00389b71");
    public static readonly Guid VideoH264 = FourCC("H264");
    public static readonly Guid VideoHEVC = FourCC("HEVC");
    public static readonly Guid VideoWMV3 = FourCC("WMV3");
    public static readonly Guid VideoNV12 = FourCC("NV12");
    public static readonly Guid VideoRGB32 = new("00000016-0000-0010-8000-00AA00389B71");

    // Attributes
    public static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    public static readonly Guid MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static readonly Guid SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static readonly Guid TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");

    public static readonly Guid ContainerMPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    public static readonly Guid ContainerMP3 = new("e438b912-83f1-4de6-9e3a-9ffbc6dd24d1");
    public static readonly Guid ContainerASF = new("430f6f6e-b6bf-4fc1-a0bd-9ee46eee2afb");
    public static readonly Guid ContainerADTS = new("132fd27d-0f02-43de-a301-38fbbbb3834e");
    public static readonly Guid ContainerFLAC = new("31344aa3-05a9-42b5-901b-8e9d4257f75e");
    public static readonly Guid ContainerAVI = new("7edfe8af-402f-4d76-a33c-619fd157d0f1");

    static Guid FourCC(string code) =>
        new(code[0] | code[1] << 8 | code[2] << 16 | code[3] << 24, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateAttributes(out IMFAttributes attributes, int initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes? attributes, out IMFSourceReader reader);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes? attributes, out IMFSinkWriter writer);
    [DllImport("mf.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void MFTranscodeGetAudioOutputAvailableTypes(ref Guid subtype, int flags, IMFAttributes? codecConfig, out IMFCollection types);

    static readonly Lazy<bool> Started = new(() => MFStartup(0x20070, 0) >= 0);

    /// <summary>Media Foundation is process-wide; it's started once and left running.</summary>
    public static void EnsureStarted()
    {
        if (!Started.Value) throw new NotSupportedException("Windows Media Foundation isn't available on this PC.");
    }

    public static IMFMediaType NewType(Guid major, Guid subtype)
    {
        MFCreateMediaType(out var type);
        type.SetGUID(MT_MAJOR_TYPE, major);
        type.SetGUID(MT_SUBTYPE, subtype);
        return type;
    }

    public static void SetGUID(this IMFAttributes a, Guid key, Guid value) => a.SetGUID(ref key, ref value);
    public static void SetInt(this IMFAttributes a, Guid key, int value) => a.SetUINT32(ref key, value);
    public static void SetLong(this IMFAttributes a, Guid key, long value) => a.SetUINT64(ref key, value);
    public static void SetSize(this IMFAttributes a, Guid key, int high, int low) => a.SetUINT64(ref key, (long)high << 32 | (uint)low);
    public static void SetBlob(this IMFAttributes a, Guid key, byte[] data) => a.SetBlob(ref key, data, data.Length);

    public static Guid? GetGuid(this IMFAttributes a, Guid key) => a.GetGUID(ref key, out var v) >= 0 ? v : null;
    public static int? GetInt(this IMFAttributes a, Guid key) => a.GetUINT32(ref key, out var v) >= 0 ? v : null;
    public static long? GetLong(this IMFAttributes a, Guid key) => a.GetUINT64(ref key, out var v) >= 0 ? v : null;
    public static (int high, int low)? GetSize(this IMFAttributes a, Guid key) =>
        a.GetUINT64(ref key, out var v) >= 0 ? ((int)(v >> 32), (int)(v & 0xFFFFFFFF)) : null;

    public static byte[]? GetBlob(this IMFAttributes a, Guid key)
    {
        if (a.GetBlobSize(ref key, out var size) < 0) return null;
        var data = new byte[size];
        a.GetBlob(ref key, data, size, out _);
        return data;
    }

    public static void CopyIfPresent(this IMFAttributes from, IMFAttributes to, Guid key)
    {
        if (from.GetUINT32(ref key, out var u) >= 0) to.SetUINT32(ref key, u);
        else if (from.GetUINT64(ref key, out var l) >= 0) to.SetUINT64(ref key, l);
        else if (from.GetGUID(ref key, out var g) >= 0) to.SetGUID(ref key, ref g);
        else if (from.GetBlob(key) is { } b) to.SetBlob(key, b);
    }

    /// <summary>Duration of the opened media, in 100-ns units.</summary>
    public static long? Duration(IMFSourceReader reader)
    {
        var key = PD_DURATION;
        var variant = Marshal.AllocCoTaskMem(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            if (reader.GetPresentationAttribute(SourceReaderMediaSource, ref key, variant) < 0) return null;
            return Marshal.ReadInt16(variant) == 21 /* VT_UI8 */ ? Marshal.ReadInt64(variant, 8) : null;
        }
        finally { Marshal.FreeCoTaskMem(variant); }
    }

    public static IMFSample SampleFrom(ReadOnlySpan<byte> data, long time, long duration)
    {
        MFCreateMemoryBuffer(Math.Max(1, data.Length), out var buffer);
        buffer.Lock(out var ptr, out _, out _);
        unsafe { data.CopyTo(new Span<byte>((void*)ptr, data.Length)); }
        buffer.Unlock();
        buffer.SetCurrentLength(data.Length);
        MFCreateSample(out var sample);
        sample.AddBuffer(buffer);
        sample.SetSampleTime(time);
        sample.SetSampleDuration(duration);
        Marshal.ReleaseComObject(buffer);
        return sample;
    }

    public static byte[] Bytes(IMFSample sample)
    {
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            buffer.Lock(out var ptr, out _, out var length);
            try
            {
                var data = new byte[length];
                Marshal.Copy(ptr, data, 0, length);
                return data;
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    public static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}
