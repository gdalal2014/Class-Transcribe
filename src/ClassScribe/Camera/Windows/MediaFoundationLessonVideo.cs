using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using NAudio.MediaFoundation;

namespace ClassScribe.Camera.Windows;

/// <summary>
/// A lesson's video made with Media Foundation (teach-video 6.9; see <see cref="ILessonVideoMedia"/>), through NAudio's
/// declarations of the source reader and the sink writer, with ClassScribe's own GUIDs (<see cref="VideoGuids"/>,
/// <see cref="LessonVideoGuids"/>). It opens no device.
/// </summary>
/// <remarks>
/// <para>Passed through, each slice's H.264 samples are read as they are (the source reader's native type, no decoder) and
/// written to the sink writer with the same type as input and output, so that no encoder is loaded: each at its source's
/// time plus the slice's place, the first of the video at 0 (a piece's own first frame is black), each lasting until the
/// next and the last until the plan's end. Encoded again, each slice is decoded to NV12 at the plan's size (the source
/// reader's advanced video processing scales a source of another size) and encoded once more by Windows' software H.264
/// encoder with the pieces' settings (<see cref="PieceSettings"/>), which gives the whole picture one sequence header.</para>
/// <para>The sound is read from the mix as it is written, a tenth of a second at a time, interleaved with the picture by
/// time, as 16-bit PCM at the mix's rate, and encoded by Windows' AAC encoder, AAC-LC mono at 128 kbit/s; the click
/// test of <c>LessonVideoMediaTests</c> finds the encoder's priming accounted for, the sound within a frame's time of where
/// the .wav has it, so nothing is added to the picture's times. The file is a plain MP4 (MFTranscodeContainerType_MPEG4)
/// over a file ClassScribe makes itself, others allowed to read it, so that the recording counts as being written
/// meanwhile. Its index (moov) comes after its data: Windows' MP4 sink, asked for MF_MPEG4SINK_MOOV_BEFORE_MDAT through the
/// sink writer's attributes, the media sink's and the media type, kept it at the end on 23 October (Windows 11 26300), and
/// players read it there; a player streaming from Drive waits for the end of the file first.</para>
/// <para>In a passed-through picture only IDR pictures stay marked as clean points (see <see cref="VideoWriter"/>), since
/// Windows' software encoder also marks its other I pictures so, and Windows' decoder sought there gives nothing.</para>
/// <para>The check reads the file back with the source reader: a picture and a sound, its length from the media source's
/// duration, and a frame decoded at each place asked: after a seek there, the decoded frame that covers it.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationLessonVideo : ILessonVideoMedia
{
    /// <summary>The sound's bitrate in the video, in bytes a second: 128 kbit/s, as YouTube asks for mono.</summary>
    public const int SoundBytesPerSecond = 16_000;

    /// <summary>How much sound goes in one sample of the sink writer: a tenth of a second.</summary>
    private static readonly TimeSpan SoundChunk = TimeSpan.FromMilliseconds(100);

    /// <summary>MF_E_ATTRIBUTENOTFOUND: an attribute that is not there.</summary>
    private const int AttributeNotFound = unchecked((int)0xC00D36E6);

    /// <inheritdoc />
    public VideoSourceFacts Describe(string path)
    {
        MediaFoundationApi.Startup();
        var reader = OpenReader(path, attributes: null);
        try
        {
            reader.GetNativeMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out var type);
            try
            {
                type.GetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, out var subtype);
                if (subtype != VideoGuids.H264) throw new InvalidDataException("its picture is not H.264");

                type.GetUINT64(VideoGuids.FrameSize, out var packed);
                var length = Duration(reader) is { } duration && duration > TimeSpan.Zero ? duration : ToItsEnd;
                return new VideoSourceFacts((int)(packed >> 32), (int)(packed & 0xFFFFFFFF), Blob(type, LessonVideoGuids.SequenceHeader), length);
            }
            finally
            {
                Marshal.ReleaseComObject(type);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    /// <summary>
    /// The length a source is given when its file does not say its duration, as a fragmented piece may not: a day, which
    /// the plan cuts at the next slice and the sound's end, and the writer at the source's last sample.
    /// </summary>
    public static readonly TimeSpan ToItsEnd = TimeSpan.FromDays(1);

    /// <inheritdoc />
    public void Write(LessonVideoPlan plan, MixedSound sound, string path, Action<double> progress, CancellationToken cancellationToken)
    {
        MediaFoundationApi.Startup();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
        using var writer = new VideoWriter(file, plan, sound);
        writer.Open(VideoTypeOf(plan));
        foreach (var slice in plan.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteSlice(slice, progress, cancellationToken);
        }

        writer.Finish(cancellationToken);
        progress(1);
    }

    /// <inheritdoc />
    public string? Check(string path, LessonVideoCheck check)
    {
        MediaFoundationApi.Startup();
        IMFSourceReader reader;
        try
        {
            reader = OpenReader(path, attributes: null);
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: a file Media Foundation cannot open has failed the check, which says so.
            return $"Windows cannot open it ({CameraCallLog.Describe(ex).Error})";
        }

        try
        {
            if (!HasStream(reader, MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM)) return "it has no sound";
            if (!HasStream(reader, MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM)) return "it has no picture";

            var length = Duration(reader) ?? TimeSpan.Zero;
            if ((length - check.Length).Duration() > LessonVideoCheck.Tolerance)
                return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"it is {length.TotalSeconds:0.000} seconds long, not {check.Length.TotalSeconds:0.000} as its sound is");

            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);
            var decoded = MediaFoundationApi.CreateMediaType();
            try
            {
                decoded.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
                decoded.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, VideoGuids.Nv12);
                reader.SetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, decoded);
            }
            finally
            {
                Marshal.ReleaseComObject(decoded);
            }

            foreach (var at in check.FramesAt)
            {
                if (!DecodesAt(reader, at)) return $"no frame decodes at {Durations.Format(at)}.{at.Milliseconds:000}";
            }

            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    /// <summary>
    /// True when a frame decodes at <paramref name="at"/>: after a seek there, which lands on the keyframe before it, the
    /// frames are decoded up to the one shown at it, the last that begins at or before it (each lasts until the next), and
    /// that one holds a picture. A decoder may give its frames no duration, so the next frame's beginning, or the end of the
    /// stream, is what ends the one shown.
    /// </summary>
    private static bool DecodesAt(IMFSourceReader reader, TimeSpan at)
    {
        SetPosition(reader, at < TimeSpan.Zero ? TimeSpan.Zero : at);
        var shown = false;
        while (true)
        {
            reader.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out var time, out var sample);
            if (sample is null)
            {
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return shown;
                continue;
            }

            try
            {
                sample.GetTotalLength(out var bytes);
                if ((long)time > at.Ticks) return shown || (bytes > 0 && (long)time - at.Ticks < LessonVideoPlan.BeforeAJoin.Ticks);

                shown = bytes > 0;
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
    }

    /// <summary>The video stream's media type for the sink writer: the first slice's own H.264 type when passed through, else the encoder's H.264 at the plan's size.</summary>
    private static IMFMediaType VideoTypeOf(LessonVideoPlan plan)
    {
        var type = MediaFoundationApi.CreateMediaType();
        if (!plan.EncodeAgain)
        {
            var reader = OpenReader(plan.Slices[0].Path, attributes: null);
            try
            {
                reader.GetNativeMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out var native);
                try
                {
                    native.CopyAllItems(type);
                }
                finally
                {
                    Marshal.ReleaseComObject(native);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(reader);
            }

            return type;
        }

        var settings = PieceSettings.For(new CameraSize(plan.Size.Width, plan.Size.Height), software: true);
        type.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
        type.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, VideoGuids.H264);
        type.SetUINT32(MediaFoundationAttributes.MF_MT_AVG_BITRATE, settings.BitsPerSecond);
        type.SetUINT32(VideoGuids.InterlaceMode, VideoGuids.Progressive);
        type.SetUINT64(VideoGuids.FrameSize, Pack(plan.Size.Width, plan.Size.Height));
        type.SetUINT64(VideoGuids.FrameRate, Pack(PieceSettings.FramesPerSecond, 1));
        type.SetUINT64(VideoGuids.PixelAspectRatio, Pack(1, 1));
        type.SetUINT32(VideoGuids.Mpeg2Profile, PieceSettings.MainProfile);
        type.SetUINT32(VideoGuids.Mpeg2Level, settings.Level);
        return type;
    }

    /// <summary>A source reader over <paramref name="path"/>, with <paramref name="attributes"/> when given.</summary>
    private static IMFSourceReader OpenReader(string path, IMFAttributes? attributes)
    {
        MediaFoundationInterop.MFCreateSourceReaderFromURL(path, attributes, out var reader);
        return reader;
    }

    /// <summary>True when <paramref name="reader"/> has a stream of the kind <paramref name="stream"/> names (the first audio or video stream).</summary>
    private static bool HasStream(IMFSourceReader reader, int stream)
    {
        try
        {
            reader.GetCurrentMediaType(stream, out var type);
            Marshal.ReleaseComObject(type);
            return true;
        }
        catch (COMException)
        {
            // Deliberately not rethrown: MF_E_INVALIDSTREAMNUMBER is the answer, no such stream.
            return false;
        }
    }

    /// <summary>The media source's duration (MF_PD_DURATION); null when it does not say one.</summary>
    private static TimeSpan? Duration(IMFSourceReader reader)
    {
        var variant = Marshal.AllocHGlobal(PropVariantSize);
        try
        {
            ZeroVariant(variant);
            try
            {
                reader.GetPresentationAttribute(MediaFoundationInterop.MF_SOURCE_READER_MEDIASOURCE, MediaFoundationAttributes.MF_PD_DURATION, variant);
            }
            catch (COMException)
            {
                // Deliberately not rethrown: a source that keeps no duration, as a fragmented file may, says none.
                return null;
            }

            return Marshal.ReadInt16(variant) == VtUi8 ? TimeSpan.FromTicks(Marshal.ReadInt64(variant, 8)) : null;
        }
        finally
        {
            PropVariantClear(variant);
            Marshal.FreeHGlobal(variant);
        }
    }

    /// <summary>Seeks <paramref name="reader"/> to <paramref name="at"/>, in 100-ns units (GUID_NULL's time format).</summary>
    private static void SetPosition(IMFSourceReader reader, TimeSpan at)
    {
        var variant = Marshal.AllocHGlobal(PropVariantSize);
        try
        {
            ZeroVariant(variant);
            Marshal.WriteInt16(variant, VtI8);
            Marshal.WriteInt64(variant, 8, at.Ticks);
            reader.SetCurrentPosition(Guid.Empty, variant);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
        }
    }

    /// <summary>A blob attribute of <paramref name="attributes"/>; empty when it is not there.</summary>
    private static byte[] Blob(IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetBlobSize(key, out var size);
            var blob = new byte[size];
            attributes.GetBlob(key, blob, size, out _);
            return blob;
        }
        catch (COMException ex) when (ex.HResult == AttributeNotFound)
        {
            // Deliberately not rethrown: a type without the attribute has none, which compares as an empty header.
            return [];
        }
    }

    /// <summary>The size of a PROPVARIANT on any Windows this runs on.</summary>
    private const int PropVariantSize = 24;

    /// <summary>VT_I8: a PROPVARIANT holding a 64-bit signed number.</summary>
    private const short VtI8 = 20;

    /// <summary>VT_UI8: a PROPVARIANT holding a 64-bit unsigned number.</summary>
    private const short VtUi8 = 21;

    /// <summary>Fills a PROPVARIANT's bytes with zeros: VT_EMPTY.</summary>
    private static void ZeroVariant(IntPtr variant)
    {
        for (var offset = 0; offset < PropVariantSize; offset += 8) Marshal.WriteInt64(variant, offset, 0);
    }

    /// <summary>Lets go of what a PROPVARIANT holds (ole32's PropVariantClear).</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr variant);

    /// <summary>Two numbers in one attribute, the first in the high half, as Media Foundation packs a size or a rate.</summary>
    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    /// <summary>
    /// One writing of a lesson's video: the sink writer over the file, its picture and sound streams, the picture's sample
    /// waiting for the next, which gives it its duration, and the sound written so far.
    /// </summary>
    /// <param name="file">The video's file, made by the caller.</param>
    /// <param name="plan">The plan written.</param>
    /// <param name="sound">The sound, read as it is written.</param>
    private sealed class VideoWriter(FileStream file, LessonVideoPlan plan, MixedSound sound) : IDisposable
    {
        private IMFByteStream? _byteStream;
        private IMFSinkWriter? _writer;
        private int _video;
        private int _audio;
        private IMFSample? _pending;
        private long _pendingAt = -1;
        private long _lastAt = -1;
        private long _soundSamples;
        private bool _soundDone;
        private float[] _floats = [];
        private byte[] _pcm = [];

        /// <summary>Makes the sink writer with the video's two streams and begins writing.</summary>
        public void Open(IMFMediaType videoType)
        {
            MediaFoundationInterop.MFCreateMFByteStreamOnStream(new FileStreamAsIStream(file), out var byteStream);
            _byteStream = byteStream;
            var attributes = MediaFoundationApi.CreateAttributes(3);
            try
            {
                attributes.SetGUID(MediaFoundationAttributes.MF_TRANSCODE_CONTAINERTYPE, LessonVideoGuids.ContainerMpeg4);
                attributes.SetUINT32(MediaFoundationAttributes.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 0);
                MediaFoundationInterop.MFCreateSinkWriterFromURL(null, byteStream, attributes, out var writer);
                _writer = writer;
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }

            try
            {
                _writer.AddStream(videoType, out _video);
                if (plan.EncodeAgain) SetEncoderInput();
                else _writer.SetInputMediaType(_video, videoType, null);
            }
            finally
            {
                Marshal.ReleaseComObject(videoType);
            }

            AddSound();
            _writer.BeginWriting();
        }

        /// <summary>The encoder's input when the picture is encoded again: NV12 at the plan's size, with the pieces' encoder settings.</summary>
        private void SetEncoderInput()
        {
            var settings = PieceSettings.For(new CameraSize(plan.Size.Width, plan.Size.Height), software: true);
            var input = Nv12Type(plan.Size.Width, plan.Size.Height);
            var parameters = MediaFoundationApi.CreateAttributes(4);
            try
            {
                parameters.SetUINT32(VideoGuids.CodecRateControlMode, VideoGuids.ConstantBitrate);
                parameters.SetUINT32(VideoGuids.CodecMeanBitRate, settings.BitsPerSecond);
                parameters.SetUINT32(VideoGuids.CodecGopSize, PieceSettings.KeyframeEvery);
                parameters.SetUINT32(VideoGuids.CodecBPictureCount, 0);
                _writer!.SetInputMediaType(_video, input, parameters);
            }
            finally
            {
                Marshal.ReleaseComObject(input);
                Marshal.ReleaseComObject(parameters);
            }
        }

        /// <summary>The sound's stream: AAC-LC mono at 128 kbit/s out, 16-bit PCM at the mix's rate in.</summary>
        private void AddSound()
        {
            var output = MediaFoundationApi.CreateMediaType();
            var input = MediaFoundationApi.CreateMediaType();
            try
            {
                output.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Audio);
                output.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, AudioSubtypes.MFAudioFormat_AAC);
                output.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                output.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_SAMPLES_PER_SECOND, sound.SampleRate);
                output.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_NUM_CHANNELS, 1);
                output.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, SoundBytesPerSecond);
                output.SetUINT32(MediaFoundationAttributes.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
                _writer!.AddStream(output, out _audio);

                input.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Audio);
                input.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, AudioSubtypes.MFAudioFormat_PCM);
                input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_SAMPLES_PER_SECOND, sound.SampleRate);
                input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_NUM_CHANNELS, 1);
                input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_BLOCK_ALIGNMENT, 2);
                input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sound.SampleRate * 2);
                _writer.SetInputMediaType(_audio, input, null);
            }
            finally
            {
                Marshal.ReleaseComObject(output);
                Marshal.ReleaseComObject(input);
            }
        }

        /// <summary>
        /// Writes one slice's picture: each of its samples within the slice's length, at the slice's place plus its own time,
        /// the very first of the video at 0; a sample that would not come after the last written is dropped. The sound is
        /// written up to each sample's time first, so that the two are interleaved.
        /// </summary>
        public void WriteSlice(PlannedSlice slice, Action<double> progress, CancellationToken cancellationToken)
        {
            var reader = plan.EncodeAgain ? DecodingReader(slice.Path) : OpenReader(slice.Path, attributes: null);
            try
            {
                reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
                reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);
                if (plan.EncodeAgain && _lastAt < 0 && slice.At > TimeSpan.Zero) Add(Black(), 0, cancellationToken);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reader.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out var time, out var sample);
                    if (sample is null)
                    {
                        if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                        continue;
                    }

                    if ((long)time >= slice.Length.Ticks)
                    {
                        Marshal.ReleaseComObject(sample);
                        break;
                    }

                    var at = _lastAt < 0 ? 0 : slice.At.Ticks + (long)time;
                    if (at <= _lastAt)
                    {
                        Marshal.ReleaseComObject(sample);
                        continue;
                    }

                    if (!plan.EncodeAgain) KeepOnlyIdrAsCleanPoint(sample);
                    Add(sample, at, cancellationToken);
                    progress(Math.Min(0.99, (double)at / Math.Max(1, plan.End.Ticks)));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(reader);
            }
        }

        /// <summary>
        /// Leaves a passed-through sample marked as a clean point (MFSampleExtension_CleanPoint, which becomes the MP4's sync
        /// samples) only when it holds an IDR picture, from which a decoder can start. Windows' software encoder also marks
        /// its other I pictures so, after which Windows' decoder, sought there, gives nothing: a seek in the finished video,
        /// the check's and a player's, then lands on the IDR before.
        /// </summary>
        private static void KeepOnlyIdrAsCleanPoint(IMFSample sample)
        {
            try
            {
                sample.GetUINT32(LessonVideoGuids.CleanPoint, out var clean);
                if (clean == 0) return;
            }
            catch (COMException ex) when (ex.HResult == AttributeNotFound)
            {
                // Deliberately not rethrown: a sample not marked is no clean point, which is what it stays.
                return;
            }

            sample.ConvertToContiguousBuffer(out var buffer);
            try
            {
                buffer.Lock(out var pointer, out _, out var length);
                try
                {
                    var bytes = new byte[Math.Min(length, IdrSearchBytes)];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    if (!H264Pictures.HoldsIdr(bytes)) sample.SetUINT32(LessonVideoGuids.CleanPoint, 0);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }

        /// <summary>How far into a sample its picture's kind is looked for: the parameter sets and the first slice's header come first.</summary>
        private const int IdrSearchBytes = 4096;

        /// <summary>The rest of the sound, the last picture lasting to the plan's end, then the file finished.</summary>
        public void Finish(CancellationToken cancellationToken)
        {
            if (_pending is not null) WritePending(Math.Max(1, plan.End.Ticks - _pendingAt));
            WriteSoundUntil(long.MaxValue, cancellationToken);
            _writer!.DoFinalize();
            file.Flush(flushToDisk: true);
        }

        /// <summary>Lets go of the waiting sample, the sink writer and the byte stream.</summary>
        public void Dispose()
        {
            if (_pending is not null) Marshal.ReleaseComObject(_pending);
            if (_writer is not null) Marshal.ReleaseComObject(_writer);
            if (_byteStream is not null) Marshal.ReleaseComObject(_byteStream);
            _pending = null;
            _writer = null;
            _byteStream = null;
        }

        /// <summary>A reader that decodes <paramref name="path"/>'s picture to NV12 at the plan's size, scaling a source of another size.</summary>
        private IMFSourceReader DecodingReader(string path)
        {
            var attributes = MediaFoundationApi.CreateAttributes(1);
            try
            {
                attributes.SetUINT32(LessonVideoGuids.AdvancedVideoProcessing, 1);
                var reader = OpenReader(path, attributes);
                var output = Nv12Type(plan.Size.Width, plan.Size.Height);
                try
                {
                    reader.SetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, output);
                }
                finally
                {
                    Marshal.ReleaseComObject(output);
                }

                return reader;
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }
        }

        /// <summary>A black NV12 frame at the plan's size, as a sample, for the start of a picture encoded again that begins later than 0.</summary>
        private IMFSample Black()
        {
            var frame = CameraPicture.Black(plan.Size.Width, plan.Size.Height);
            var buffer = MediaFoundationApi.CreateMemoryBuffer(frame.Length);
            try
            {
                buffer.Lock(out var pointer, out _, out _);
                try
                {
                    Marshal.Copy(frame, 0, pointer, frame.Length);
                }
                finally
                {
                    buffer.Unlock();
                }

                buffer.SetCurrentLength(frame.Length);
                var sample = MediaFoundationApi.CreateSample();
                sample.AddBuffer(buffer);
                return sample;
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }

        /// <summary>Writes the waiting sample, lasting until <paramref name="at"/>, after the sound up to it; <paramref name="sample"/> waits next.</summary>
        private void Add(IMFSample sample, long at, CancellationToken cancellationToken)
        {
            if (_pending is not null) WritePending(Math.Max(1, at - _pendingAt));
            WriteSoundUntil(at, cancellationToken);
            sample.SetSampleTime(at);
            _pending = sample;
            _pendingAt = at;
            _lastAt = at;
        }

        /// <summary>Writes the waiting sample with <paramref name="duration"/>, and lets go of it.</summary>
        private void WritePending(long duration)
        {
            var pending = _pending!;
            _pending = null;
            try
            {
                pending.SetSampleDuration(duration);
                _writer!.WriteSample(_video, pending);
            }
            finally
            {
                Marshal.ReleaseComObject(pending);
            }
        }

        /// <summary>Writes the sound up to <paramref name="until"/> (100-ns units), a tenth of a second at a time, or to its end.</summary>
        private void WriteSoundUntil(long until, CancellationToken cancellationToken)
        {
            var chunk = (int)(sound.SampleRate * SoundChunk.TotalSeconds);
            if (_floats.Length != chunk)
            {
                _floats = new float[chunk];
                _pcm = new byte[chunk * 2];
            }

            while (!_soundDone && SoundTime(_soundSamples) < until)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = sound.Read(_floats);
                if (read == 0)
                {
                    _soundDone = true;
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    var value = (short)MathF.Round(Math.Clamp(_floats[i], -1f, 1f) * short.MaxValue);
                    _pcm[i * 2] = (byte)value;
                    _pcm[i * 2 + 1] = (byte)(value >> 8);
                }

                var buffer = MediaFoundationApi.CreateMemoryBuffer(read * 2);
                IMFSample? sample = null;
                try
                {
                    buffer.Lock(out var pointer, out _, out _);
                    try
                    {
                        Marshal.Copy(_pcm, 0, pointer, read * 2);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }

                    buffer.SetCurrentLength(read * 2);
                    sample = MediaFoundationApi.CreateSample();
                    sample.AddBuffer(buffer);
                    sample.SetSampleTime(SoundTime(_soundSamples));
                    sample.SetSampleDuration(SoundTime(_soundSamples + read) - SoundTime(_soundSamples));
                    _writer!.WriteSample(_audio, sample);
                    _soundSamples += read;
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                }
            }
        }

        /// <summary>Where <paramref name="samples"/> of the sound end, in 100-ns units.</summary>
        private long SoundTime(long samples) => samples * TimeSpan.TicksPerSecond / sound.SampleRate;

        /// <summary>An NV12 media type of <paramref name="width"/> by <paramref name="height"/> at 30 frames a second, progressive, square pixels.</summary>
        private static IMFMediaType Nv12Type(int width, int height)
        {
            var type = MediaFoundationApi.CreateMediaType();
            type.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
            type.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, VideoGuids.Nv12);
            type.SetUINT32(VideoGuids.InterlaceMode, VideoGuids.Progressive);
            type.SetUINT64(VideoGuids.FrameSize, Pack(width, height));
            type.SetUINT64(VideoGuids.FrameRate, Pack(PieceSettings.FramesPerSecond, 1));
            type.SetUINT64(VideoGuids.PixelAspectRatio, Pack(1, 1));
            type.SetUINT32(VideoGuids.DefaultStride, width);
            type.SetUINT32(MediaFoundationAttributes.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            return type;
        }
    }
}

