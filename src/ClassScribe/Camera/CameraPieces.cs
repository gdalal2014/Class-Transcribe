using System.Collections.Concurrent;
using System.Globalization;
using ClassScribe.Audio.Timeline;

namespace ClassScribe.Camera;

/// <summary>
/// How a lesson's piece is encoded (teach-video 6.5, amendment 39): the run's picture size, H.264 Main profile, the level
/// from the size's macroblocks at 30 frames a second, no B-frames, a keyframe every 30 frames (so each fragment is about a
/// second, and a crash loses about that), constant bitrate by the band of the height (up to 480 lines 2.5, up to 720 lines
/// 5, up to 1080 lines 8 Mbit/s, YouTube's figures) scaled by the size's pixels against that band's 16:9 size. Fixed for
/// the hardware and the software encoder alike, so that the pieces of a lesson are as alike as an encoder allows.
/// </summary>
/// <param name="Size">The picture's size.</param>
/// <param name="BitsPerSecond">The constant bitrate.</param>
/// <param name="Level">The H.264 level times ten, as Windows' encoders take it: 30, 31, 32 or 40.</param>
/// <param name="Software">True to use Windows' software encoder; false to ask for the hardware encoder.</param>
public sealed record PieceSettings(CameraSize Size, int BitsPerSecond, int Level, bool Software)
{
    /// <summary>The frame rate the piece is declared at, and the most frames a second it keeps (see <see cref="FramePlacer.ShortestGap"/>).</summary>
    public const int FramesPerSecond = 30;

    /// <summary>H.264's Main profile, as Windows' encoders take it (eAVEncH264VProfile_Main).</summary>
    public const int MainProfile = 77;

    /// <summary>A keyframe every this many frames: a second at 30 frames a second.</summary>
    public const int KeyframeEvery = 30;

    /// <summary>The settings for <paramref name="size"/>, with the software encoder when <paramref name="software"/>.</summary>
    public static PieceSettings For(CameraSize size, bool software = false) => new(size, BitrateFor(size), LevelFor(size.Width, size.Height), software);

