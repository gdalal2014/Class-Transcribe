using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Camera;
using ClassScribe.Camera.Windows;
using ClassScribe.Storage;
using NAudio.MediaFoundation;

namespace ClassScribe.Tests;

/// <summary>
/// The real piece writer (teach-video 6.1 and 6.5): Media Foundation's sink writer with Windows' H.264 encoder, writing
/// generated NV12 frames into a fragmented MP4 in a temporary folder, read back with Media Foundation's source reader. It
/// opens no device: category WindowsMedia, run with the suite. The software encoder is used, so that the result does not
/// depend on this laptop's graphics.
/// </summary>
[TestFixture]
[Category("WindowsMedia")]
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationPieceWriterTests
{
    private const string BaseName = "Tajwid_2026-10-22_14-00-00Z";
    private TempFolder _folder = null!;

    /// <summary>A temporary folder for the pieces.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The settings of a small piece: 480p with the software encoder.</summary>
    private static PieceSettings Small => PieceSettings.For(CameraSize.P480, software: true);

    /// <summary>A frame of the small size whose brightness is <paramref name="value"/>.</summary>
    private static byte[] Frame(byte value)
    {
        var frame = CameraPicture.Black(Small.Size.Width, Small.Size.Height);
        frame.AsSpan(0, Small.Size.Width * Small.Size.Height).Fill(value);
        return frame;
    }

    /// <summary>
    /// A piece holds the black frame at 0 and every frame written, at its time, the last lasting to the end given; it reads
    /// back as H.264 at the piece's size.
    /// </summary>
    [Test]
    public void A_piece_reads_back_with_every_frame_at_its_time()
    {
        var path = new RecordingFiles(_folder.Path, BaseName).CameraPiece(TimeSpan.Zero);
        var times = Enumerable.Range(0, 60).Select(frame => TimeSpan.FromSeconds(0.1) + TimeSpan.FromTicks(333_333 * frame)).ToList();

        using (var writer = new MediaFoundationPieceWriter(path, Small))
        {
            Assert.That(writer.Encoder, Does.StartWith("software encoder"));
            foreach (var (time, index) in times.Select((time, index) => (time, index))) writer.Write(Frame((byte)(40 + index * 3)), time);
            writer.Finish(TimeSpan.FromSeconds(2.2));
        }

        var read = ReadBack(path);
        var expected = new[] { TimeSpan.Zero }.Concat(times).ToList();
        Assert.That(read.Times, Has.Count.EqualTo(expected.Count));
        Assert.That(read.Times.Zip(expected, (got, wanted) => (got - wanted).Duration()).Max(), Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(1)),
            "each frame at its time, to the container's own rounding");
        Assert.That(read.End, Is.EqualTo(TimeSpan.FromSeconds(2.2)).Within(TimeSpan.FromMilliseconds(2)));
        Assert.That(read.Size, Is.EqualTo((848, 480)));
        Assert.That(read.H264, Is.True);
    }

    /// <summary>While its piece is being written the lesson is being written, as a segment's recording is; once finished it is not.</summary>
    [Test]
    public void A_piece_being_written_holds_its_recording()
    {
        var files = new RecordingFiles(_folder.Path, BaseName);
        var writer = new MediaFoundationPieceWriter(files.CameraPiece(TimeSpan.Zero), Small);
        try
        {
            writer.Write(Frame(80), TimeSpan.FromSeconds(0.1));
            Assert.That(ClassFolders.IsBeingWritten(files), Is.True);
            writer.Finish(TimeSpan.FromSeconds(0.5));
        }
        finally
        {
            writer.Dispose();
        }

        Assert.That(ClassFolders.IsBeingWritten(files), Is.False);
    }

    /// <summary>A piece already there is never written over: the writer refuses before anything is encoded, and the file stays as it was.</summary>
    [Test]
    public void A_piece_already_there_is_refused()
    {
        var path = new RecordingFiles(_folder.Path, BaseName).CameraPiece(TimeSpan.Zero);
        File.WriteAllText(path, "an earlier piece");

        Assert.Throws<IOException>(() => new MediaFoundationPieceWriter(path, Small).Dispose());
        Assert.That(File.ReadAllText(path), Is.EqualTo("an earlier piece"));
    }

    /// <summary>
    /// A piece left unfinished, as when the camera process is killed in the middle of a lesson, still reads back up to its
    /// last whole fragment: the fragmented container's promise, a second or so lost at most.
    /// </summary>
    [Test]
    public void A_piece_left_unfinished_reads_back_to_its_last_fragment()
    {
        var path = new RecordingFiles(_folder.Path, BaseName).CameraPiece(TimeSpan.Zero);
        var writer = new MediaFoundationPieceWriter(path, Small);
        for (var frame = 1; frame <= 150; frame++) writer.Write(Frame((byte)(30 + frame)), TimeSpan.FromTicks(333_333 * frame));
        writer.Dispose();

        var read = ReadBack(path);
        Assert.That(read.Times.Count, Is.GreaterThanOrEqualTo(90), "at most about two seconds of five lost");
        Assert.That(read.Times[0], Is.EqualTo(TimeSpan.Zero));
    }

    /// <summary>What a piece holds, as Media Foundation's source reader reads it back: each sample's time, where the last ends, the picture's size and whether it is H.264.</summary>
    private sealed record ReadPiece(IReadOnlyList<TimeSpan> Times, TimeSpan End, (int Width, int Height) Size, bool H264);

    /// <summary>Reads <paramref name="path"/> back, sample by sample, without decoding it.</summary>
    private static ReadPiece ReadBack(string path)
    {
        MediaFoundationApi.Startup();
        MediaFoundationInterop.MFCreateSourceReaderFromURL(path, null, out var reader);
        try
        {
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);
            reader.GetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var type);
            type.GetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, out var subtype);
            type.GetUINT64(VideoGuids.FrameSize, out var packed);
            Marshal.ReleaseComObject(type);

            var times = new List<TimeSpan>();
            var end = TimeSpan.Zero;
            while (true)
            {
                reader.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out var time, out var sample);
                if (sample is not null)
                {
                    sample.GetSampleDuration(out var duration);
                    times.Add(TimeSpan.FromTicks((long)time));
                    end = TimeSpan.FromTicks((long)time + duration);
                    Marshal.ReleaseComObject(sample);
                }

                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
            }

            return new ReadPiece(times, end, ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF)), subtype == VideoGuids.H264);
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }
}
