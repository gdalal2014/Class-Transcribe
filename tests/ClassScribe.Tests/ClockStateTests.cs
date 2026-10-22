using System.Text.Json;
using ClassScribe.Audio.Timeline;
using ClassScribe.Camera;

namespace ClassScribe.Tests;

/// <summary>
/// The clock's whole state as an immutable snapshot (teach-video 6.6), which the camera process places its frames by: a
/// snapshot places every moment exactly as the clock does at the moment it was taken, whatever came before (begins, pauses,
/// resumes, cuts, an offset), keeps doing so however the clock changes later, carries a version that grows with every
/// change, and crosses the camera's lines as JSON unchanged.
/// </summary>
[TestFixture]
public sealed class ClockStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    /// <summary>A stamp <paramref name="seconds"/> after the test clock's zero, in its units.</summary>
    private static long Stamp(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    /// <summary>
    /// Over many random sequences of begins, pauses, resumes, cuts and the time going on, a snapshot taken after each step
    /// gives every moment, before the origin, inside and around every pause and cut, and after now, the very position and
    /// exclusion the clock gives; and every earlier snapshot still gives what the clock gave when it was taken.
    /// </summary>
    [Test]
    public void A_snapshot_places_exactly_as_the_clock_does_over_random_sequences()
    {
        for (var seed = 1; seed <= 60; seed++)
        {
            var random = new Random(seed);
            var time = new ManualTimeProvider(Start);
            time.Advance(TimeSpan.FromSeconds(10));
            var clock = new RecordingClock(time);
            if (random.Next(3) == 0) clock.SetOffset(TimeSpan.FromSeconds(random.Next(1, 7200)));

            var earlier = new List<(ClockState Snapshot, long[] Stamps, TimeSpan[] Positions, bool[] Excluded)>();
            for (var step = 0; step < 120; step++)
            {
                var now = time.GetTimestamp();
                switch (random.Next(6))
                {
                    case 0:
                        clock.Begin(now - Stamp(random.NextDouble() * 2));
                        break;
                    case 1:
                        clock.Pause();
                        break;
                    case 2:
                        clock.Resume();
                        break;
                    case 3:
                        var from = now - Stamp(random.NextDouble() * 60);
                        clock.Cut(from, from + Stamp(random.NextDouble() * 30));
                        break;
                    default:
                        time.Advance(TimeSpan.FromSeconds(random.NextDouble() * 20));
                        break;
                }

                var snapshot = clock.Snapshot();
                var stamps = Enumerable.Range(0, 40).Select(_ => Stamp(random.NextDouble() * (time.GetTimestamp() / (double)TimeSpan.TicksPerSecond + 30))).ToArray();
                var positions = stamps.Select(clock.Position).ToArray();
                var excluded = stamps.Select(clock.IsExcluded).ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(stamps.Select(snapshot.Position), Is.EqualTo(positions), $"seed {seed}, step {step}: positions");
                    Assert.That(stamps.Select(snapshot.IsExcluded), Is.EqualTo(excluded), $"seed {seed}, step {step}: exclusions");
                    Assert.That(snapshot.Position(time.GetTimestamp()), Is.EqualTo(clock.Now()), $"seed {seed}, step {step}: now");
                    Assert.That(snapshot.IsPaused, Is.EqualTo(clock.IsPaused), $"seed {seed}, step {step}: paused");
                });
                earlier.Add((snapshot, stamps, positions, excluded));
            }

            foreach (var (snapshot, stamps, positions, excluded) in earlier)
            {
                Assert.That(stamps.Select(snapshot.Position), Is.EqualTo(positions), $"seed {seed}: an earlier snapshot changed with the clock");
                Assert.That(stamps.Select(snapshot.IsExcluded), Is.EqualTo(excluded), $"seed {seed}: an earlier snapshot's exclusions changed");
            }
        }
    }

    /// <summary>The version grows with each change the clock makes, and stays for each call that changes nothing: a second pause, a resume while not paused, an empty cut, a second origin.</summary>
    [Test]
    public void The_version_grows_with_each_change_and_only_then()
    {
        var time = new ManualTimeProvider(Start);
        time.Advance(TimeSpan.FromSeconds(10));
        var clock = new RecordingClock(time);
        var versions = new List<long> { clock.Snapshot().Version };

        void Note() => versions.Add(clock.Snapshot().Version);

        clock.SetOffset(TimeSpan.FromMinutes(5));
        Note();
        clock.Begin(Stamp(10));
        Note();
        clock.Begin(Stamp(11));
        Note();
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Pause();
        Note();
        clock.Pause();
        Note();
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Resume();
        Note();
        clock.Resume();
        Note();
        clock.Cut(Stamp(12), Stamp(12));
        Note();
        clock.Cut(Stamp(11), Stamp(12));
        Note();

        Assert.That(versions, Is.EqualTo(new long[] { 0, 1, 2, 2, 3, 3, 4, 4, 4, 5 }));
    }

    /// <summary>A clock before anything happened places every moment at the offset, as the empty snapshot does.</summary>
    [Test]
    public void Before_the_origin_a_snapshot_places_everything_at_the_offset()
    {
        var time = new ManualTimeProvider(Start);
        var clock = new RecordingClock(time);
        clock.SetOffset(TimeSpan.FromMinutes(42));

        var snapshot = clock.Snapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Origin, Is.Null);
            Assert.That(snapshot.Position(Stamp(999)), Is.EqualTo(TimeSpan.FromMinutes(42)));
            Assert.That(ClockState.Empty.Position(Stamp(5)), Is.EqualTo(TimeSpan.Zero));
            Assert.That(ClockState.Empty.IsExcluded(Stamp(5)), Is.False);
        }
    }

    /// <summary>A snapshot sent to the camera process as a clock line and read there places every moment as the one sent.</summary>
    [Test]
    public void A_snapshot_crosses_the_camera_lines_unchanged()
    {
        var time = new ManualTimeProvider(Start);
        time.Advance(TimeSpan.FromSeconds(10));
        var clock = new RecordingClock(time);
        clock.SetOffset(TimeSpan.FromSeconds(90));
        clock.Begin(Stamp(10));
        time.Advance(TimeSpan.FromSeconds(20));
        clock.Pause();
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Resume();
        clock.Cut(Stamp(40), Stamp(44));
        time.Advance(TimeSpan.FromSeconds(30));
        clock.Pause();

        var sent = clock.Snapshot();
        var read = JsonSerializer.Deserialize(JsonSerializer.Serialize(sent, CameraJson.Default.ClockState), CameraJson.Default.ClockState)!;
        var stamps = Enumerable.Range(0, 140).Select(second => Stamp(second * 0.5)).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Version, Is.EqualTo(sent.Version));
            Assert.That(read.Excluded, Is.EqualTo(sent.Excluded));
            Assert.That(stamps.Select(read.Position), Is.EqualTo(stamps.Select(sent.Position)));
            Assert.That(stamps.Select(read.IsExcluded), Is.EqualTo(stamps.Select(sent.IsExcluded)));
        }
    }
}
