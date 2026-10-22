using ClassScribe.Audio;
using ClassScribe.Recorder;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Condition = (string Name, ClassScribe.Commands.AttentionLevel Level, string Short, string Text);

namespace ClassScribe.Commands;

/// <summary>
/// How a YouTube video's recording ends by itself (design 4.4), checked once a second beside the class's checks, in this
/// order, the first that applies stopping it:
/// <list type="number">
/// <item>the window is gone: its browser process ended, as when Gulzar closes it (<see cref="RunEndCause.VideoClosed"/>, confirmed);</item>
/// <item>the window moved on: one video's title for at least 5 seconds with sound heard, then another for two looks, as
/// YouTube's Autoplay moves on once a video ends (<see cref="RunEndCause.VideoEnded"/>, confirmed);</item>
/// <item>two minutes of silence after sound, counted on the steady clock from the end of the last freeze and the last pause,
/// never while paused with P (<see cref="RunEndCause.VideoSilent"/>, not confirmed: a paused video sounds the same);</item>
/// <item>nothing heard in the first 5 minutes, with a Notice from 30 seconds on to press play, never while paused
/// (<see cref="RunEndCause.NothingPlayed"/>);</item>
/// <item>YouTube:LongestVideoHours of recording, or of running, whichever comes first (<see cref="RunEndCause.VideoLimit"/>).</item>
/// </list>
/// Q twice and a class that starts stop it outside these checks, and the safety stop after them. In place of the usual
/// silence warning, once sound has been heard, a warning at the silence warning's time, at most a minute, that the recording
/// stops at 2 minutes, and a Notice while it lasts. The title the window showed for the video is kept on the run's
/// <see cref="VideoPlayback"/> for the video's source file. One watch per run.
/// </summary>
internal sealed class VideoEndWatch(VideoPlayback video, TimeProvider time)
{
    /// <summary>How long a video's title must have stood, with sound heard, before another title ends the video.</summary>
    public static readonly TimeSpan TitleStoodFor = TimeSpan.FromSeconds(5);

    /// <summary>For how many looks in a row another title must stand before it ends the video.</summary>
    public const int NewTitleLooks = 2;

    /// <summary>How long silence after sound may last before the recording stops.</summary>
    public static readonly TimeSpan SilenceStopsAfter = TimeSpan.FromMinutes(2);

    /// <summary>The latest the warning about silence comes, ahead of the stop.</summary>
    private static readonly TimeSpan LatestSilenceWarning = TimeSpan.FromSeconds(60);

    /// <summary>How long after the window opened the Notice to press play comes, while nothing has been heard.</summary>
    public static readonly TimeSpan PressPlayAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long after the window opened the recording stops, while nothing has been heard.</summary>
    public static readonly TimeSpan NothingPlayedAfter = TimeSpan.FromMinutes(5);

    /// <summary>Sound counts as heard at a look when it last arrived this recently.</summary>
    private static readonly TimeSpan HeardWithin = TimeSpan.FromSeconds(1.5);

    /// <summary>What the Notice to press play says.</summary>
    public const string PressPlay =
        "Nothing plays in ClassScribe's video window yet: press play there. If it plays, check that YouTube's player and Chrome's volume in Windows' mixer are up";

    /// <summary>True once sound has been heard in the recording.</summary>
    private bool _heardAny;

    /// <summary>True once the Notice to press play has been said in the log.</summary>
    private bool _pressPlaySaid;

    /// <summary>True once this stretch of silence has been warned about.</summary>
    private bool _silenceWarned;

    /// <summary>The video's title as the window shows it, since when on the steady clock, and whether sound was heard while it stood.</summary>
    private (string Title, long Since, bool Heard)? _title;

    /// <summary>Another title the window shows, and for how many looks in a row.</summary>
    private (string Title, int Looks)? _next;