    /// <summary>
    /// The constant bitrate for <paramref name="size"/>: the band of its height (480, 720, 1080 lines at 2.5, 5, 8 Mbit/s),
    /// scaled by its pixels against the band's 16:9 size (848x480, 1280x720, 1920x1080), so that 720p takes 5 Mbit/s and
    /// 1024x576 3.2.
    /// </summary>
    public static int BitrateFor(CameraSize size)
    {
        var (band, rate) = size.Height <= 480 ? (CameraSize.P480, 2_500_000) : size.Height <= 720 ? (CameraSize.P720, 5_000_000) : (CameraSize.P1080, 8_000_000);
        return (int)Math.Round((double)rate * size.Width * size.Height / (band.Width * band.Height), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The smallest H.264 level, times ten, that holds <paramref name="width"/> by <paramref name="height"/> at
    /// <paramref name="framesPerSecond"/>: by the macroblocks a frame and a second (H.264's table A-1). Level 3.0 holds
    /// 720x480 but not 848x480 (47,700 macroblocks a second against its 40,500), which the probe's self-check found when
    /// Windows refused 3.0 at 480p, so 480p takes 3.1, as 720p does; 1080p takes 4.0.
    /// </summary>
    public static int LevelFor(int width, int height, int framesPerSecond = FramesPerSecond)
    {
        var blocks = (width + 15) / 16 * ((height + 15) / 16);
        var rate = blocks * framesPerSecond;
        return rate <= 40_500 && blocks <= 1_620 ? 30 : rate <= 108_000 && blocks <= 3_600 ? 31 : rate <= 216_000 && blocks <= 5_120 ? 32 : 40;
    }

    /// <summary>The settings in words: <c>1280x720 at 30 frames a second, 5 Mbit/s, H.264 level 3.1</c>.</summary>
    public string Words => string.Create(CultureInfo.InvariantCulture,
        $"{Size.Width}x{Size.Height} at {FramesPerSecond} frames a second, {BitsPerSecond / 1e6:0.#} Mbit/s, H.264 level {Level / 10.0:0.0}");

    /// <summary>How many bytes an NV12 frame of the size takes: the brightness plane, then the colour plane at half its height.</summary>
    public int FrameBytes => Size.Width * Size.Height * 3 / 2;
}

/// <summary>
/// A lesson's piece being written (teach-video 6.6): a fragmented, video-only MP4 of H.264, whose first sample is a black
/// frame at time 0, written when it is made, so that the piece's time 0 is the position its name holds whatever comes next.
/// Each frame lasts until the next; the last lasts to the end given at <see cref="Finish"/>. Called from one thread at a
/// time.
/// </summary>
public interface IPieceWriter : IDisposable
{
    /// <summary>The piece's file.</summary>
    string Path { get; }

    /// <summary>The encoder in words, such as "hardware encoder (Intel® Quick Sync Video H.264 Encoder MFT)".</summary>
    string Encoder { get; }

    /// <summary>Writes a frame of NV12 pixels (the brightness plane, then the colour plane, each row as wide as the picture) at <paramref name="at"/> from the piece's start, later than the last.</summary>
    void Write(byte[] nv12, TimeSpan at);

    /// <summary>Writes a black frame at <paramref name="at"/>, later than the last: the picture while the camera is away.</summary>
    void WriteBlack(TimeSpan at);

    /// <summary>Lets the last frame last to <paramref name="end"/> from the piece's start and finishes the file.</summary>
    void Finish(TimeSpan end);
}

/// <summary>Makes a lesson's piece writer (see <see cref="IPieceWriter"/>).</summary>
public interface IPieceWriterFactory
{
    /// <summary>
    /// A new piece at <paramref name="path"/>, which must not be there yet, encoded as <paramref name="settings"/> say, its
    /// black frame at 0 written. Throws when the file cannot be made or the encoder refuses its settings.
    /// </summary>
    IPieceWriter Create(string path, PieceSettings settings);
}

/// <summary>
/// A frame from the camera, handed to a record job while Windows holds it (teach-video 6.6): its stamp, its size and a way
/// to copy its pixels out before it is released.
/// </summary>
public interface ICameraFrame
{
    /// <summary>The performance counter's time the frame was taken, in units of 100 nanoseconds (SystemRelativeTime); null when Windows gave none.</summary>
    long? Stamp100ns { get; }

    /// <summary>The frame's width.</summary>
    int Width { get; }

    /// <summary>The frame's height.</summary>
    int Height { get; }

    /// <summary>
    /// Copies the frame's NV12 pixels into <paramref name="target"/>: the brightness plane, then the colour plane, each row
    /// as wide as the picture. False when the frame holds no NV12 pixels of its size that can be read.
    /// </summary>
    bool CopyNv12To(byte[] target);
}

/// <summary>Looks at a picture without keeping it.</summary>
public static class CameraPicture
{
    /// <summary>The mean brightness below which a picture counts as black: 16 of 255, NV12's black.</summary>
    public const double DarkBelow = 16;

    /// <summary>
    /// The mean brightness of an NV12 picture, from 0 to 255: the mean of a 16 by 9 grid of its brightness plane, at the
    /// middle of each cell (teach-video 6.12), a few hundred reads a frame.
    /// </summary>
    public static double MeanBrightness(byte[] nv12, int width, int height)
    {
        long sum = 0;
        for (var row = 0; row < 9; row++)
        {
            var y = (row * 2 + 1) * height / 18;
            for (var column = 0; column < 16; column++) sum += nv12[y * width + (column * 2 + 1) * width / 32];
        }

        return sum / 144.0;
    }

    /// <summary>A black NV12 picture of <paramref name="width"/> by <paramref name="height"/>: brightness 16, colour 128.</summary>
    public static byte[] Black(int width, int height)
    {
        var frame = new byte[width * height * 3 / 2];
        frame.AsSpan(0, width * height).Fill(16);
        frame.AsSpan(width * height).Fill(128);
        return frame;
    }
}

/// <summary>What became of a frame placed on a piece (see <see cref="FramePlacer"/>).</summary>
public enum FrameFate
{
    /// <summary>Written at its place.</summary>
    Written,

    /// <summary>Taken before the clock's origin, before any sound was recorded: dropped, the black frame shows.</summary>
    BeforeOrigin,

    /// <summary>Taken inside a pause or a cut: dropped, as sound stamped there is.</summary>
    Excluded,

    /// <summary>Placed before the piece's start: dropped.</summary>
    BeforePiece,

    /// <summary>Placed at or before the last frame written: dropped, as a packet of sound behind its track is trimmed.</summary>
    NotAfterLast,

    /// <summary>Placed less than <see cref="FramePlacer.ShortestGap"/> after the last frame written: dropped, so that at most about 30 frames a second are kept.</summary>
    TooSoon,

    /// <summary>Placed after the end of the piece: dropped.</summary>
    AfterEnd
}

/// <summary>Where a frame went: its fate, and when it was written, its time from the piece's start.</summary>
/// <param name="Fate">What became of it.</param>
/// <param name="At">Its time from the piece's start, for a frame written; zero otherwise.</param>
public readonly record struct FramePlacement(FrameFate Fate, TimeSpan At = default)
{
    /// <summary>True for a frame written.</summary>
    public bool Written => Fate == FrameFate.Written;
}

/// <summary>
/// Places the camera's frames on a piece (teach-video 6.6), pure: a frame taken before the clock's origin, or inside an
/// exclusion of the clock (a pause, a cut), is dropped; otherwise its position is the clock's position of its stamp plus
/// the camera's offset; a frame placed before the piece's start, at or before the last frame written, less than
/// <see cref="ShortestGap"/> after it, or after the piece's end, is dropped, as <see cref="TrackPlacer"/> trims sound; the
/// rest are written at their position less the piece's start. The piece's black frame at its start counts as the first
/// frame written. One placer places one piece's frames, from one thread.
/// </summary>
/// <param name="pieceStart">Where on the clock the piece's time 0 is.</param>
/// <param name="offset">How far the camera's picture is moved against the sound (Recording:CameraOffsets): negative is earlier.</param>
public sealed class FramePlacer(TimeSpan pieceStart, TimeSpan offset)
{
    /// <summary>
    /// The shortest gap kept between two frames written: a camera that runs at 60 frames a second has every other frame
    /// dropped, so that the piece keeps about the 30 a second its level was set for, while a camera at 30 a second whose
    /// stamps wander a little keeps them all.
    /// </summary>
    public static readonly TimeSpan ShortestGap = TimeSpan.FromMilliseconds(25);

    /// <summary>The position of the last frame written, on the clock: the piece's black frame at its start before any.</summary>
    private TimeSpan _last = pieceStart;

    /// <summary>Where on the clock the piece's time 0 is.</summary>
    public TimeSpan PieceStart { get; } = pieceStart;

    /// <summary>The position on the clock of the last frame written; the piece's start before any.</summary>
    public TimeSpan Last => _last;

    /// <summary>How many frames were written.</summary>
    public long Written { get; private set; }

    /// <summary>How many frames were dropped.</summary>
    public long Dropped { get; private set; }

    /// <summary>
    /// Where the frame taken at <paramref name="stamp100ns"/> goes, by <paramref name="clock"/>: written at its place, or
    /// dropped, and why. A frame placed after <paramref name="end"/> (a position on the clock) is dropped.
    /// </summary>
    public FramePlacement Place(ClockState clock, long stamp100ns, TimeSpan? end = null)
    {
        var placement = Decide(clock, stamp100ns, end);
        if (placement.Written) Written++;
        else Dropped++;
        return placement;
    }

    /// <summary>
    /// Where a black frame at <paramref name="position"/> on the clock goes, as when the camera stopped sending its picture
    /// (teach-video 6.12): written when it comes after the last frame written, which it then is; otherwise nothing. It is no
    /// frame of the camera's, so it counts as neither written nor dropped.
    /// </summary>
    public FramePlacement PlaceBlack(TimeSpan position)
    {
        if (position <= _last) return new FramePlacement(FrameFate.NotAfterLast);

        _last = position;
        return new FramePlacement(FrameFate.Written, position - PieceStart);
    }

    /// <summary>The fate of a frame, and the last frame written moved on when it is written.</summary>
    private FramePlacement Decide(ClockState clock, long stamp, TimeSpan? end)
    {
        if (clock.Origin is not { } origin || stamp < origin) return new FramePlacement(FrameFate.BeforeOrigin);
        if (clock.IsExcluded(stamp)) return new FramePlacement(FrameFate.Excluded);

        var position = clock.Position(stamp) + offset;
        if (position < PieceStart) return new FramePlacement(FrameFate.BeforePiece);
        if (end is { } last && position > last) return new FramePlacement(FrameFate.AfterEnd);
        if (position <= _last) return new FramePlacement(FrameFate.NotAfterLast);
        if (Written > 0 && position - _last < ShortestGap) return new FramePlacement(FrameFate.TooSoon);

        _last = position;
        return new FramePlacement(FrameFate.Written, position - PieceStart);
    }
}

/// <summary>A frame waiting in the queue: its pixels in a pooled buffer, its stamp (null when Windows gave none), when it arrived, and its brightness.</summary>
/// <param name="Pixels">The NV12 pixels, in a buffer of the queue's pool.</param>
/// <param name="Stamp">When the frame was taken, in units of 100 nanoseconds; null when Windows gave no stamp.</param>
/// <param name="Arrival">When it arrived, in the same units.</param>
/// <param name="Brightness">Its mean brightness (see <see cref="CameraPicture.MeanBrightness"/>).</param>
public sealed record QueuedFrame(byte[] Pixels, long? Stamp, long Arrival, double Brightness);

/// <summary>
/// The frames of a record job waiting to be placed (teach-video 6.6): each frame's pixels are copied, on the thread that
/// handed it over, into one of a fixed number of pooled buffers, and the frame waits about half a second, so that the clock
/// line of a pause (which follows the pause by up to a quarter of a second) arrives before the frames it leaves out. A frame
/// that finds no free buffer is dropped and counted; more than <see cref="RingLimit"/> waiting frames let the oldest go
/// early. Safe for one thread handing frames over and another taking them.
/// </summary>
/// <param name="frameBytes">The bytes of one NV12 frame.</param>
/// <param name="buffers">How many buffers the pool holds.</param>
public sealed class FrameQueue(int frameBytes, int buffers)
{
    /// <summary>How long a frame waits before it is placed.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(0.5);

    /// <summary>The most frames that wait at once: half a second at 30 frames a second.</summary>
    public const int RingLimit = 15;

    /// <summary>The buffers a record job's queue holds: the ring, the frames that arrive while the job writes, and a few more.</summary>
    public const int PoolSize = 24;

    /// <summary>The frames waiting, oldest first.</summary>
    private readonly ConcurrentQueue<QueuedFrame> _waiting = new();

    /// <summary>The buffers given back, free to take.</summary>
    private readonly ConcurrentBag<byte[]> _free = [];

    /// <summary>How many buffers have been asked to be made, those refused beyond the pool's size included.</summary>
    private int _made;

    /// <summary>How many frames were dropped here.</summary>
    private long _dropped;

    /// <summary>How many frames found no free buffer, or held no pixels that could be read, and were dropped.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>How many frames wait now.</summary>
    public int Count => _waiting.Count;

    /// <summary>
    /// Copies <paramref name="frame"/> into a free buffer and queues it as arrived at <paramref name="arrival"/>; returns its
    /// brightness, or null when it was dropped: no buffer free, or no pixels of the queue's size that could be read.
    /// </summary>
    public double? Offer(ICameraFrame frame, long arrival)
    {
        if (frame.Width * frame.Height * 3 / 2 != frameBytes || Rent() is not { } buffer)
        {
            Interlocked.Increment(ref _dropped);
            return null;
        }

        if (!frame.CopyNv12To(buffer))
        {
            Return(buffer);
            Interlocked.Increment(ref _dropped);
            return null;
        }

        var brightness = CameraPicture.MeanBrightness(buffer, frame.Width, frame.Height);
        _waiting.Enqueue(new QueuedFrame(buffer, frame.Stamp100ns, arrival, brightness));
        return brightness;
    }

    /// <summary>
    /// The oldest waiting frame, when it is due at <paramref name="now"/> (it has waited <see cref="Hold"/>, or more than
    /// <see cref="RingLimit"/> wait) or <paramref name="all"/> are asked for; false when none is. Its buffer goes back with
    /// <see cref="Return"/> once it is written.
    /// </summary>
    public bool TryTakeDue(long now, bool all, out QueuedFrame frame)
    {
        frame = null!;
        if (!_waiting.TryPeek(out var oldest)) return false;
        if (!all && _waiting.Count <= RingLimit && now - oldest.Arrival < Hold.Ticks) return false;
        if (!_waiting.TryDequeue(out var taken)) return false;

        frame = taken;
        return true;
    }

    /// <summary>Gives <paramref name="buffer"/> back to the pool.</summary>
    public void Return(byte[] buffer) => _free.Add(buffer);

    /// <summary>A free buffer: one given back, or a new one while the pool has made fewer than its size; null when none is free.</summary>
    private byte[]? Rent()
    {
        if (_free.TryTake(out var buffer)) return buffer;
        return Interlocked.Increment(ref _made) <= buffers ? new byte[frameBytes] : null;
    }
}
