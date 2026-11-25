using System.Runtime.InteropServices;

namespace ConvertWheel.Conversion.Media;

/// <summary>
/// Video conversion on Windows' own codecs (H.264, WMV, AAC, WMA; often hardware accelerated), plus
/// our GIF encoder and MKV muxer. Streams that already fit the target container are copied untouched.
/// </summary>
internal static class VideoEngine
{
    // AVI is left to FFmpeg: the devices that still want AVI need Xvid, which Windows can't encode.
    static readonly HashSet<string> VideoTargets = ["mp4", "mov", "wmv", "mkv"];

    public static bool CanConvert(ConversionPlan plan, FormatInfo target)
    {
        if (plan.SourceKind == FormatKind.Video) return VideoTargets.Contains(target.Ext) || target.Ext is "gif" or "webp";
        return Animation.IsAnimated(plan.SourcePath) && VideoTargets.Contains(target.Ext);
    }

    public static void Convert(ConversionPlan plan, FormatInfo target, string output, IProgress<double?> progress, CancellationToken ct)
    {
        MF.EnsureStarted();
        if (plan.SourceKind == FormatKind.Image)
        {
            GifToVideo(plan.SourcePath, output, target.Ext, progress, ct);
            return;
        }
        MediaCheck.EnsureComplete(plan.SourcePath);
        // Windows guesses the frame rate of Matroska/WebM files, and drops frames when it guesses low.
        if (MediaCheck.IsMatroska(plan.SourcePath)) throw new NotSupportedException("Matroska and WebM videos are left to FFmpeg.");
        if (target.Ext == "webp")
        {
            VideoToWebP(plan.SourcePath, output, progress, ct);
            return;
        }
        if (target.Ext == "gif")
        {
            VideoToGif(plan.SourcePath, output, progress, ct);
            return;
        }
        if (target.Ext == "mkv")
        {
            MatroskaRemuxer.Convert(plan.SourcePath, output, progress, ct);
            return;
        }
        if (target.Ext is "mp4" or "mov" && TryRemux(plan.SourcePath, output, progress, ct))
        {
            if (target.Ext == "mov") MarkAsQuickTime(output);
            return;
        }
        Transcode(plan.SourcePath, output, target.Ext, progress, ct);
        if (target.Ext == "mov") MarkAsQuickTime(output);
    }

    // ------------------------------------------------------------------ probing

    /// <param name="AutoRotated">True when Windows' video processor will apply <paramref name="Rotation"/> while decoding.</param>
    internal sealed record VideoInfo(Guid Subtype, int Width, int Height, int RateNum, int RateDen, int ParNum, int ParDen, int? Rotation, long? Duration, bool AutoRotated)
    {
        public double Fps => RateDen > 0 ? (double)RateNum / RateDen : 30;
    }

