namespace ConvertWheel.Conversion.Media;

/// <summary>Converts audio (including the audio track of videos) without FFmpeg.</summary>
internal static class AudioEngine
{
    static readonly HashSet<string> Outputs = ["mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "wma", "aiff", "m4r"];

    public static bool CanEncode(string ext) => Outputs.Contains(ext);

    public static void Convert(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        var tags = ReadTags(input);
        if (TryCopy(input, output, ext, ct))
        {
            WriteTags(tags, output, ext);
            return;
        }

        using IAudioSource chain = Prepare(Open(input), ext, out int bits);
        using (var sink = CreateSink(output, ext, chain.Format, bits, tags))
        {
            var buffer = new float[8192 * chain.Format.Channels];
            long frames = 0, total = chain.TotalFrames ?? 0;
            int n;
            while ((n = chain.Read(buffer, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sink.Write(buffer, n);
                frames += n / chain.Format.Channels;
                if (total > 0) progress.Report(Math.Min(0.99, (double)frames / total));
            }
            if (frames == 0) throw new ConversionException("This file has no audio to convert.");
            sink.Finish();
        }
        WriteTags(tags, output, ext);
    }

    static void WriteTags(MediaTags tags, string output, string ext)
    {
        if (ext is "mp3" or "m4a" or "m4r" or "wma" or "flac") tags.WriteWithWindows(output);
    }

    // ------------------------------------------------------------------ sources

    public static IAudioSource Open(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext is "aif" or "aiff" or "aifc") return new AiffSource(path);
        if (ext is "ogg" or "oga" or "opus")
        {
            var (kind, channels) = SniffOgg(path);
            if (kind == "opus") return new OpusSource(path, channels);
            if (kind == "vorbis") return new VorbisSource(path);
        }
        return new MfAudioSource(path);
    }

    /// <summary>Ogg is a container: the first page says whether it holds Vorbis, Opus or something else.</summary>
    static (string? kind, int channels) SniffOgg(string path)
    {
        var head = new byte[512];
        int n;
        using (var f = File.OpenRead(path)) n = f.Read(head, 0, head.Length);
        var span = head.AsSpan(0, n);
        int opus = span.IndexOf("OpusHead"u8);
        if (opus >= 0 && opus + 9 < n) return ("opus", head[opus + 9]);
        if (span.IndexOf("\u0001vorbis"u8) >= 0) return ("vorbis", 0);
        return (null, 0);
    }

    static MediaTags ReadTags(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext is "ogg" or "oga" or "opus")
            {
                using var source = Open(path);
                if (source is VorbisSource v)
                    return MediaTags.FromVorbisComments(v.Reader.Tags.All.SelectMany(kv => kv.Value.Select(value => new KeyValuePair<string, string>(kv.Key, value))));
                if (source is OpusSource o && o.Tags != null)
                    return MediaTags.FromVorbisComments(o.Tags.Fields);
            }
            return MediaTags.ReadWithWindows(path);
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't read tags from {path}: {ex.Message}");
            return new MediaTags();
        }
    }

    // ------------------------------------------------------------------ format requirements

    /// <summary>Adds channel/sample-rate conversion where the target codec needs it.</summary>
    public static IAudioSource Prepare(IAudioSource source, string ext, out int bits)
    {
        int maxChannels = ext is "wav" or "flac" or "aiff" ? 8 : 2;
        IAudioSource chain = source;
        if (chain.Format.Channels > maxChannels) chain = new ChannelMixer(chain, maxChannels);

        int rate = chain.Format.SampleRate;
        int[]? allowed = ext switch
        {
            "mp3" => [32000, 44100, 48000],
            "m4a" or "m4r" or "aac" => [44100, 48000],
            "wma" => [44100], // Windows' WMA encoder only offers 44.1 kHz at good bitrates
            _ => null,
        };
        // Nearest supported rate at or above the source (keeps the full frequency range), else the highest.
        if (allowed != null && !allowed.Contains(rate)) rate = allowed.Where(r => r >= rate).DefaultIfEmpty(allowed.Max()).Min();
        if (ext == "flac" && rate > 192000) rate = 192000;
        if (rate != chain.Format.SampleRate) chain = new Resampler(chain, rate);

        bits = ext is "wav" or "flac" or "aiff" && source.SourceBits >= 24 ? 24 : 16;
        return chain;
    }

    // ------------------------------------------------------------------ sinks

    static IAudioSink CreateSink(string path, string ext, AudioFormat format, int bits, MediaTags tags) => ext switch
    {
        "wav" => new WavSink(path, format, bits),
        "aiff" => new AiffSink(path, format, bits),
        "ogg" => new VorbisSink(path, format, tags),
        "opus" => new OpusSink(path, format, tags),
        "mp3" => new MfAudioSink(path, MF.ContainerMP3, Mp3Type(format), format, 16),
        "m4a" or "m4r" => new MfAudioSink(path, MF.ContainerMPEG4, AacType(format), format, 16), // M4R is an iPhone ringtone: AAC in MP4
        "aac" => new MfAudioSink(path, MF.ContainerADTS, AacType(format), format, 16),
        "wma" => new MfAudioSink(path, MF.ContainerASF, WmaType(format), format, 16),
        "flac" => new MfAudioSink(path, MF.ContainerFLAC, FlacType(format, bits), format, bits),
        _ => throw new NotSupportedException($"No built-in {ext} encoder."),
    };

