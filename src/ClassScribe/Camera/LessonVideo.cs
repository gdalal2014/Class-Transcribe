namespace ClassScribe.Camera;

/// <summary>What a source of a lesson's picture holds, as the media reader finds it: a camera's piece, or an older finished video.</summary>
/// <param name="Width">The picture's width.</param>
/// <param name="Height">The picture's height.</param>
/// <param name="SequenceHeader">Its H.264 sequence header (MF_MT_MPEG_SEQUENCE_HEADER): the encoder's parameter sets, which one MP4 track can hold only once.</param>
/// <param name="Length">How long its picture lasts, from its time 0 to the end of its last frame.</param>
public sealed record VideoSourceFacts(int Width, int Height, byte[] SequenceHeader, TimeSpan Length)
{
    /// <summary>True when <paramref name="other"/> can be passed through beside this one: the same size and the very same sequence header.</summary>
    public bool JoinsWith(VideoSourceFacts other) => Width == other.Width && Height == other.Height && SequenceHeader.AsSpan().SequenceEqual(other.SequenceHeader);

    /// <summary>The facts compared by value, the header by its bytes, so that two readings of one file are equal.</summary>
    public bool Equals(VideoSourceFacts? other) => other is not null && JoinsWith(other) && Length == other.Length;

    /// <summary>A hash of the size and the length, in step with <see cref="Equals(VideoSourceFacts?)"/>.</summary>
    public override int GetHashCode() => HashCode.Combine(Width, Height, Length, SequenceHeader.Length);
}

/// <summary>A source of a lesson's picture: its file, where its time 0 is on the lesson's clock, and what it holds.</summary>
/// <param name="Path">The file: a camera's piece, or an older finished video.</param>
/// <param name="Start">Where its time 0 is on the lesson's clock: a piece's from its name, an older video's at 0.</param>
/// <param name="Facts">What it holds.</param>
public sealed record LessonVideoSource(string Path, TimeSpan Start, VideoSourceFacts Facts);

/// <summary>A slice of a source's picture in the lesson's video: its first <paramref name="Length"/>, from its time 0, placed at <paramref name="At"/>.</summary>
/// <param name="Path">The source's file.</param>
/// <param name="At">Where the slice begins in the lesson's video, which is where its source's time 0 is on the lesson's clock.</param>
/// <param name="Length">How much of the source, from its time 0, goes in: up to where the next slice begins, and not past the sound's end.</param>
public sealed record PlannedSlice(string Path, TimeSpan At, TimeSpan Length);

/// <summary>
/// The plan of a lesson's video (teach-video 6.9), pure: its picture from the camera's pieces in position order, each placed
/// at its start, a piece cut where the next one begins and at the sound's end; before the first piece, when it begins later
/// than the lesson's start (a continued lesson whose first pieces were deleted), an older finished video's picture; a gap
/// between slices holds the last frame, since the writer lets every frame last until the next; and the picture lasts to the
/// end of the sound. One MP4 track holds one H.264 sequence header, so every source's header and size are compared: all
/// equal, the picture is passed through as it is; any different (the software encoder took over after the hardware one
/// failed, or a continued lesson was recorded with the other encoder or at another size), the whole picture is decoded and
/// encoded once more, at the newest piece's size, which gives it one header. Encoding only the odd piece would not do:
/// another encoder makes another header even with the same settings.
/// </summary>
/// <param name="Slices">The slices, in the order of the video.</param>
/// <param name="End">Where the video ends: the sound's end.</param>
/// <param name="EncodeAgain">True when the picture is decoded and encoded once more rather than passed through.</param>
/// <param name="WhyEncodeAgain">Why, in words, naming the source that differs; null when it is passed through.</param>
/// <param name="Size">The picture's size in the video: every source's when passed through, the newest piece's when encoded again.</param>
public sealed record LessonVideoPlan(IReadOnlyList<PlannedSlice> Slices, TimeSpan End, bool EncodeAgain, string? WhyEncodeAgain, (int Width, int Height) Size)
{
    /// <summary>How far before a join, or before the end, the check decodes the frame of the slice that ends there: a frame's time at 30 a second.</summary>
    public static readonly TimeSpan BeforeAJoin = TimeSpan.FromMilliseconds(33);

    /// <summary>Where one source gives way to the next in the video: each slice's beginning but the first.</summary>
    public IReadOnlyList<TimeSpan> Joins => [.. Slices.Skip(1).Select(slice => slice.At)];

    /// <summary>Where the check decodes a frame: at the start, on each side of every join, and just before the end.</summary>
    public IReadOnlyList<TimeSpan> FramesToCheck =>
        [TimeSpan.Zero, .. Joins.SelectMany(join => new[] { join - BeforeAJoin, join }), End - BeforeAJoin];

