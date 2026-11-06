using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using ClassScribe.Storage;

namespace ClassScribe.Camera.Windows;

/// <summary>Makes pieces with Media Foundation's sink writer (see <see cref="MediaFoundationPieceWriter"/>).</summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationPieceWriterFactory : IPieceWriterFactory
{
    /// <inheritdoc />
    public IPieceWriter Create(string path, PieceSettings settings) => new MediaFoundationPieceWriter(path, settings);
}

/// <summary>
/// A lesson's piece written by Media Foundation's sink writer (teach-video 6.1, 6.5): a fragmented MP4
/// (MFTranscodeContainerType_FMPEG4) holding H.264 only, so that a crash or a killed process leaves it readable to its last
/// fragment, about a second. The encoder is set as <see cref="PieceSettings"/> says, through the encoding parameters the sink
/// writer hands it with the input type (the camera probe's self-check read them back from the encoder's ICodecAPI): Main
/// profile, the level by the size, no B-frames, a keyframe every 30 frames, constant bitrate. The hardware encoder is asked
/// for with MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS unless the settings say software. The file is made by ClassScribe itself,
/// with others allowed to read it, and handed to the sink writer as a byte stream, so that a recording whose piece is being
/// written is seen as such (<see cref="ClassFolders.IsBeingWritten"/>). The black frame at time 0 is the first
/// sample; each sample lasts until the next. Media Foundation's objects are reached through NAudio's declarations;
/// ClassScribe adds the video GUIDs (<see cref="VideoGuids"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationPieceWriter : IPieceWriter
{
    /// <summary>The file, made here and shared for reading.</summary>
    private readonly FileStream _file;

    /// <summary>The bytes of one frame.</summary>
    private readonly int _frameBytes;

    /// <summary>How the piece is encoded.</summary>
    private readonly PieceSettings _settings;

    /// <summary>The sink writer; null once let go.</summary>
    private IMFSinkWriter? _writer;

    /// <summary>The byte stream over the file, which the sink writer writes to; null once let go.</summary>
    private IMFByteStream? _byteStream;

    /// <summary>The picture's stream in the sink writer.</summary>
    private int _stream;

    /// <summary>The sample waiting for the next one, which gives it its duration; null before the first and after the finish.</summary>
    private IMFSample? _pending;

    /// <summary>When the waiting sample is, from the piece's start.</summary>
    private TimeSpan _pendingAt;

    /// <summary>True once the piece is finished: nothing more is written.</summary>
    private bool _finished;

    /// <summary>
    /// Makes the piece at <paramref name="path"/>, which must not be there yet, and starts the sink writer with
    /// <paramref name="settings"/>; the black frame at time 0 waits as the first sample. Throws when the file cannot be made
    /// (an <see cref="IOException"/>) or Windows refuses the encoder or its settings; nothing is left open then.
    /// </summary>
    public MediaFoundationPieceWriter(string path, PieceSettings settings)
    {
        Path = path;
        _settings = settings;
        _frameBytes = settings.FrameBytes;
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
        try
        {
            MediaFoundationApi.Startup();
            Open();
            _pending = MakeSample(CameraPicture.Black(settings.Size.Width, settings.Size.Height), TimeSpan.Zero);
            _pendingAt = TimeSpan.Zero;
        }
        catch
        {
            Release();
            _file.Dispose();
            DeleteHalfMade(path);
            throw;
        }
    }

    /// <summary>
    /// Deletes the half-made piece at <paramref name="path"/> after the encoder or its settings were refused; one that cannot
    /// be deleted now (a scanner holding the new file) is left, so that the refusal itself is what is thrown, and the software
    /// encoder's start then deletes it or says why it cannot.
    /// </summary>
    internal static void DeleteHalfMade(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the encoder's refusal, rethrown by the caller, is the failure to say; an exception
            // from this clean-up would take its place, and turn a refused hardware encoder, which the software encoder
            // replaces, into a disk failure, which costs the picture.
        }
    }

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public string Encoder { get; private set; } = "";

    /// <inheritdoc />
    public void Write(byte[] nv12, TimeSpan at) => Add(MakeSample(nv12, at), at);

    /// <inheritdoc />
    public void WriteBlack(TimeSpan at) => Add(MakeSample(CameraPicture.Black(_settings.Size.Width, _settings.Size.Height), at), at);

    /// <inheritdoc />
    public void Finish(TimeSpan end)
    {
        if (_finished) return;
        _finished = true;
        var writer = _writer ?? throw new ObjectDisposedException(nameof(MediaFoundationPieceWriter));
        if (_pending is not null) WritePending(Max(end - _pendingAt, TimeSpan.FromMilliseconds(1)));
        writer.DoFinalize();
        Release();
        _file.Flush(flushToDisk: true);
        _file.Dispose();
    }

    /// <summary>Lets go of the sink writer and closes the file, finished or not: a piece not finished is readable to its last fragment.</summary>
    public void Dispose()
    {
        Release();
        _file.Dispose();
    }

    /// <summary>The sink writer over the file: the container, the encoder's output type and its settings, the NV12 input, then writing begun.</summary>
    private void Open()
    {
        MediaFoundationInterop.MFCreateMFByteStreamOnStream(new FileStreamAsIStream(_file), out var byteStream);
        _byteStream = byteStream;

        var attributes = MediaFoundationApi.CreateAttributes(2);
        try
        {
            attributes.SetGUID(MediaFoundationAttributes.MF_TRANSCODE_CONTAINERTYPE, VideoGuids.ContainerFragmentedMpeg4);
            attributes.SetUINT32(MediaFoundationAttributes.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, _settings.Software ? 0 : 1);
            MediaFoundationInterop.MFCreateSinkWriterFromURL(null, byteStream, attributes, out var writer);
            _writer = writer;
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        var (width, height) = (_settings.Size.Width, _settings.Size.Height);
        var output = MediaFoundationApi.CreateMediaType();
        var input = MediaFoundationApi.CreateMediaType();
        var parameters = MediaFoundationApi.CreateAttributes(6);
        try
        {
            output.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
            output.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, VideoGuids.H264);
            output.SetUINT32(MediaFoundationAttributes.MF_MT_AVG_BITRATE, _settings.BitsPerSecond);
            output.SetUINT32(VideoGuids.InterlaceMode, VideoGuids.Progressive);
            output.SetUINT64(VideoGuids.FrameSize, Pack(width, height));
            output.SetUINT64(VideoGuids.FrameRate, Pack(PieceSettings.FramesPerSecond, 1));
            output.SetUINT64(VideoGuids.PixelAspectRatio, Pack(1, 1));
            output.SetUINT32(VideoGuids.Mpeg2Profile, PieceSettings.MainProfile);
            output.SetUINT32(VideoGuids.Mpeg2Level, _settings.Level);
            _writer.AddStream(output, out _stream);

            input.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
            input.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, VideoGuids.Nv12);
            input.SetUINT32(VideoGuids.InterlaceMode, VideoGuids.Progressive);
            input.SetUINT64(VideoGuids.FrameSize, Pack(width, height));
            input.SetUINT64(VideoGuids.FrameRate, Pack(PieceSettings.FramesPerSecond, 1));
            input.SetUINT64(VideoGuids.PixelAspectRatio, Pack(1, 1));
            input.SetUINT32(VideoGuids.DefaultStride, width);
            input.SetUINT32(MediaFoundationAttributes.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

            parameters.SetUINT32(VideoGuids.CodecRateControlMode, VideoGuids.ConstantBitrate);
            parameters.SetUINT32(VideoGuids.CodecMeanBitRate, _settings.BitsPerSecond);
            parameters.SetUINT32(VideoGuids.CodecGopSize, PieceSettings.KeyframeEvery);
            parameters.SetUINT32(VideoGuids.CodecBPictureCount, 0);
            _writer.SetInputMediaType(_stream, input, parameters);
        }
        finally
        {
            Marshal.ReleaseComObject(output);
            Marshal.ReleaseComObject(input);
            Marshal.ReleaseComObject(parameters);
        }

        Encoder = NameTheEncoder();
        _writer.BeginWriting();
    }

    /// <summary>
    /// The encoder the sink writer loaded, in words: "hardware encoder (its name)" or "software encoder (its name)", by the
    /// transform's hardware URL and friendly name; by the settings alone when the transform cannot be asked.
    /// </summary>
    private string NameTheEncoder()
    {
        var asked = _settings.Software ? "software encoder" : "hardware encoder";
        IntPtr pointer = IntPtr.Zero;
        try
        {
            var service = Guid.Empty;
            var transformId = typeof(IMFTransform).GUID;
            _writer!.GetServiceForStream(_stream, ref service, ref transformId, out pointer);
            if (pointer == IntPtr.Zero) return asked;

            var transform = (IMFTransform)Marshal.GetObjectForIUnknown(pointer);
            try
            {
                transform.GetAttributes(out var attributes);
                try
                {
                    var hardware = TryString(attributes, MediaFoundationAttributes.MFT_ENUM_HARDWARE_URL_Attribute) is not null;
                    var name = TryString(attributes, MediaFoundationAttributes.MFT_FRIENDLY_NAME_Attribute);
                    return $"{(hardware ? "hardware" : "software")} encoder{(name is null ? "" : $" ({name})")}";
                }
                finally
                {
                    Marshal.ReleaseComObject(attributes);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(transform);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Deliberately not rethrown: the encoder's name is for the log only; a transform that cannot be asked is named
            // by the settings it was asked with.
            return asked;
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
        }
    }

    /// <summary>A string attribute; null when it is not there.</summary>
    private static string? TryString(IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetAllocatedString(key, out var value, out _);
            return value;
        }
        catch (COMException)
        {
            // Deliberately not rethrown: an attribute that is not there (MF_E_ATTRIBUTENOTFOUND) is the answer, none.
            return null;
        }
    }

    /// <summary>A sample of <paramref name="nv12"/> at <paramref name="at"/>, its pixels copied into a buffer of Media Foundation's own.</summary>
    private IMFSample MakeSample(byte[] nv12, TimeSpan at)
    {
        var buffer = MediaFoundationApi.CreateMemoryBuffer(_frameBytes);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            try
            {
                Marshal.Copy(nv12, 0, pointer, _frameBytes);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength(_frameBytes);
            var sample = MediaFoundationApi.CreateSample();
            sample.AddBuffer(buffer);
            sample.SetSampleTime(at.Ticks);
            return sample;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>Writes the waiting sample, lasting until <paramref name="at"/>, and makes <paramref name="sample"/> the waiting one.</summary>
    private void Add(IMFSample sample, TimeSpan at)
    {
        if (_finished) throw new InvalidOperationException("The piece is finished; nothing more is written to it.");
        if (_pending is not null) WritePending(Max(at - _pendingAt, TimeSpan.FromTicks(1)));
        _pending = sample;
        _pendingAt = at;
    }

    /// <summary>Writes the waiting sample with <paramref name="duration"/>, and lets go of it.</summary>
    private void WritePending(TimeSpan duration)
    {
        var pending = _pending!;
        _pending = null;
        try
        {
            pending.SetSampleDuration(duration.Ticks);
            _writer!.WriteSample(_stream, pending);
        }
        finally
        {
            Marshal.ReleaseComObject(pending);
        }
    }

    /// <summary>Lets go of the waiting sample, the sink writer and the byte stream.</summary>
    private void Release()
    {
        if (_pending is not null)
        {
            Marshal.ReleaseComObject(_pending);
            _pending = null;
        }

        if (_writer is not null)
        {
            Marshal.ReleaseComObject(_writer);
            _writer = null;
        }

        if (_byteStream is not null)
        {
            Marshal.ReleaseComObject(_byteStream);
            _byteStream = null;
        }
    }

    /// <summary>Two numbers in one attribute, the first in the high half, as Media Foundation packs a size or a rate.</summary>
    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    /// <summary>The longer of two lengths.</summary>
    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;
}

/// <summary>
/// The GUIDs and values for video that NAudio does not declare (mfapi.h, wmcodecdsp.h, codecapi.h), checked against the
/// camera probe's own table, which its self-check wrote pieces with.
/// </summary>
public static class VideoGuids
{
    /// <summary>MFVideoFormat_H264.</summary>
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");

    /// <summary>MFVideoFormat_NV12.</summary>
    public static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    /// <summary>MFTranscodeContainerType_FMPEG4: fragmented MP4.</summary>
    public static readonly Guid ContainerFragmentedMpeg4 = new("9ba876f1-419f-4b77-a1e0-35959d9d4004");

    /// <summary>MF_MT_INTERLACE_MODE.</summary>
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

    /// <summary>MFVideoInterlace_Progressive.</summary>
    public const int Progressive = 2;

    /// <summary>MF_MT_FRAME_SIZE.</summary>
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// <summary>MF_MT_FRAME_RATE.</summary>
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

    /// <summary>MF_MT_PIXEL_ASPECT_RATIO.</summary>
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

    /// <summary>MF_MT_MPEG2_PROFILE.</summary>
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    /// <summary>MF_MT_MPEG2_LEVEL.</summary>
    public static readonly Guid Mpeg2Level = new("96f66574-11c5-4015-8666-bff516436da7");

    /// <summary>MF_MT_DEFAULT_STRIDE.</summary>
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// <summary>CODECAPI_AVEncCommonRateControlMode.</summary>
    public static readonly Guid CodecRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    public const int ConstantBitrate = 0;

    /// <summary>CODECAPI_AVEncCommonMeanBitRate.</summary>
    public static readonly Guid CodecMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");

    /// <summary>CODECAPI_AVEncMPVGOPSize.</summary>
    public static readonly Guid CodecGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    /// <summary>CODECAPI_AVEncMPVDefaultBPictureCount.</summary>
    public static readonly Guid CodecBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
}

/// <summary>
/// A <see cref="FileStream"/> as the COM stream Media Foundation's byte stream wraps (MFCreateMFByteStreamOnStream), so that
/// ClassScribe makes the file itself, with the sharing it chooses. Reads, writes, seeks, sets the size, says its size, and
/// flushes on commit; what Media Foundation does not ask of a file stream is refused.
/// </summary>
/// <param name="file">The file, open for reading and writing.</param>
internal sealed class FileStreamAsIStream(FileStream file) : IStream
{
    /// <summary>STG_E_INVALIDFUNCTION: the call is not offered.</summary>
    private const int NotOffered = unchecked((int)0x80030001);

    /// <inheritdoc />
    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        var read = file.Read(pv, 0, cb);
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
    }

    /// <inheritdoc />
    public void Write(byte[] pv, int cb, IntPtr pcbWritten)
    {
        file.Write(pv, 0, cb);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb);
    }

    /// <inheritdoc />
    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var position = file.Seek(dlibMove, (SeekOrigin)dwOrigin);
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, position);
    }

    /// <inheritdoc />
    public void SetSize(long libNewSize) => file.SetLength(libNewSize);

    /// <inheritdoc />
    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new COMException("Copying is not offered.", NotOffered);

    /// <inheritdoc />
    public void Commit(int grfCommitFlags) => file.Flush();

    /// <inheritdoc />
    public void Revert() => throw new COMException("Reverting is not offered.", NotOffered);

    /// <inheritdoc />
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Locking is not offered.", NotOffered);

    /// <inheritdoc />
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Locking is not offered.", NotOffered);

    /// <inheritdoc />
    public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG { type = 2, cbSize = file.Length, grfMode = 2 };

    /// <inheritdoc />
    public void Clone(out IStream ppstm) => throw new COMException("Cloning is not offered.", NotOffered);
}