    internal static IMFSourceReader OpenVideoReader(string path, out VideoInfo info, bool processing = true, bool probeRotation = true)
    {
        MF.MFCreateAttributes(out var attrs, 1);
        if (processing) attrs.SetInt(MF.SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
        MF.MFCreateSourceReaderFromURL(path, attrs, out var reader);
        MF.Release(attrs);
        reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
        try { reader.SetStreamSelection(MF.SourceReaderFirstVideo, true); }
        catch (COMException) { MF.Release(reader); throw new NotSupportedException("This file has no video track."); }
        if (reader.GetNativeMediaType(MF.SourceReaderFirstVideo, 0, out var native) < 0)
        {
            MF.Release(reader);
            throw new NotSupportedException("This file has no video track.");
        }
        var (w, h) = native.GetSize(MF.MT_FRAME_SIZE) ?? (0, 0);
        var (rn, rd) = native.GetSize(MF.MT_FRAME_RATE) ?? (30, 1);
        var (pn, pd) = native.GetSize(MF.MT_PIXEL_ASPECT_RATIO) ?? (1, 1);
        info = new VideoInfo(native.GetGuid(MF.MT_SUBTYPE) ?? Guid.Empty, w, h, rn, Math.Max(1, rd), Math.Max(1, pn), Math.Max(1, pd),
            native.GetInt(MF.MT_VIDEO_ROTATION), MF.Duration(reader), false);
        if (probeRotation && FrameRotation.Normalize(info.Rotation) != 0) info = info with { AutoRotated = DecoderPassesRotation(path, info.Rotation!.Value) };
        MF.Release(native);
        if (w <= 0 || h <= 0) { MF.Release(reader); throw new NotSupportedException("Unknown video size."); }
        return reader;
    }

    /// <summary>
    /// Frame size to request from the decoder: square pixels, upright if Windows applies the rotation
    /// itself, otherwise still in stored orientation (see <see cref="PendingRotation"/>).
    /// </summary>
    static (int width, int height) DecodedSize(VideoInfo info)
    {
        int width = (int)Math.Round(info.Width * (double)info.ParNum / info.ParDen);
        return info.AutoRotated && FrameRotation.Normalize(info.Rotation) is 90 or 270 ? (info.Height, width) : (width, info.Height);
    }

    /// <summary>Rotation still to be dealt with after decoding (0 when Windows already applied it).</summary>
    static int PendingRotation(VideoInfo info) => info.AutoRotated ? 0 : FrameRotation.Normalize(info.Rotation);

    /// <summary>
    /// Windows rotates decoded frames only when the decoder passes the rotation on (H.264 does,
    /// MPEG-4 Part 2 doesn't), so ask a plain decoder what it reports.
    /// </summary>
    static bool DecoderPassesRotation(string path, int rotation)
    {
        IMFSourceReader? reader = null;
        try
        {
            reader = OpenVideoReader(path, out _, processing: false, probeRotation: false);
            foreach (var format in new[] { MF.VideoNV12, MF.VideoRGB32 })
            {
                var want = MF.NewType(MF.MediaTypeVideo, format);
                int hr = reader.SetCurrentMediaType(MF.SourceReaderFirstVideo, IntPtr.Zero, want);
                MF.Release(want);
                if (hr < 0) continue;
                reader.GetCurrentMediaType(MF.SourceReaderFirstVideo, out var current);
                bool passes = current.GetInt(MF.MT_VIDEO_ROTATION) == rotation;
                MF.Release(current);
                return passes;
            }
            return false;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException) { return false; }
        finally { MF.Release(reader); }
    }

    static bool HasAudio(string path)
    {
        try
        {
            MF.MFCreateSourceReaderFromURL(path, null, out var reader);
            try
            {
                if (reader.GetNativeMediaType(MF.SourceReaderFirstAudio, 0, out var t) < 0) return false;
                MF.Release(t);
                return true;
            }
            finally { MF.Release(reader); }
        }
        catch (COMException) { return false; }
    }

    // ------------------------------------------------------------------ transcoding

    static IMFSinkWriter CreateWriter(string path, Guid container)
    {
        MF.MFCreateAttributes(out var attrs, 3);
        try
        {
            attrs.SetGUID(MF.TRANSCODE_CONTAINERTYPE, container);
            attrs.SetInt(MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            attrs.SetInt(MF.SINK_WRITER_DISABLE_THROTTLING, 1);
            MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out var writer);
            return writer;
        }
        finally { MF.Release(attrs); }
    }

    /// <summary>A sensible bitrate for visually clean H.264/WMV at this size and frame rate.</summary>
    static int Bitrate(int width, int height, double fps, double factor = 0.12) =>
        (int)Math.Clamp(width * (double)height * Math.Min(fps, 60) * factor, 1_000_000, 40_000_000);

    /// <param name="rotation">A playback-rotation flag to store (MP4/MOV), or null.</param>
    static (int videoStream, Guid inputFormat) AddVideoStream(IMFSinkWriter writer, string ext, int width, int height, int rateNum, int rateDen, int? rotation = null)
    {
        var codec = ext == "wmv" ? MF.VideoWMV3 : MF.VideoH264;
        var output = MF.NewType(MF.MediaTypeVideo, codec);
        output.SetSize(MF.MT_FRAME_SIZE, width, height);
        output.SetSize(MF.MT_FRAME_RATE, rateNum, rateDen);
        output.SetSize(MF.MT_PIXEL_ASPECT_RATIO, 1, 1);
        output.SetInt(MF.MT_INTERLACE_MODE, 2); // progressive
        output.SetInt(MF.MT_AVG_BITRATE, Bitrate(width, height, (double)rateNum / rateDen));
        if (codec == MF.VideoH264) output.SetInt(MF.MT_MPEG2_PROFILE, 100); // High profile
        if (rotation is int r && r != 0) output.SetInt(MF.MT_VIDEO_ROTATION, r);

        writer.AddStream(output, out int stream);
        MF.Release(output);

        var input = MF.NewType(MF.MediaTypeVideo, MF.VideoNV12);
        input.SetSize(MF.MT_FRAME_SIZE, width, height);
        input.SetSize(MF.MT_FRAME_RATE, rateNum, rateDen);
        input.SetSize(MF.MT_PIXEL_ASPECT_RATIO, 1, 1);
        input.SetInt(MF.MT_INTERLACE_MODE, 2);
        writer.SetInputMediaType(stream, input, null);
        MF.Release(input);
        return (stream, MF.VideoNV12);
    }

    static (int stream, IAudioSource source, MfAudioSource original)? AddAudioStream(IMFSinkWriter writer, string path, string ext)
    {
        if (!HasAudio(path))
        {
            if (MediaCheck.HasAudio(path) == true) throw new NotSupportedException("Windows can't read this video's audio track.");
            return null;
        }
        IAudioSource? source = null;
        try
        {
            string codecExt = ext switch { "wmv" => "wma", "avi" => "mp3", _ => "m4a" };
            var original = new MfAudioSource(path, keepTiming: true);
            source = original;
            source = AudioEngine.Prepare(original, codecExt, out _);
            var output = codecExt switch
            {
                "wma" => AudioEngine.WmaType(source.Format),
                "mp3" => Mp3Type(source.Format),
                _ => AudioEngine.AacType(source.Format),
            };
            int stream;
            try { writer.AddStream(output, out stream); }
            finally { MF.Release(output); }
            var input = MfAudioSink.PcmType(source.Format, 16);
            try { writer.SetInputMediaType(stream, input, null); }
            finally { MF.Release(input); }
            return (stream, source, original);
        }
        catch (Exception ex) when (ex is NotSupportedException or COMException)
        {
            source?.Dispose();
            throw new NotSupportedException("Windows couldn't convert the audio track.", ex);
        }
    }

    static IMFMediaType Mp3Type(AudioFormat f)
    {
        var t = MF.NewType(MF.MediaTypeAudio, MF.AudioMP3);
        t.SetInt(MF.MT_AUDIO_NUM_CHANNELS, f.Channels);
        t.SetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND, f.SampleRate);
        t.SetInt(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
        t.SetInt(MF.MT_AUDIO_BLOCK_ALIGNMENT, 1);
        return t;
    }

    static Guid Container(string ext) => ext switch
    {
        "wmv" => MF.ContainerASF,
        "avi" => MF.ContainerAVI,
        _ => MF.ContainerMPEG4,
    };

    /// <summary>Windows' reader only gives us the first audio track of several; FFmpeg can keep them all.</summary>
    static void EnsureOneAudioTrack(IMFSourceReader reader, Mp4Info? timing)
    {
        int tracks = 0;
        for (int i = 0; reader.GetNativeMediaType(i, 0, out var t) >= 0; i++)
        {
            if (t.GetGuid(MF.MT_MAJOR_TYPE) == MF.MediaTypeAudio) tracks++;
            MF.Release(t);
        }
        if (Math.Max(tracks, timing?.Audio.Count ?? 0) > 1) throw new NotSupportedException("This video has several audio tracks, which needs FFmpeg.");
    }

    internal static void TranscodeTo(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        MF.EnsureStarted();
        Transcode(input, output, ext, progress, ct);
    }

    static void Transcode(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        var timing = Mp4Info.Read(input);
        if (timing?.Trimmed == true) throw new NotSupportedException("This video has an edit list, which needs FFmpeg.");
        var reader = OpenVideoReader(input, out var info);
        IMFSinkWriter? writer = null;
        (int stream, IAudioSource source, MfAudioSource original)? audio = null;
        try
        {
            EnsureOneAudioTrack(reader, timing);
            // The reader's video processor scales to square pixels (and applies phone rotation when the
            // decoder allows). Encoders need even dimensions.
            var (width, height) = DecodedSize(info);
            width &= ~1;
            height &= ~1;
            // Rotation Windows didn't apply: MP4/MOV store it as a playback flag; WMV can't, so turn the pixels.
            int pending = PendingRotation(info);
            int turnPixels = ext == "wmv" ? pending : 0;
            var (outWidth, outHeight) = turnPixels is 90 or 270 ? (height, width) : (width, height);
            var want = MF.NewType(MF.MediaTypeVideo, MF.VideoNV12);
            want.SetSize(MF.MT_FRAME_SIZE, width, height);
            want.SetSize(MF.MT_PIXEL_ASPECT_RATIO, 1, 1);
            want.SetInt(MF.MT_INTERLACE_MODE, 2);
            int hr = reader.SetCurrentMediaType(MF.SourceReaderFirstVideo, IntPtr.Zero, want);
            MF.Release(want);
            if (hr < 0) throw new NotSupportedException($"Windows can't decode this video (0x{hr:X8}).");

            writer = CreateWriter(output, Container(ext));
            var (videoStream, _) = AddVideoStream(writer, ext, outWidth, outHeight, info.RateNum, info.RateDen,
                turnPixels == 0 && pending != 0 ? pending : null);
            audio = AddAudioStream(writer, input, ext);
            writer.BeginWriting();

            long videoTime = 0, audioTime = 0, audioFrames = 0;
            bool videoDone = false, audioDone = audio == null;

            // Start at 0 whichever track comes first, and keep the other exactly as much later as it was.
            IMFSample? first = null;
            long firstTime = 0;
            while (first == null && !videoDone)
            {
                ct.ThrowIfCancellationRequested();
                reader.ReadSample(MF.SourceReaderFirstVideo, 0, out _, out int flags, out firstTime, out first);
                if ((flags & MF.ReaderError) != 0) throw new InvalidOperationException("Windows couldn't decode this video.");
                if ((flags & MF.ReaderEndOfStream) != 0) videoDone = first == null;
            }
            if (first == null) throw new InvalidOperationException("Windows found no pictures in this video.");
            double videoStart = timing?.FirstVideo?.Start ?? firstTime / 1e7, start = videoStart;
            if (audio != null)
            {
                double heard = audio.Value.original.FirstTime / 1e7, audioStart = timing?.Audio.FirstOrDefault()?.Start ?? heard;
                start = Math.Max(0, Math.Min(start, audioStart));
                audio.Value.original.Shift = audioStart - start - heard;
            }
            long videoShift = (long)Math.Round((videoStart - start) * 1e7) - firstTime;
            long videoEnd = 0;
            var buffer = audio != null ? new float[4096 * audio.Value.source.Format.Channels] : [];
            var pcm = new byte[buffer.Length * 2];
            long duration = info.Duration ?? 0;

            while (!videoDone || !audioDone)
            {
                ct.ThrowIfCancellationRequested();
                if (!videoDone && (audioDone || videoTime <= audioTime))
                {
                    int flags = 0;
                    long ts = firstTime;
                    var sample = first;
                    first = null;
                    if (sample == null) reader.ReadSample(MF.SourceReaderFirstVideo, 0, out _, out flags, out ts, out sample);
                    if ((flags & MF.ReaderError) != 0) { MF.Release(sample); throw new InvalidOperationException("Windows couldn't decode this video."); }
                    ts = Math.Max(0, ts + videoShift);
                    if ((flags & MF.ReaderStreamTick) != 0) writer.SendStreamTick(videoStream, ts);
                    if (sample != null)
                    {
                        sample.SetSampleTime(ts);
                        videoEnd = Math.Max(videoEnd, ts + (sample.GetSampleDuration(out long frameLength) >= 0 ? frameLength : 0));
                        if (turnPixels != 0)
                        {
                            var frame = MF.Bytes(sample);
                            if (frame.Length < width * height * 3 / 2) throw new InvalidOperationException("Unexpected video frame layout.");
                            sample.GetSampleTime(out long time);
                            sample.GetSampleDuration(out long length);
                            var turned = MF.SampleFrom(FrameRotation.Nv12(frame, width, height, turnPixels), time, length);
                            writer.WriteSample(videoStream, turned);
                            MF.Release(turned);
                        }
                        else writer.WriteSample(videoStream, sample);
                        MF.Release(sample);
                        videoTime = ts;
                        if (duration > 0) progress.Report(Math.Min(0.99, (double)ts / duration));
                    }
                    if ((flags & MF.ReaderEndOfStream) != 0) videoDone = true;
                }
                else
                {
                    var (stream, source, _) = audio!.Value;
                    int n = source.Read(buffer, buffer.Length);
                    if (n == 0) { audioDone = true; continue; }
                    Pcm.ToInt(buffer.AsSpan(0, n), pcm, 16);
                    long frames = n / source.Format.Channels;
                    long duration100ns = (audioFrames + frames) * 10_000_000 / source.Format.SampleRate - audioTime;
                    var sample = MF.SampleFrom(pcm.AsSpan(0, n * 2), audioTime, duration100ns);
                    writer.WriteSample(stream, sample);
                    MF.Release(sample);
                    audioFrames += frames;
                    audioTime = audioFrames * 10_000_000 / source.Format.SampleRate;
                }
            }
            long end = Math.Max(videoEnd, audioTime);
            if (duration > 0 && audioTime > duration * 1.1 + 20_000_000) throw new InvalidOperationException("Windows decoded this video's audio at the wrong speed.");
            if (duration > 0 && duration - end > Math.Max(10_000_000, duration / 50))
                throw new InvalidOperationException($"Only {MediaCheck.Clock(end)} of {MediaCheck.Clock(duration)} could be read.");
            writer.DoFinalize();
        }
        finally
        {
            audio?.source.Dispose();
            MF.Release(writer);
            MF.Release(reader);
        }
    }

    /// <summary>
    /// H.264/HEVC video with AAC audio can move into MP4/MOV without re-encoding: instant and lossless.
    /// Returns false (leaving nothing behind) when the streams don't fit or Windows refuses.
    /// </summary>
    static bool TryRemux(string input, string output, IProgress<double?> progress, CancellationToken ct)
    {
        IMFSourceReader? reader = null;
        IMFSinkWriter? writer = null;
        var types = new List<IMFMediaType>();
        try
        {
            var timing = Mp4Info.Read(input);
            MF.MFCreateSourceReaderFromURL(input, null, out reader);
            reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
            if (reader.GetNativeMediaType(MF.SourceReaderFirstVideo, 0, out var video) < 0) return false;
            types.Add(video);
            var vcodec = video.GetGuid(MF.MT_SUBTYPE);
            if (vcodec != MF.VideoH264 && vcodec != MF.VideoHEVC) return false;
            IMFMediaType? audio = null;
            if (reader.GetNativeMediaType(MF.SourceReaderFirstAudio, 0, out var a) >= 0)
            {
                types.Add(a);
                var codec = a.GetGuid(MF.MT_SUBTYPE);
                // FFmpeg can copy these too, so there's no need to encode the picture again.
                if (codec == MF.AudioMP3 || codec == MF.AudioAC3 || codec == MF.AudioEAC3 || codec == MF.AudioALAC || codec == MF.AudioMp4ALAC)
                    throw new NotSupportedException("This video's tracks can be copied, but not by Windows.");
                if (codec != MF.AudioAAC) return false;
                audio = a;
            }
            else if (MediaCheck.HasAudio(input) == true) throw new NotSupportedException("Windows can't read this video's audio track.");

            // Windows' copy loses edit lists (B-frame and encoder delays, a track that starts late) and further
            // audio tracks. FFmpeg's keeps them.
            EnsureOneAudioTrack(reader, timing);
            if (timing != null && timing.Tracks.Exists(t => t.Trimmed || t.Video && t.Delayed || t.Audio && Math.Abs(t.Start ?? 0) > 0.03))
                throw new NotSupportedException("This video has an edit list, which needs FFmpeg.");

            reader.SetStreamSelection(MF.SourceReaderFirstVideo, true);
            if (reader.SetCurrentMediaType(MF.SourceReaderFirstVideo, IntPtr.Zero, video) < 0) return false;
            if (audio != null)
            {
                reader.SetStreamSelection(MF.SourceReaderFirstAudio, true);
                if (reader.SetCurrentMediaType(MF.SourceReaderFirstAudio, IntPtr.Zero, audio) < 0) return false;
            }

            writer = CreateWriter(output, MF.ContainerMPEG4);
            writer.AddStream(video, out int vs);
            writer.SetInputMediaType(vs, video, null);
            int @as = -1;
            if (audio != null)
            {
                writer.AddStream(audio, out @as);
                writer.SetInputMediaType(@as, audio, null);
            }
            writer.BeginWriting();

            // Map the reader's stream indices to the writer's.
            int readerVideo = -1, readerAudio = -1;
            for (int i = 0; ; i++)
            {
                if (reader.GetNativeMediaType(i, 0, out var t) < 0) break;
                var major = t.GetGuid(MF.MT_MAJOR_TYPE);
                MF.Release(t);
                if (major == MF.MediaTypeVideo && readerVideo < 0) readerVideo = i;
                if (major == MF.MediaTypeAudio && readerAudio < 0) readerAudio = i;
            }

            long duration = MF.Duration(reader) ?? 0, end = 0;
            int samples = 0;
            var firsts = new Dictionary<int, long>();
            var ended = new HashSet<int>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                reader.ReadSample(MF.SourceReaderAnyStream, 0, out int index, out int flags, out long ts, out var sample);
                try
                {
                    if ((flags & MF.ReaderError) != 0) return Fail("read error");
                    if (sample != null)
                    {
                        int target = index == readerVideo ? vs : index == readerAudio ? @as : -1;
                        if (firsts.TryAdd(target, ts) && firsts.Count == 2 && Math.Abs(firsts.Values.First() - firsts.Values.Last()) > 600_000)
                            throw new NotSupportedException("This video's tracks start at different times, which needs FFmpeg.");
                        if (target >= 0) writer.WriteSample(target, sample);
                        samples++;
                        end = Math.Max(end, ts + (sample.GetSampleDuration(out long length) >= 0 ? length : 0));
                        if (duration > 0) progress.Report(Math.Min(0.99, (double)ts / duration));
                    }
                }
                finally { MF.Release(sample); }
                if ((flags & MF.ReaderEndOfStream) != 0)
                {
                    ended.Add(index);
                    if (ended.Contains(readerVideo) && (audio == null || ended.Contains(readerAudio))) break;
                    reader.SetStreamSelection(index, false);
                }
            }
            if (samples == 0) return Fail("no samples");
            if (duration > 0 && duration - end > Math.Max(10_000_000, duration / 50))
                throw new InvalidOperationException($"Only {MediaCheck.Clock(end)} of {MediaCheck.Clock(duration)} could be read.");
            writer.DoFinalize();
            return true;
        }
        catch (COMException ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            foreach (var t in types) MF.Release(t);
            MF.Release(writer);
            MF.Release(reader);
        }

        static bool Fail(string why)
        {
            Log.Write($"Remux not possible ({why}); re-encoding instead.");
            return false;
        }
    }