/// <summary>The GUIDs a lesson's video needs that NAudio does not declare (mfapi.h, mfreadwrite.h, mfidl.h).</summary>
public static class LessonVideoGuids
{
    /// <summary>MFTranscodeContainerType_MPEG4: a plain, not fragmented, MP4.</summary>
    public static readonly Guid ContainerMpeg4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

    /// <summary>MF_MT_MPEG_SEQUENCE_HEADER: an H.264 stream's parameter sets.</summary>
    public static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    /// <summary>MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING: the source reader converts and scales the decoded picture to the type asked for.</summary>
    public static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");

    /// <summary>MFSampleExtension_CleanPoint: a sample a decoder can start from, a sync sample in an MP4.</summary>
    public static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
}

/// <summary>What an H.264 sample's bytes say of its picture, as Media Foundation hands them over (Annex B start codes, or 4-byte lengths).</summary>
public static class H264Pictures
{
    /// <summary>The NAL unit type of an IDR picture's slice.</summary>
    private const int IdrSlice = 5;

    /// <summary>The NAL unit type of any other picture's slice.</summary>
    private const int OtherSlice = 1;

    /// <summary>
    /// True when <paramref name="bytes"/>, the start of an H.264 sample, hold an IDR picture's slice before any other
    /// picture's: the NAL units read by their start codes (00 00 01), or, without one at the start, by 4-byte lengths.
    /// </summary>
    public static bool HoldsIdr(ReadOnlySpan<byte> bytes) =>
        bytes is [0, 0, 1, ..] or [0, 0, 0, 1, ..] ? ByStartCodes(bytes) : ByLengths(bytes);

    /// <summary>The first slice's kind among NAL units marked by start codes.</summary>
    private static bool ByStartCodes(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] != 0 || bytes[i + 1] != 0 || bytes[i + 2] != 1) continue;

            var type = bytes[i + 3] & 0x1F;
            if (type == IdrSlice) return true;
            if (type == OtherSlice) return false;
            i += 3;
        }

        return false;
    }

    /// <summary>The first slice's kind among NAL units each after a 4-byte length.</summary>
    private static bool ByLengths(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 4 < bytes.Length;)
        {
            var length = (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
            var type = bytes[i + 4] & 0x1F;
            if (type == IdrSlice) return true;
            if (type == OtherSlice || length <= 0) return false;
            i += 4 + length;
        }

        return false;
    }
}