    /// <summary>
    /// One look at the video's recording, with <paramref name="snapshot"/> showing the recording, <paramref name="context"/> the
    /// run, at <paramref name="now"/> on the steady clock, with nothing heard since <paramref name="awakeSince"/> counting, and
    /// <paramref name="longest"/> as YouTube:LongestVideoHours. Adds its lines to <paramref name="lines"/> and what needs
    /// attention to <paramref name="conditions"/>, and returns why the recording stops now, or null.
    /// </summary>
    public string? Look(RecordingSnapshot snapshot, MonitorContext context, long now, long awakeSince, TimeSpan silenceWarning, TimeSpan longest,
        List<(LogLevel Level, string Text)> lines, List<Condition> conditions)
    {
        // 1. The window is gone.
        if (!video.IsOpen())
        {
            lines.Add((LogLevel.Information, "The video window was closed; stopping the recording"));
            return RunEndCause.VideoClosed;
        }

        // Sound is heard now when it has reached the files and last arrived a moment ago: before any has, the time since the
        // last sound counts from the start, which is no sound.
        var main = snapshot.Main;
        _heardAny |= main.HeardSound;
        var heardNow = _heardAny && !snapshot.Paused && main.Capturing && main.SinceLastSound <= HeardWithin;
        if (heardNow) _silenceWarned = false;

        // 2. The window moved on to another video.
        if (WatchTitle(heardNow, now) is { } movedOn)
        {
            lines.Add((LogLevel.Information, $"The window moved on to another video (\"{movedOn.Next}\"); the recording of \"{movedOn.Played}\" stops here"));
            return RunEndCause.VideoEnded;
        }

        var ran = time.GetElapsedTime(context.StartedTimestamp, now);
        if (!snapshot.Paused && main.Capturing)
        {
            if (_heardAny)
            {
                // 3. Silence after sound: counted from the last sound, the end of the last freeze and the end of the last pause.
                var quiet = Shorter(main.SinceLastSound, time.GetElapsedTime(awakeSince, now));
                var warnAfter = Shorter(silenceWarning, LatestSilenceWarning);
                if (quiet >= SilenceStopsAfter)
                {
                    lines.Add((LogLevel.Information, "Nothing has played for 2 minutes; the video has most likely ended, or YouTube paused it"));
                    return RunEndCause.VideoSilent;
                }

                if (quiet >= warnAfter)
                {
                    var text = $"Nothing has played for {RecordingMonitor.DescribeLength(quiet)}; the recording stops at 2 minutes unless it plays again. Press P here to pause the count";
                    if (!_silenceWarned)
                    {
                        _silenceWarned = true;
                        lines.Add((LogLevel.Warning, $"Nothing has played for {RecordingMonitor.DescribeLength(warnAfter)}; the recording stops at 2 minutes unless it plays again. Press P here to pause the count"));
                    }

                    conditions.Add(("VideoQuiet", AttentionLevel.Notice, "nothing plays", text));
                }
            }
            else
            {
                // 4. Nothing heard since the window opened.
                if (ran >= NothingPlayedAfter)
                {
                    lines.Add((LogLevel.Warning, "Nothing has played in ClassScribe's video window for 5 minutes; stopping"));
                    return RunEndCause.NothingPlayed;
                }

                if (ran >= PressPlayAfter)
                {
                    if (!_pressPlaySaid)
                    {
                        _pressPlaySaid = true;
                        lines.Add((LogLevel.Warning, PressPlay));
                    }

                    conditions.Add(("PressPlay", AttentionLevel.Notice, "press play in the video window", PressPlay));
                }
            }
        }

        // 5. The longest a video is recorded for: of recording or of running, whichever comes first.
        if (Shorter(longest - ran, longest - snapshot.Position) <= TimeSpan.Zero)
        {
            lines.Add((LogLevel.Warning, $"This video has played for {RecordingMonitor.DescribeLength(longest)} (YouTube:LongestVideoHours); stopping"));
            return RunEndCause.VideoLimit;
        }

        return null;
    }

    /// <summary>
    /// Follows the window's title: a title that has stood <see cref="TitleStoodFor"/> with sound heard is the video's, kept on
    /// the playback; another title then, at <see cref="NewTitleLooks"/> looks in a row, means the window moved on, and is
    /// returned with the video's. A look with no title changes nothing; another title before the video's has stood takes its
    /// place.
    /// </summary>
    private (string Played, string Next)? WatchTitle(bool heardNow, long now)
    {
        if (video.Title() is not { } title) return null;

        if (_title is not { } current)
        {
            _title = (title, now, heardNow);
            return null;
        }

        if (title == current.Title)
        {
            _next = null;
            _title = current with { Heard = current.Heard || heardNow };
            if (_title.Value.Heard && time.GetElapsedTime(current.Since, now) >= TitleStoodFor) video.Played(current.Title);
            return null;
        }

        var stood = current.Heard && time.GetElapsedTime(current.Since, now) >= TitleStoodFor;
        if (!stood)
        {
            _title = (title, now, heardNow);
            _next = null;
            return null;
        }

        _next = _next is { } next && next.Title == title ? next with { Looks = next.Looks + 1 } : (title, 1);
        return _next.Value.Looks >= NewTitleLooks ? (current.Title, title) : null;
    }

    /// <summary>The shorter of two lengths.</summary>
    private static TimeSpan Shorter(TimeSpan first, TimeSpan second) => first < second ? first : second;
}