    /// <summary>
    /// Windows writes ISO MP4; QuickTime's .mov is the same box structure with a different brand,
    /// so relabelling the file-type box makes it a proper .mov.
    /// </summary>
    static void MarkAsQuickTime(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var head = new byte[64];
        int n = f.Read(head, 0, head.Length);
        if (n < 16 || head[4] != 'f' || head[5] != 't' || head[6] != 'y' || head[7] != 'p') return;
        int size = head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3];
        if (size < 16 || size > n) return;
        "qt  "u8.CopyTo(head.AsSpan(8));
        head[12] = 0; head[13] = 0; head[14] = 0x02; head[15] = 0x00; // minor version 0x200
        for (int i = 16; i + 4 <= size; i += 4) "qt  "u8.CopyTo(head.AsSpan(i));
        f.Position = 0;
        f.Write(head, 0, size);
    }

    // ------------------------------------------------------------------ GIF

    /// <summary>Animated WebP: full colour (no palette) and much smaller than GIF.</summary>
    static void VideoToWebP(string input, string output, IProgress<double?> progress, CancellationToken ct)
    {
        Conversion.WebP.AnimatedWriter? writer = null;
        try
        {
            ReadGifFrames(input, 15, 720, ct, (pixels, w, h, delayMs, _) =>
            {
                writer ??= new Conversion.WebP.AnimatedWriter(output, w, h, 75);
                writer.AddFrame(pixels, delayMs);
            }, p => progress.Report(p * 0.99));
            if (writer == null) throw new ConversionException("This video has no pictures to convert.");
            writer.Finish();
        }
        finally { writer?.Dispose(); }
    }

    static void VideoToGif(string input, string output, IProgress<double?> progress, CancellationToken ct)
    {
        const double Fps = 15;
        const int MaxWidth = 720;

        // Pass 1 builds a palette from the whole clip; pass 2 encodes.
        var histogram = new ColorHistogram();
        int width = 0, height = 0;
        long duration = 0;
        ReadGifFrames(input, Fps, MaxWidth, ct, (pixels, w, h, _, total) =>
        {
            width = w; height = h; duration = total;
            histogram.Add(pixels, step: 3);
        }, p => progress.Report(p * 0.4));

        if (width == 0) throw new ConversionException("This video has no pictures to convert.");
        using var gif = new GifWriter(output, width, height, histogram);
        ReadGifFrames(input, Fps, MaxWidth, ct, (pixels, _, _, delayMs, _) => gif.AddFrame(pixels, delayMs), p => progress.Report(0.4 + p * 0.59));
    }

    /// <summary>Decodes frames at a reduced rate and size as top-down BGRA.</summary>
    static void ReadGifFrames(string input, double fps, int maxWidth, CancellationToken ct,
        Action<byte[], int, int, int, long> onFrame, Action<double> onProgress)
    {
        var reader = OpenVideoReader(input, out var info);
        try
        {
            // GIFs have no rotation flag: any rotation Windows doesn't apply, we apply to the pixels.
            var (decodedWidth, decodedHeight) = DecodedSize(info);
            int rotate = PendingRotation(info);
            double visibleWidth = rotate is 90 or 270 ? decodedHeight : decodedWidth;
            double scale = Math.Min(1, maxWidth / visibleWidth);
            int width = (int)(decodedWidth * scale) & ~1;
            int height = (int)Math.Round(decodedHeight * scale) & ~1;
            var want = MF.NewType(MF.MediaTypeVideo, MF.VideoRGB32);
            want.SetSize(MF.MT_FRAME_SIZE, width, height);
            want.SetSize(MF.MT_PIXEL_ASPECT_RATIO, 1, 1);
            want.SetInt(MF.MT_INTERLACE_MODE, 2);
            int hr = reader.SetCurrentMediaType(MF.SourceReaderFirstVideo, IntPtr.Zero, want);
            MF.Release(want);
            if (hr < 0) throw new NotSupportedException("Windows can't decode this video.");
            reader.GetCurrentMediaType(MF.SourceReaderFirstVideo, out var current);
            int stride = current.GetInt(MF.MT_DEFAULT_STRIDE) ?? width * 4;
            MF.Release(current);

            var (frameWidth, frameHeight) = FrameRotation.Size(width, height, rotate);
            long step = (long)(10_000_000 / fps), nextTime = 0, total = info.Duration ?? 0;
            byte[]? pending = null;
            long pendingTime = 0, end = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                reader.ReadSample(MF.SourceReaderFirstVideo, 0, out _, out int flags, out long ts, out var sample);
                if ((flags & MF.ReaderError) != 0) throw new InvalidOperationException("Windows couldn't decode this video.");
                if (sample != null)
                {
                    end = Math.Max(end, ts + (sample.GetSampleDuration(out long length) >= 0 ? length : 0));
                    if (ts >= nextTime || pending == null)
                    {
                        var pixels = FrameRotation.Bgra(Pixels(sample, width, height, stride), width, height, rotate);
                        if (pending != null) onFrame(pending, frameWidth, frameHeight, (int)(ts / 10_000 - pendingTime / 10_000), total);
                        pending = pixels;
                        pendingTime = ts;
                        nextTime = ts + step - step / 4;
                        if (total > 0) onProgress(Math.Min(1, (double)ts / total));
                    }
                    MF.Release(sample);
                }
                if ((flags & MF.ReaderEndOfStream) != 0) break;
            }
            // The last picture stays until the video ends.
            if (pending != null) onFrame(pending, frameWidth, frameHeight, (int)(Math.Max(end - pendingTime, step / 2) / 10_000), total);
        }
        finally { MF.Release(reader); }
    }

    static byte[] Pixels(IMFSample sample, int width, int height, int defaultStride)
    {
        var pixels = new byte[width * height * 4];
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            IntPtr top;
            int pitch;
            bool locked2D = buffer is IMF2DBuffer;
            if (buffer is IMF2DBuffer twoD) twoD.Lock2D(out top, out pitch);
            else
            {
                buffer.Lock(out var start, out _, out _);
                pitch = defaultStride;
                top = pitch < 0 ? start + (height - 1) * -pitch : start;
            }
            try
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(top + y * pitch, pixels, y * width * 4, width * 4);
                    for (int x = 0; x < width; x++) pixels[(y * width + x) * 4 + 3] = 255;
                }
            }
            finally
            {
                if (locked2D) ((IMF2DBuffer)buffer).Unlock2D();
                else buffer.Unlock();
            }
        }
        finally { MF.Release(buffer); }
        return pixels;
    }

    static void GifToVideo(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        var frames = Animation.Read(input);
        if (frames.Delays.Count == 0) throw new ConversionException("This animation has no frames.");
        if (ext == "mkv")
        {
            // Encode to MP4 first, then rewrap.
            var temp = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}.mp4");
            try
            {
                FramesToVideo(frames, temp, "mp4", p => progress.Report(p * 0.8), ct);
                MatroskaRemuxer.Convert(temp, output, null, ct);
            }
            finally { try { File.Delete(temp); } catch { } }
            return;
        }
        FramesToVideo(frames, output, ext, p => progress.Report(p), ct);
        if (ext == "mov") MarkAsQuickTime(output);
    }

    internal static void FramesToVideo(Animation frames, string output, string ext, Action<double> progress, CancellationToken ct)
    {
        int width = frames.Width & ~1, height = frames.Height & ~1;
        if (width < 2 || height < 2) throw new ConversionException("This animation is too small to make a video from.");
        if (frames.Delays.Count == 0) throw new ConversionException("This animation has no frames.");
        double averageMs = frames.Delays.Average();
        int rateNum = (int)Math.Round(1000 * 1000 / averageMs), rateDen = 1000;

        var writer = CreateWriter(output, Container(ext));
        try
        {
            var (stream, _) = AddVideoStream(writer, ext, width, height, rateNum, rateDen);
            writer.BeginWriting();
            long time = 0;
            var nv12 = new byte[width * height * 3 / 2];
            int i = 0;
            foreach (var f in frames)
            {
                ct.ThrowIfCancellationRequested();
                ToNv12(f.Bgra, f.Width, width, height, nv12);
                long duration = f.DelayMs * 10_000L;
                var sample = MF.SampleFrom(nv12, time, duration);
                writer.WriteSample(stream, sample);
                MF.Release(sample);
                time += duration;
                progress((double)++i / frames.Delays.Count);
            }
            writer.DoFinalize();
        }
        finally { MF.Release(writer); }
    }

    /// <summary>
    /// BGRA (transparency composited onto white) to NV12, limited range. Players assume BT.709 colours
    /// for HD sizes and BT.601 below that, so use the matching matrix.
    /// </summary>
    static void ToNv12(byte[] bgra, int sourceWidth, int width, int height, byte[] nv12)
    {
        int uvBase = width * height;
        bool hd = height >= 720 || width >= 1280;
        var (yr, yg, yb, ur, ug, ub, vr, vg, vb) = hd ? (47, 157, 16, -26, -87, 113, 112, -102, -10) : (66, 129, 25, -38, -74, 112, 112, -94, -18);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = Rgb(bgra, (y * sourceWidth + x) * 4);
                nv12[y * width + x] = (byte)(((yr * r + yg * g + yb * b + 128) >> 8) + 16);
            }
        for (int y = 0; y < height; y += 2)
            for (int x = 0; x < width; x += 2)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        var p = Rgb(bgra, ((y + dy) * sourceWidth + x + dx) * 4);
                        r += p.r; g += p.g; b += p.b;
                    }
                r /= 4; g /= 4; b /= 4;
                int i = uvBase + (y / 2) * width + x;
                nv12[i] = (byte)(((ur * r + ug * g + ub * b + 128) >> 8) + 128);
                nv12[i + 1] = (byte)(((vr * r + vg * g + vb * b + 128) >> 8) + 128);
            }

        static (int r, int g, int b) Rgb(byte[] p, int i)
        {
            int a = p[i + 3];
            int white = 255 * (255 - a);
            return ((p[i + 2] * a + white) / 255, (p[i + 1] * a + white) / 255, (p[i] * a + white) / 255);
        }
    }
}