    /// <summary>
    /// The plan for <paramref name="pieces"/>, in any order, with <paramref name="older"/>, an older finished video, for
    /// positions before the first piece, and the sound <paramref name="soundLength"/> long. Throws an
    /// <see cref="InvalidDataException"/>, in words, when there is no sound, or no piece that adds anything to it.
    /// </summary>
    public static LessonVideoPlan Make(IReadOnlyList<LessonVideoSource> pieces, LessonVideoSource? older, TimeSpan soundLength)
    {
        if (soundLength <= TimeSpan.Zero) throw new InvalidDataException("The lesson has no sound to make its video with");

        var ordered = pieces.Where(piece => piece.Start < soundLength && piece.Facts.Length > TimeSpan.Zero).OrderBy(piece => piece.Start).ToList();
        if (ordered.Count == 0) throw new InvalidDataException("The lesson has no camera piece that holds any of its picture before its sound ends");

        List<(LessonVideoSource Source, PlannedSlice Slice)> slices = [];
        if (older is not null && ordered[0].Start > TimeSpan.Zero && older.Facts.Length > TimeSpan.Zero)
            slices.Add((older, new PlannedSlice(older.Path, TimeSpan.Zero, Min(older.Facts.Length, ordered[0].Start))));

        for (var i = 0; i < ordered.Count; i++)
        {
            var piece = ordered[i];
            var until = i + 1 < ordered.Count ? ordered[i + 1].Start : soundLength;
            slices.Add((piece, new PlannedSlice(piece.Path, piece.Start, Min(piece.Facts.Length, until - piece.Start))));
        }

        var first = slices[0].Source.Facts;
        var odd = slices.Select(slice => slice.Source).FirstOrDefault(source => !source.Facts.JoinsWith(first));
        var newest = ordered[^1].Facts;
        var why = odd is null
            ? null
            : $"{System.IO.Path.GetFileName(odd.Path)} was encoded differently from {System.IO.Path.GetFileName(slices[0].Source.Path)} (another encoder or another picture size)";
        return new LessonVideoPlan([.. slices.Select(slice => slice.Slice)], soundLength, odd is not null, why, odd is null ? (first.Width, first.Height) : (newest.Width, newest.Height));
    }

    /// <summary>The shorter of two lengths.</summary>
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
}

/// <summary>What the check of a lesson's video asks of the file written (teach-video 6.9).</summary>
/// <param name="Length">The length of the lesson's longest .wav track, which the video's must match within <see cref="Tolerance"/>.</param>
/// <param name="FramesAt">Where a frame must decode: the start, each side of every join, and the end (see <see cref="LessonVideoPlan.FramesToCheck"/>).</param>
public sealed record LessonVideoCheck(TimeSpan Length, IReadOnlyList<TimeSpan> FramesAt)
{
    /// <summary>How far the video's length may be from the sound's: a tenth of a second.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// The media work of a lesson's video, the seam between the finish job and Media Foundation (see
/// <see cref="Windows.MediaFoundationLessonVideo"/>), so that the job's rules are tested with a fake: what a source holds,
/// writing the video, and checking it. It opens no device.
/// </summary>
public interface ILessonVideoMedia
{
    /// <summary>
    /// What the piece or older video at <paramref name="path"/> holds: its picture's size, its H.264 sequence header and its
    /// length. Throws an <see cref="InvalidDataException"/> for a file that holds no H.264 picture that can be read, and
    /// whatever Windows throws for one it cannot open.
    /// </summary>
    VideoSourceFacts Describe(string path);

    /// <summary>
    /// Writes the lesson's video as <paramref name="plan"/> says into <paramref name="path"/>, which must not be there yet:
    /// a plain MP4, not fragmented, its index (moov) after the data, where Windows' MP4 sink writes it though asked for it
    /// first; the picture passed through or encoded again, every frame lasting until the next and the last until the plan's
    /// end, and <paramref name="sound"/> read to its end, encoded AAC-LC mono at
    /// 128 kbit/s. The file is held open for writing, others allowed to read it, until this returns, so that the recording
    /// counts as being written meanwhile. How far it has got, from 0 to 1, goes to <paramref name="progress"/>. Throws when
    /// it cannot be written, and an <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is
    /// cancelled; either leaves the file for the caller to delete.
    /// </summary>
    void Write(LessonVideoPlan plan, Audio.MixedSound sound, string path, Action<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the video at <paramref name="path"/> as <paramref name="check"/> asks: it opens, it has a picture and a sound,
    /// its length matches, and a frame decodes at each place asked. Null when it passes; otherwise why not, in a few words.
    /// </summary>
    string? Check(string path, LessonVideoCheck check);
}