    static IMFMediaType Mp3Type(AudioFormat f)
    {
        var t = MF.NewType(MF.MediaTypeAudio, MF.AudioMP3);
        t.SetInt(MF.MT_AUDIO_NUM_CHANNELS, f.Channels);
        t.SetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND, f.SampleRate);
        t.SetInt(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, f.Channels == 1 ? 16000 : 32000); // 128 / 256 kbps
        t.SetInt(MF.MT_AUDIO_BLOCK_ALIGNMENT, 1);
        return t;
    }

    public static IMFMediaType AacType(AudioFormat f)
    {
        var t = MF.NewType(MF.MediaTypeAudio, MF.AudioAAC);
        t.SetInt(MF.MT_AUDIO_NUM_CHANNELS, f.Channels);
        t.SetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND, f.SampleRate);
        t.SetInt(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
        t.SetInt(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, f.Channels == 1 ? 16000 : 24000); // 128 / 192 kbps
        t.SetInt(MF.MT_AAC_PAYLOAD_TYPE, 0);
        return t;
    }

    /// <summary>WMA needs codec-specific data, so ask Windows for its exact supported types and pick the closest.</summary>
    public static IMFMediaType WmaType(AudioFormat f)
    {
        var subtype = MF.AudioWMAV8;
        MF.MFTranscodeGetAudioOutputAvailableTypes(ref subtype, 0x3F, null, out var types);
        IMFMediaType? best = null;
        try
        {
            types.GetElementCount(out int count);
            int bestScore = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                types.GetElement(i, out var element);
                var t = (IMFMediaType)element;
                int score = Math.Abs((t.GetInt(MF.MT_AUDIO_AVG_BYTES_PER_SECOND) ?? 0) - 24000)
                            + (t.GetInt(MF.MT_AUDIO_NUM_CHANNELS) == f.Channels ? 0 : 1_000_000)
                            + (t.GetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND) == f.SampleRate ? 0 : 1_000_000);
                if (score < bestScore)
                {
                    if (best != null) MF.Release(best);
                    best = t;
                    bestScore = score;
                }
                else MF.Release(t);
            }
            if (best == null || bestScore >= 1_000_000) throw new NotSupportedException($"Windows has no WMA encoder for {f.SampleRate} Hz, {f.Channels} channels.");
            var result = best;
            best = null;
            return result;
        }
        finally { MF.Release(best); MF.Release(types); }
    }

    static IMFMediaType FlacType(AudioFormat f, int bits)
    {
        var t = MF.NewType(MF.MediaTypeAudio, MF.AudioFLAC);
        t.SetInt(MF.MT_AUDIO_NUM_CHANNELS, f.Channels);
        t.SetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND, f.SampleRate);
        t.SetInt(MF.MT_AUDIO_BITS_PER_SAMPLE, bits);
        return t;
    }

    // ------------------------------------------------------------------ lossless copy

    /// <summary>
    /// When the audio is already in the target codec (e.g. the AAC track of an MP4 going to M4A), copy it
    /// into the new container untouched: instant and lossless.
    /// </summary>
    static bool TryCopy(string input, string output, string ext, CancellationToken ct)
    {
        // Formats only our managed decoders read (Ogg, Opus, AIFF) can't be copied through Windows.
        if (Path.GetExtension(input).ToLowerInvariant() is ".ogg" or ".oga" or ".opus" or ".aif" or ".aiff" or ".aifc") return false;
        Guid? codec = ext switch { "m4a" or "m4r" or "aac" => MF.AudioAAC, "mp3" => MF.AudioMP3, _ => null };
        if (codec == null) return false;
        var container = ext switch { "mp3" => MF.ContainerMP3, "aac" => MF.ContainerADTS, _ => MF.ContainerMPEG4 };

        IMFSourceReader? reader = null;
        IMFSinkWriter? writer = null;
        IMFMediaType? native = null;
        try
        {
            MF.EnsureStarted();
            MF.MFCreateSourceReaderFromURL(input, null, out reader);
            reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
            reader.SetStreamSelection(MF.SourceReaderFirstAudio, true);
            if (reader.GetNativeMediaType(MF.SourceReaderFirstAudio, 0, out native) < 0 || native.GetGuid(MF.MT_SUBTYPE) != codec) return false;
            if (reader.SetCurrentMediaType(MF.SourceReaderFirstAudio, IntPtr.Zero, native) < 0) return false;

            MF.MFCreateAttributes(out var attrs, 2);
            try
            {
                attrs.SetGUID(MF.TRANSCODE_CONTAINERTYPE, container);
                attrs.SetInt(MF.SINK_WRITER_DISABLE_THROTTLING, 1);
                MF.MFCreateSinkWriterFromURL(output, IntPtr.Zero, attrs, out writer);
            }
            finally { MF.Release(attrs); }
            writer.AddStream(native, out int stream);
            writer.SetInputMediaType(stream, native, null);
            writer.BeginWriting();
            int written = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                reader.ReadSample(MF.SourceReaderFirstAudio, 0, out _, out int flags, out _, out var sample);
                try
                {
                    if ((flags & MF.ReaderError) != 0) return false;
                    if (sample != null)
                    {
                        writer.WriteSample(stream, sample);
                        written++;
                    }
                }
                finally { MF.Release(sample); }
                if ((flags & MF.ReaderEndOfStream) != 0) break;
            }
            if (written == 0) return false;
            writer.DoFinalize();
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or NotSupportedException)
        {
            Log.Write($"Lossless audio copy not possible ({ex.Message}); re-encoding instead.");
            return false;
        }
        finally
        {
            MF.Release(native);
            MF.Release(writer);
            MF.Release(reader);
        }
    }
}
