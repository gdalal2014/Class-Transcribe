using ClassScribe.Audio;
using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// A lesson video's media work as a test plays it, in place of Media Foundation (see <see cref="ILessonVideoMedia"/>): each
/// source's facts are the test's to say (720p from the hardware encoder by default, as long as its name's file says when it
/// holds a number of seconds, else ten minutes), or a failure; the writing holds the file open for writing as the real one
/// does, reads the whole sound, notes the plan and every sample's count and peak, says its progress, and can be made to
/// wait at a gate, fail, or wait for a cancel; the check answers as the test says, noting what it was asked. Nothing is
/// encoded or decoded. Safe to use from any thread.
/// </summary>
public sealed class FakeLessonVideoMedia : ILessonVideoMedia
{
    /// <summary>The hardware encoder's sequence header, as the tests make it up.</summary>
    public static readonly byte[] HardwareHeader = [0, 0, 0, 1, 0x67, 0x4D, 0x40, 0x1F];

    /// <summary>The software encoder's sequence header, as the tests make it up: another header.</summary>
    public static readonly byte[] SoftwareHeader = [0, 0, 0, 1, 0x67, 0x4D, 0x00, 0x1F];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Func<VideoSourceFacts>> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LessonVideoPlan> _written = [];
    private readonly List<LessonVideoCheck> _checks = [];

    /// <summary>Says what the file named <paramref name="name"/> holds.</summary>
    public void Holds(string name, VideoSourceFacts facts)
    {
        lock (_gate) _facts[name] = () => facts;
    }

    /// <summary>Makes the file named <paramref name="name"/> one that cannot be read.</summary>
    public void Unreadable(string name)
    {
        lock (_gate) _facts[name] = () => throw new InvalidDataException("it holds no H.264 picture");
    }

    /// <summary>What a source holds when the test said nothing of it: 720p from the hardware encoder, ten minutes long.</summary>
    public VideoSourceFacts Default { get; set; } = new(1280, 720, HardwareHeader, TimeSpan.FromMinutes(10));

    /// <summary>When set, the writing waits for it, cancellation passed through, before it ends: a video taking its time.</summary>
    public ManualResetEventSlim? HoldWrite { get; set; }

    /// <summary>When set, the writing throws this once it has made its file.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>What the check answers: null passes, words fail it.</summary>
    public string? CheckAnswer { get; set; }

    /// <summary>The plans written, in order.</summary>
    public IReadOnlyList<LessonVideoPlan> Written
    {
        get { lock (_gate) return [.. _written]; }
    }

    /// <summary>What each check was asked, in order.</summary>
    public IReadOnlyList<LessonVideoCheck> Checks
    {
        get { lock (_gate) return [.. _checks]; }
    }

    /// <summary>The samples the last writing read from the sound.</summary>
    public long SoundSamples { get; private set; }

    /// <summary>The rate of the sound the last writing read.</summary>
    public int SoundRate { get; private set; }

    /// <summary>The loudest sample the last writing read, from 0 to 1.</summary>
    public float SoundPeak { get; private set; }

    /// <summary>True while a writing runs, its file held open.</summary>
    public bool Writing { get; private set; }

    /// <inheritdoc />
    public VideoSourceFacts Describe(string path)
    {
        lock (_gate)
        {
            return _facts.TryGetValue(Path.GetFileName(path), out var facts) ? facts() : Default;
        }
    }

    /// <inheritdoc />
    public void Write(LessonVideoPlan plan, MixedSound sound, string path, Action<double> progress, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        lock (_gate) _written.Add(plan);
        Writing = true;
        try
        {
            file.Write("a lesson's video"u8);
            var buffer = new float[sound.SampleRate];
            long samples = 0;
            var peak = 0f;
            int read;
            while ((read = sound.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples += read;
                for (var i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
            }

            (SoundSamples, SoundRate, SoundPeak) = (samples, sound.SampleRate, peak);
            progress(0.5);
            HoldWrite?.Wait(cancellationToken);
            if (WriteFailure is { } failure) throw failure;
            progress(1);
        }
        finally
        {
            Writing = false;
        }
    }

    /// <inheritdoc />
    public string? Check(string path, LessonVideoCheck check)
    {
        lock (_gate) _checks.Add(check);
        return File.Exists(path) ? CheckAnswer : "it is not there";
    }
}
