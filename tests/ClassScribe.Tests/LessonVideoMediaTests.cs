using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Camera.Windows;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.MediaFoundation;

namespace ClassScribe.Tests;

/// <summary>
/// The real media work of a lesson's video (teach-video 6.9): pieces written by the real piece writer with Windows' software
/// encoder from generated frames, a .wav beside them, joined by Media Foundation into a plain MP4 (its index after its data,
/// where Windows writes it), read back and checked, in a temporary folder. It opens no device: category WindowsMedia, run with the suite.
/// </summary>
[TestFixture]
[Category("WindowsMedia")]
[SupportedOSPlatform("windows")]
public sealed class LessonVideoMediaTests
{
    private const string BaseName = "Tajwid_2026-10-22_14-00-00Z";
    private static readonly TimeSpan FrameTime = TimeSpan.FromTicks(333_333);
    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private readonly MediaFoundationLessonVideo _media = new();

    /// <summary>A temporary folder for the lesson.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = new RecordingFiles(_folder.Path, BaseName);
    }

    /// <summary>Deletes the folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>
    /// Writes a piece beginning at <paramref name="start"/> on the lesson's clock, <paramref name="seconds"/> long at 30 frames a
    /// second, each frame of its own brightness, at <paramref name="size"/> with the software encoder; returns its path.
    /// </summary>
    private string Piece(TimeSpan start, double seconds, CameraSize? size = null)
    {
        var settings = PieceSettings.For(size ?? CameraSize.P480, software: true);
        var path = _files.CameraPiece(start);
        using var writer = new MediaFoundationPieceWriter(path, settings);
        var frames = (int)Math.Round(seconds * 30);
        for (var frame = 1; frame < frames; frame++)
        {
            var picture = CameraPicture.Black(settings.Size.Width, settings.Size.Height);
            picture.AsSpan(0, settings.Size.Width * settings.Size.Height).Fill((byte)(40 + frame % 150));
            writer.Write(picture, FrameTime * frame);
        }

        writer.Finish(TimeSpan.FromSeconds(seconds));
        return path;
    }

    /// <summary>Writes the lesson's sound: <paramref name="seconds"/> at 48 kHz, quiet, with a click at <paramref name="clickAt"/> when given.</summary>
    private void Sound(double seconds, double? clickAt = null)
    {
        var samples = TestAudio.Tone(48_000, seconds, 220, 0.01);
        if (clickAt is { } click)
        {
            var at = (int)(click * 48_000);
            for (var i = at; i < at + 48 && i < samples.Length; i++) samples[i] = 30_000;
        }

        File.Delete(_files.Segment(1));
        TestAudio.WriteWav(_files.Segment(1), 48_000, samples);
    }

    /// <summary>The lesson's sound, mixed as the upload is, at 48 kHz.</summary>
    private MixedSound Mix() => new AudioPreparer(NullLogger<AudioPreparer>.Instance).OpenMix(new UploadTracks(OnDisk.FindSegments(_files), []), 48_000, CancellationToken.None);

    /// <summary>The plan of the lesson's pieces, described by the real reader, with the sound <paramref name="seconds"/> long.</summary>
    private LessonVideoPlan Plan(double seconds, string? older = null) =>
        LessonVideoPlan.Make([.. OnDisk.FindCameraPieces(_files).Select(piece => new LessonVideoSource(piece.Path, piece.Start, _media.Describe(piece.Path)))],
            older is null ? null : new LessonVideoSource(older, TimeSpan.Zero, _media.Describe(older)), TimeSpan.FromSeconds(seconds));

    /// <summary>Makes the video of <paramref name="plan"/> into the lesson's .partial, then checks it as the finish job does, and returns the check's answer.</summary>
    private string? MakeAndCheck(LessonVideoPlan plan)
    {
        using (var sound = Mix()) _media.Write(plan, sound, _files.LessonVideoPartial, _ => { }, CancellationToken.None);
        return _media.Check(_files.LessonVideoPartial, new LessonVideoCheck(plan.End, plan.FramesToCheck));
    }

    /// <summary>A piece is described by its size, its H.264 sequence header and its length.</summary>
    [Test]
    public void A_piece_is_described_by_its_size_header_and_length()
    {
        var path = Piece(TimeSpan.Zero, 1);

        var facts = _media.Describe(path);

        Assert.That((facts.Width, facts.Height), Is.EqualTo((848, 480)));
        Assert.That(facts.SequenceHeader, Is.Not.Empty, "the parameter sets the join compares");
        Assert.That(facts.Length, Is.EqualTo(TimeSpan.FromSeconds(1)).Within(TimeSpan.FromMilliseconds(50)).Or.EqualTo(MediaFoundationLessonVideo.ToItsEnd));
    }

    /// <summary>Two pieces of one encoder have the same header, so the join passes the picture through; a piece of another size does not.</summary>
    [Test]
    public void Pieces_of_one_encoder_join_and_one_of_another_size_does_not()
    {
        var first = _media.Describe(Piece(TimeSpan.Zero, 1));
        var second = _media.Describe(Piece(TimeSpan.FromSeconds(1), 1));
        var other = _media.Describe(Piece(TimeSpan.FromSeconds(2), 1, new CameraSize(640, 360)));

        Assert.That(first.JoinsWith(second), Is.True);
        Assert.That(first.JoinsWith(other), Is.False);
    }

    /// <summary>
    /// Two pieces and their sound become one plain MP4: picture and sound, as long as the sound, a frame decoded at the start,
    /// on each side of the join and at the end, passed through, with its index (moov) and its data (mdat), not fragmented.
    /// </summary>
    [Test]
    public void Two_pieces_and_the_sound_become_one_video_that_passes_its_check()
    {
        Piece(TimeSpan.Zero, 1);
        Piece(TimeSpan.FromSeconds(1), 1);
        Sound(2);
        var plan = Plan(2);

        Assert.That(plan.EncodeAgain, Is.False);
        Assert.That(MakeAndCheck(plan), Is.Null);

        var read = ReadBack(_files.LessonVideoPartial);
        Assert.That(read.Size, Is.EqualTo((848, 480)));
        Assert.That(read.VideoTimes[0], Is.EqualTo(TimeSpan.Zero));
        Assert.That(read.VideoTimes.Count, Is.EqualTo(60).Within(2), "both pieces' frames");
        Assert.That(read.VideoTimes.Any(time => (time - TimeSpan.FromSeconds(1)).Duration() < TimeSpan.FromMilliseconds(5)), Is.True, "the second piece's black frame at its place");
        Assert.That(read.VideoEnd, Is.EqualTo(TimeSpan.FromSeconds(2)).Within(TimeSpan.FromMilliseconds(40)));
        var boxes = TopLevelBoxes(_files.LessonVideoPartial);
        Assert.That(boxes[0], Is.EqualTo("ftyp"), "a plain MP4, not fragmented");
        Assert.That(boxes, Has.Member("moov").And.Member("mdat").And.No.Member("moof"));
    }

    /// <summary>
    /// A passed-through picture keeps only its IDR pictures as sync samples: a seek past an I picture of the software
    /// encoder's that is not one still decodes, from the IDR before it.
    /// </summary>
    [Test]
    public void Only_IDR_pictures_stay_sync_samples_so_a_seek_anywhere_decodes()
    {
        Piece(TimeSpan.Zero, 2);
        Sound(2);
        var plan = Plan(2);

        Assert.That(MakeAndCheck(plan), Is.Null);
        Assert.That(_media.Check(_files.LessonVideoPartial, new LessonVideoCheck(TimeSpan.FromSeconds(2), [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.9)])),
            Is.Null);
    }

    /// <summary>
    /// The real camera host's finish job with Media Foundation's work: two pieces and the sound become the lesson's video,
    /// checked and renamed, and the pieces are deleted; nothing is opened.
    /// </summary>
    [Test]
    public async Task The_finish_job_makes_a_real_lesson_video()
    {
        Piece(TimeSpan.Zero, 1);
        Piece(TimeSpan.FromSeconds(1), 1);
        Sound(2);
        await using var process = new FakeCameraProcess();
        var host = Task.Run(() => new CameraHost { Media = _media }.RunAsync(TestRuns.Id(12), process.ProcessStream, process.ProcessStream));
        var seen = new CameraLinesSeen(process.Channel);
        await CameraLines.SendJobAsync(process.Channel, new CameraJob(CameraProtocol.Version, CameraJobKind.Finish, Folder: _folder.Path, BaseName: BaseName), CancellationToken.None);

        var exit = await host.WaitAsync(TimeSpan.FromSeconds(60));
        await process.ProcessEnd.DisposeAsync();
        await seen.Reading.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(exit, Is.EqualTo(CameraProtocol.DoneExitCode), string.Join(" | ", seen.Of<CameraFailed>().Select(failed => failed.Why)));
        Assert.That(File.Exists(_files.LessonVideo), Is.True);
        Assert.That(File.Exists(_files.LessonVideoPartial), Is.False);
        Assert.That(OnDisk.FindCameraPieces(_files), Is.Empty);
        Assert.That(seen.Of<CameraMade>().Single().LengthSeconds, Is.EqualTo(2).Within(0.01));
        Assert.That(ClassFolders.LessonVideoStateOf(_files), Is.EqualTo(LessonVideoState.Made));
    }

    /// <summary>An H.264 sample's picture is told by its first slice's NAL unit, by start codes or by lengths.</summary>
    [Test]
    public void An_IDR_picture_is_told_by_its_first_slice()
    {
        byte[] idr = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 0x4D, 0, 0, 1, 0x68, 0xEE, 0, 0, 1, 0x65, 0x88];
        byte[] other = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 0x4D, 0, 0, 1, 0x68, 0xEE, 0, 0, 1, 0x41, 0x9A];
        byte[] byLength = [0, 0, 0, 2, 0x09, 0xF0, 0, 0, 0, 2, 0x65, 0x88];

        Assert.That(H264Pictures.HoldsIdr(idr), Is.True);
        Assert.That(H264Pictures.HoldsIdr(other), Is.False);
        Assert.That(H264Pictures.HoldsIdr(byLength), Is.True);
        Assert.That(H264Pictures.HoldsIdr([]), Is.False);
    }

    /// <summary>A piece of another size has the whole picture encoded again, at the newest piece's size, and the video still passes its check.</summary>
    [Test]
    public void A_piece_of_another_size_is_encoded_again_and_passes()
    {
        Piece(TimeSpan.Zero, 1, new CameraSize(640, 360));
        Piece(TimeSpan.FromSeconds(1), 1);
        Sound(2);
        var plan = Plan(2);

        Assert.That(plan.EncodeAgain, Is.True);
        Assert.That(MakeAndCheck(plan), Is.Null);
        Assert.That(ReadBack(_files.LessonVideoPartial).Size, Is.EqualTo((848, 480)));
    }

    /// <summary>A continued lesson's older video gives the picture before its first piece; the joined video passes its check.</summary>
    [Test]
    public void An_older_video_before_the_first_piece_joins_and_passes()
    {
        Piece(TimeSpan.Zero, 1);
        Sound(1);
        Assert.That(MakeAndCheck(Plan(1)), Is.Null);
        File.Move(_files.LessonVideoPartial, _files.LessonVideo);
        File.Delete(_files.CameraPiece(TimeSpan.Zero));

        Piece(TimeSpan.FromSeconds(1), 1);
        Sound(2);
        var plan = Plan(2, older: _files.LessonVideo);

        Assert.That(plan.Slices.Select(slice => slice.Path), Is.EqualTo(new[] { _files.LessonVideo, _files.CameraPiece(TimeSpan.FromSeconds(1)) }));
        Assert.That(plan.EncodeAgain, Is.False, "the same encoder made the older video's picture");
        Assert.That(MakeAndCheck(plan), Is.Null);
    }

    /// <summary>The check fails a video whose length is not its sound's, naming both.</summary>
    [Test]
    public void The_check_fails_a_video_of_another_length()
    {
        Piece(TimeSpan.Zero, 1);
        Sound(1);
        var plan = Plan(1);
        using (var sound = Mix()) _media.Write(plan, sound, _files.LessonVideoPartial, _ => { }, CancellationToken.None);

        var answer = _media.Check(_files.LessonVideoPartial, new LessonVideoCheck(TimeSpan.FromSeconds(3), plan.FramesToCheck));

        Assert.That(answer, Does.StartWith("it is 1.0").And.EndWith("seconds long, not 3.000 as its sound is"));
    }

    /// <summary>The check fails a file Windows cannot open as a video.</summary>
    [Test]
    public void The_check_fails_a_file_that_is_no_video()
    {
        File.WriteAllText(_files.LessonVideoPartial, "not a video");

        Assert.That(_media.Check(_files.LessonVideoPartial, new LessonVideoCheck(TimeSpan.FromSeconds(1), [TimeSpan.Zero])), Does.StartWith("Windows cannot open it"));
    }

    /// <summary>
    /// The sound in the video meets the picture where the .wav has it: a click at 1.5 seconds decodes within a frame's time of
    /// 1.5 seconds, the AAC encoder's priming included (teach-video 6.9).
    /// </summary>
    [Test]
    public void The_sound_meets_the_picture_where_the_wav_has_it()
    {
        Piece(TimeSpan.Zero, 3);
        Sound(3, clickAt: 1.5);

        Assert.That(MakeAndCheck(Plan(3)), Is.Null);

        Assert.That(ClickIn(_files.LessonVideoPartial), Is.EqualTo(TimeSpan.FromSeconds(1.5)).Within(TimeSpan.FromMilliseconds(33)));
    }

    /// <summary>Writing stops at a cancel, with an <see cref="OperationCanceledException"/>, and leaves the file for the caller to delete.</summary>
    [Test]
    public void A_cancel_stops_the_writing()
    {
        Piece(TimeSpan.Zero, 1);
        Sound(1);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        using (var sound = Mix())
            Assert.Throws<OperationCanceledException>(() => _media.Write(Plan(1), sound, _files.LessonVideoPartial, _ => { }, cancel.Token));

        Assert.That(File.Exists(_files.LessonVideoPartial), Is.True);
    }

    /// <summary>What a video holds as the source reader reads it back: its picture's size, each picture sample's time, where the last ends.</summary>
    private sealed record ReadVideo((int Width, int Height) Size, IReadOnlyList<TimeSpan> VideoTimes, TimeSpan VideoEnd);

    /// <summary>Reads <paramref name="path"/>'s picture back, sample by sample, without decoding it.</summary>
    private static ReadVideo ReadBack(string path)
    {
        MediaFoundationApi.Startup();
        MediaFoundationInterop.MFCreateSourceReaderFromURL(path, null, out var reader);
        try
        {
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);
            reader.GetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var type);
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

            return new ReadVideo(((int)(packed >> 32), (int)(packed & 0xFFFFFFFF)), times, end);
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    /// <summary>Where the loud click is in <paramref name="path"/>'s sound, decoded to 16-bit PCM: the first sample above half of full scale.</summary>
    private static TimeSpan ClickIn(string path)
    {
        MediaFoundationApi.Startup();
        MediaFoundationInterop.MFCreateSourceReaderFromURL(path, null, out var reader);
        try
        {
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
            var pcm = MediaFoundationApi.CreateMediaType();
            pcm.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Audio);
            pcm.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, AudioSubtypes.MFAudioFormat_PCM);
            pcm.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            reader.SetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, pcm);
            Marshal.ReleaseComObject(pcm);
            reader.GetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var type);
            type.GetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_SAMPLES_PER_SECOND, out var rate);
            type.GetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_NUM_CHANNELS, out var channels);
            Marshal.ReleaseComObject(type);

            while (true)
            {
                reader.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, out _, out var flags, out var time, out var sample);
                if (sample is not null)
                {
                    try
                    {
                        sample.ConvertToContiguousBuffer(out var buffer);
                        buffer.Lock(out var pointer, out _, out var length);
                        var bytes = new byte[length];
                        Marshal.Copy(pointer, bytes, 0, length);
                        buffer.Unlock();
                        Marshal.ReleaseComObject(buffer);
                        for (var i = 0; i + 1 < length; i += 2 * channels)
                        {
                            if (BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2)) > 16_000)
                                return TimeSpan.FromTicks((long)time) + TimeSpan.FromSeconds(i / 2.0 / channels / rate);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }

                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return TimeSpan.MinValue;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    /// <summary>The names of the top-level boxes of the MP4 at <paramref name="path"/>, in their order.</summary>
    private static List<string> TopLevelBoxes(string path)
    {
        var names = new List<string>();
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        while (file.Position + 8 <= file.Length)
        {
            var start = file.Position;
            file.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            names.Add(System.Text.Encoding.ASCII.GetString(header[4..8]));
            if (size == 1)
            {
                file.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            }

            if (size < 8) break;
            file.Position = start + size;
        }

        return names;
    }
}
