using System.Text.Json;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// The window's title and taskbar button for each state of a run (design 1.13): what Gulzar sees at a glance on the
/// title bar, the terminal's tab and the taskbar while a class records, uploads and ends.
/// </summary>
[TestFixture]
public class TitleTextTests
{
    /// <summary>Where the recording is in the examples: an hour, 12 minutes and 40 seconds in.</summary>
    private static readonly TimeSpan Position = new(1, 12, 40);

    /// <summary>A run of "Kalam" in <paramref name="phase"/>, with <paramref name="attention"/> or none, recording in the background.</summary>
    private static RecorderState State(RecorderPhase phase, Attention? attention = null, int? uploadPercent = null,
        MicrophoneState? microphone = null, bool inWindow = false, string name = "Kalam") =>
        new(phase, name, Position, "Speakers (Realtek(R) Audio)", null, microphone, uploadPercent, null, attention ?? Attention.None, inWindow);

    /// <summary>An Alert with <paramref name="words"/> for the title.</summary>
    private static Attention Alert(string words) => new(AttentionLevel.Alert, words, $"{words}.", 1);

    /// <summary>A Notice with <paramref name="words"/> for the title.</summary>
    private static Attention Notice(string words) => new(AttentionLevel.Notice, words, $"{words}.", 1);

    /// <summary>The rows of the table in design 1.13, each with the title it gives.</summary>
    private static IEnumerable<TestCaseData> TableRows()
    {
        yield return new TestCaseData(State(RecorderPhase.Recording), "REC 01:12:40 Kalam | ClassScribe").SetName("Recording");
        yield return new TestCaseData(State(RecorderPhase.Recording, microphone: new MicrophoneState("Microphone Array", On: false, Capturing: true, null)),
            "REC 01:12:40 MIC OFF Kalam | ClassScribe").SetName("Recording, microphone off");
        yield return new TestCaseData(State(RecorderPhase.Paused), "PAUSED 01:12:40 Kalam | ClassScribe").SetName("Paused");
        yield return new TestCaseData(State(RecorderPhase.Recording, Notice("no sound 2 min")),
            "REC 01:12:40 Kalam (no sound 2 min) | ClassScribe").SetName("A Notice");
        yield return new TestCaseData(State(RecorderPhase.NotRecording, Alert("NOT RECORDING (waiting for \"chrome\")")),
            "! NOT RECORDING (waiting for \"chrome\") Kalam | ClassScribe").SetName("An Alert: not recording");
        yield return new TestCaseData(State(RecorderPhase.Recording, Alert("NO AUDIO from \"chrome\"")),
            "! NO AUDIO from \"chrome\" Kalam | ClassScribe").SetName("An Alert: no audio");
        yield return new TestCaseData(State(RecorderPhase.Recording, Alert("CANNOT WRITE")), "! CANNOT WRITE Kalam | ClassScribe").SetName("An Alert: cannot write");
        yield return new TestCaseData(State(RecorderPhase.Recording, Alert("SLEPT 19:42 to 19:50")),
            "! SLEPT 19:42 to 19:50 Kalam | ClassScribe").SetName("An Alert: slept");
        yield return new TestCaseData(State(RecorderPhase.Uploading, uploadPercent: 40), "UPLOADING 40% Kalam | ClassScribe").SetName("Uploading");
        yield return new TestCaseData(State(RecorderPhase.Transcribing), "TRANSCRIBING Kalam | ClassScribe").SetName("Transcribing");
        yield return new TestCaseData(State(RecorderPhase.Done), "DONE Kalam | ClassScribe").SetName("Done");
        yield return new TestCaseData(State(RecorderPhase.Failed), "! FAILED Kalam | ClassScribe").SetName("Failed");
    }

    /// <summary>Every title of the table in design 1.13, each ending with "| ClassScribe".</summary>
    [TestCaseSource(nameof(TableRows))]
    public void Every_title_of_the_table(RecorderState state, string title)
    {
        Assert.That(TitleText.For(state), Is.EqualTo(title));
    }

    /// <summary>
    /// A run recording in the window rather than in the background says so, right before "| ClassScribe", whatever its
    /// state: closing that window ends the recording.
    /// </summary>
    [Test]
    public void A_run_in_this_window_says_so_before_the_app_name()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TitleText.For(State(RecorderPhase.Recording, inWindow: true)), Is.EqualTo("REC 01:12:40 Kalam (in this window) | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Recording, Notice("no sound 2 min"), inWindow: true)),
                Is.EqualTo("REC 01:12:40 Kalam (no sound 2 min) (in this window) | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Recording, Alert("CANNOT WRITE"), inWindow: true)),
                Is.EqualTo("! CANNOT WRITE Kalam (in this window) | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Done, inWindow: true)), Is.EqualTo("DONE Kalam (in this window) | ClassScribe"));
        });
    }

    /// <summary>The phases the table leaves out have titles of the same kind.</summary>
    [TestCase(RecorderPhase.Starting, "STARTING Kalam | ClassScribe")]
    [TestCase(RecorderPhase.Stopping, "STOPPING Kalam | ClassScribe")]
    [TestCase(RecorderPhase.Preparing, "PREPARING Kalam | ClassScribe")]
    [TestCase(RecorderPhase.Uploading, "UPLOADING Kalam | ClassScribe")]
    [TestCase(RecorderPhase.Cancelled, "CANCELLED Kalam | ClassScribe")]
    public void The_phases_the_table_leaves_out_have_titles_too(RecorderPhase phase, string title)
    {
        Assert.That(TitleText.For(State(phase)), Is.EqualTo(title));
    }

    /// <summary>
    /// While nothing can be recorded but nothing needs attention yet, the title stays REC, as it does while a device is
    /// replaced in a second or two (design 4.1, row 1); after 15 seconds the Alert takes over.
    /// </summary>
    [Test]
    public void Not_recording_before_it_needs_attention_still_reads_REC()
    {
        Assert.That(TitleText.For(State(RecorderPhase.NotRecording)), Is.EqualTo("REC 01:12:40 Kalam | ClassScribe"));
        Assert.That(TitleText.TaskbarFor(State(RecorderPhase.NotRecording)), Is.EqualTo(TaskbarState.Normal));
    }

    /// <summary>
    /// A run that stops by itself, with --minutes or the safety stop near, says when after the name while the class records,
    /// paused or waiting too, in the local time of day the run wrote; a Notice's words come first. Once the recording has
    /// stopped there is nothing left to stop, and the title no longer says it.
    /// </summary>
    [Test]
    public void A_run_that_stops_by_itself_says_when_while_it_records()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TitleText.For(State(RecorderPhase.Recording) with { StopsAtText = "20:30" }), Is.EqualTo("REC 01:12:40 Kalam (stops 20:30) | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Paused) with { StopsAtText = "20:30" }), Is.EqualTo("PAUSED 01:12:40 Kalam (stops 20:30) | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.NotRecording, Alert("NOT RECORDING (waiting for \"chrome\")")) with { StopsAtText = "20:30" }),
                Is.EqualTo("! NOT RECORDING (waiting for \"chrome\") Kalam (stops 20:30) | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Recording, Notice("no sound 2 min"), inWindow: true) with { StopsAtText = "20:30" }),
                Is.EqualTo("REC 01:12:40 Kalam (no sound 2 min) (stops 20:30) (in this window) | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Transcribing) with { StopsAtText = "20:30" }), Is.EqualTo("TRANSCRIBING Kalam | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Recording) with { StopsAtText = "\u0007" }), Is.EqualTo("REC 01:12:40 Kalam | ClassScribe"), "nothing left once cleaned");
        });
    }

    /// <summary>A paused recording that needs a look keeps PAUSED, with the Notice's words after the name.</summary>
    [Test]
    public void A_paused_recording_with_a_notice_keeps_PAUSED()
    {
        Assert.That(TitleText.For(State(RecorderPhase.Paused, Notice("paused 10 min"))), Is.EqualTo("PAUSED 01:12:40 Kalam (paused 10 min) | ClassScribe"));
    }

    /// <summary>A paused recording whose microphone is switched off says both: P paused it, and V switched the microphone off.</summary>
    [Test]
    public void A_paused_recording_with_its_microphone_off_says_MIC_OFF_too()
    {
        var state = State(RecorderPhase.Paused, microphone: new MicrophoneState("Microphone Array", On: false, Capturing: true, null));

        Assert.That(TitleText.For(state), Is.EqualTo("PAUSED 01:12:40 MIC OFF Kalam | ClassScribe"));
    }

    /// <summary>Your microphone following chrome, in the gate's state <paramref name="gate"/>, switched on unless <paramref name="on"/> says otherwise.</summary>
    private static MicrophoneState Following(MicrophoneGateState gate, bool on = true) =>
        new("Microphone Array", on, Capturing: true, Problem: null, Follows: "chrome", gate, Why: gate == MicrophoneGateState.Recording ? null : "the gate's words");

    /// <summary>The titles while the microphone follows the class app: MIC MUTED while the app is not listening or Windows mutes, MIC WAITING while the app has not opened the microphone, uses another or cannot be read, MIC OFF while switched off, and nothing while your voice is recorded.</summary>
    private static IEnumerable<TestCaseData> MicrophoneRows()
    {
        yield return new TestCaseData(Following(MicrophoneGateState.Recording), "REC 01:12:40 Kalam | ClassScribe").SetName("Microphone: recording");
        yield return new TestCaseData(Following(MicrophoneGateState.NotListening), "REC 01:12:40 MIC MUTED Kalam | ClassScribe").SetName("Microphone: the app is not listening");
        yield return new TestCaseData(Following(MicrophoneGateState.MutedInWindows), "REC 01:12:40 MIC MUTED Kalam | ClassScribe").SetName("Microphone: muted in Windows");
        yield return new TestCaseData(Following(MicrophoneGateState.NotOpened), "REC 01:12:40 MIC WAITING Kalam | ClassScribe").SetName("Microphone: the app has not opened it");
        yield return new TestCaseData(Following(MicrophoneGateState.OtherMicrophone), "REC 01:12:40 MIC WAITING Kalam | ClassScribe").SetName("Microphone: the app uses another");
        yield return new TestCaseData(Following(MicrophoneGateState.Unreadable), "REC 01:12:40 MIC WAITING Kalam | ClassScribe").SetName("Microphone: the sessions cannot be read");
        yield return new TestCaseData(Following(MicrophoneGateState.Off, on: false), "REC 01:12:40 MIC OFF Kalam | ClassScribe").SetName("Microphone: off");
        yield return new TestCaseData(Following(MicrophoneGateState.Unfollowed), "REC 01:12:40 Kalam | ClassScribe").SetName("Microphone: no app followed");
    }

    /// <summary>Every title while the microphone follows the class app (addendum 3.3).</summary>
    [TestCaseSource(nameof(MicrophoneRows))]
    public void Every_title_while_the_microphone_follows_the_class_app(MicrophoneState microphone, string title)
    {
        Assert.That(TitleText.For(State(RecorderPhase.Recording, microphone: microphone)), Is.EqualTo(title));
    }

    /// <summary>A paused recording whose microphone is muted says both; a Notice still follows the name.</summary>
    [Test]
    public void A_paused_recording_with_its_microphone_muted_says_MIC_MUTED_too()
    {
        var state = State(RecorderPhase.Paused, Notice("no sound 2 min"), microphone: Following(MicrophoneGateState.NotListening));

        Assert.That(TitleText.For(state), Is.EqualTo("PAUSED 01:12:40 MIC MUTED Kalam (no sound 2 min) | ClassScribe"));
    }

    /// <summary>A microphone state written by a build without the gate reads with no app followed, and the title shows nothing for it.</summary>
    [Test]
    public void A_microphone_state_read_without_the_gate_shows_nothing_for_it()
    {
        var state = JsonSerializer.Deserialize(
            """{"phase":"Recording","name":"Kalam","position":"01:12:40","inWindow":false,"microphone":{"name":"Microphone Array","on":true,"capturing":true}}""",
            RecorderJson.Default.RecorderState)!;

        Assert.That(state.Microphone!.Gate, Is.EqualTo(MicrophoneGateState.Unfollowed));
        Assert.That(TitleText.For(state), Is.EqualTo("REC 01:12:40 Kalam | ClassScribe"));
    }

    /// <summary>A run that has ended shows how it ended, whatever needed attention before.</summary>
    [Test]
    public void A_run_that_has_ended_shows_how_it_ended_whatever_needed_attention()
    {
        var attention = Alert("CANNOT WRITE");

        Assert.Multiple(() =>
        {
            Assert.That(TitleText.For(State(RecorderPhase.Done, attention)), Is.EqualTo("DONE Kalam | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Failed, attention)), Is.EqualTo("! FAILED Kalam | ClassScribe"));
            Assert.That(TitleText.For(State(RecorderPhase.Cancelled, attention)), Is.EqualTo("CANCELLED Kalam | ClassScribe"));
            Assert.That(TitleText.TaskbarFor(State(RecorderPhase.Done, attention)), Is.EqualTo(TaskbarState.None));
        });
    }

    /// <summary>An Alert without words of its own still marks the title, with the phase's words.</summary>
    [Test]
    public void An_alert_without_words_still_marks_the_title()
    {
        Assert.That(TitleText.For(State(RecorderPhase.Recording, Alert(""))), Is.EqualTo("! REC 01:12:40 Kalam | ClassScribe"));
    }

    /// <summary>A Notice without words of its own leaves the title as it is; the taskbar still turns yellow.</summary>
    [Test]
    public void A_notice_without_words_leaves_the_title_as_it_is()
    {
        var state = State(RecorderPhase.Recording, Notice(""));

        Assert.That(TitleText.For(state), Is.EqualTo("REC 01:12:40 Kalam | ClassScribe"));
        Assert.That(TitleText.TaskbarFor(state), Is.EqualTo(TaskbarState.Paused));
    }

    /// <summary>
    /// Control characters in a name or in an attention's words never reach the title: a BEL could ring, and an escape
    /// could start a sequence. Runs of spaces become one.
    /// </summary>
    [Test]
    public void Control_characters_never_reach_the_title()
    {
        var state = State(RecorderPhase.Recording, Alert("CANNOT\aWRITE\u001b]0;x"), name: "Ka\u0007lam\r\n  2");

        var title = TitleText.For(state);

        Assert.That(title, Is.EqualTo("! CANNOT WRITE ]0;x Ka lam 2 | ClassScribe"));
        Assert.That(title.Any(char.IsControl), Is.False);
    }

    /// <summary>
    /// A state read from JSON without its attention, as the recorder's JSON reads a missing field (null, whatever the type
    /// says), gives the title and the taskbar of a state with nothing that needs attention, rather than an exception.
    /// </summary>
    [Test]
    public void A_state_read_without_its_attention_counts_as_needing_none()
    {
        var state = JsonSerializer.Deserialize("""{"phase":"Recording","name":"Kalam","position":"01:12:40","inWindow":false}""", RecorderJson.Default.RecorderState)!;

        Assert.That(state.Attention, Is.Null, "the JSON leaves the missing attention null");
        Assert.That(TitleText.For(state), Is.EqualTo("REC 01:12:40 Kalam | ClassScribe"));
        Assert.That(TitleText.TaskbarFor(state), Is.EqualTo(TaskbarState.Normal));
        Assert.That(TitleText.TaskbarPercentFor(state), Is.EqualTo(100));
    }

    /// <summary>A class longer than a day counts its hours on, as the log does.</summary>
    [Test]
    public void Hours_go_past_a_day()
    {
        var state = State(RecorderPhase.Recording) with { Position = new TimeSpan(1, 1, 3, 7) };

        Assert.That(TitleText.For(state), Is.EqualTo("REC 25:03:07 Kalam | ClassScribe"));
    }

    /// <summary>
    /// The taskbar button for each phase and attention (design 1.13 and section 4): green while recording, yellow when
    /// paused or for a Notice, red for an Alert or a failure, the upload's progress, a running bar while waiting, and
    /// nothing once done. A state without progress of its own fills the bar, so that its colour shows.
    /// </summary>
    [TestCase(RecorderPhase.Starting, AttentionLevel.None, null, TaskbarState.Indeterminate, 0)]
    [TestCase(RecorderPhase.Recording, AttentionLevel.None, null, TaskbarState.Normal, 100)]
    [TestCase(RecorderPhase.NotRecording, AttentionLevel.None, null, TaskbarState.Normal, 100)]
    [TestCase(RecorderPhase.Paused, AttentionLevel.None, null, TaskbarState.Paused, 100)]
    [TestCase(RecorderPhase.Recording, AttentionLevel.Notice, null, TaskbarState.Paused, 100)]
    [TestCase(RecorderPhase.Paused, AttentionLevel.Notice, null, TaskbarState.Paused, 100)]
    [TestCase(RecorderPhase.Recording, AttentionLevel.Alert, null, TaskbarState.Error, 100)]
    [TestCase(RecorderPhase.NotRecording, AttentionLevel.Alert, null, TaskbarState.Error, 100)]
    [TestCase(RecorderPhase.Paused, AttentionLevel.Alert, null, TaskbarState.Error, 100)]
    [TestCase(RecorderPhase.Stopping, AttentionLevel.None, null, TaskbarState.Indeterminate, 0)]
    [TestCase(RecorderPhase.Preparing, AttentionLevel.None, null, TaskbarState.Indeterminate, 0)]
    [TestCase(RecorderPhase.Uploading, AttentionLevel.None, 40, TaskbarState.Normal, 40)]
    [TestCase(RecorderPhase.Uploading, AttentionLevel.None, null, TaskbarState.Indeterminate, 0)]
    [TestCase(RecorderPhase.Uploading, AttentionLevel.Notice, 40, TaskbarState.Paused, 40)]
    [TestCase(RecorderPhase.Uploading, AttentionLevel.Alert, 40, TaskbarState.Error, 40)]
    [TestCase(RecorderPhase.Transcribing, AttentionLevel.None, null, TaskbarState.Indeterminate, 0)]
    [TestCase(RecorderPhase.Transcribing, AttentionLevel.Alert, null, TaskbarState.Error, 100)]
    [TestCase(RecorderPhase.Done, AttentionLevel.None, null, TaskbarState.None, 0)]
    [TestCase(RecorderPhase.Failed, AttentionLevel.None, null, TaskbarState.Error, 100)]
    [TestCase(RecorderPhase.Cancelled, AttentionLevel.None, null, TaskbarState.None, 0)]
    public void The_taskbar_for_each_phase_and_attention(RecorderPhase phase, AttentionLevel level, int? uploadPercent, TaskbarState taskbar, int percent)
    {
        var attention = level == AttentionLevel.None ? Attention.None : new Attention(level, "words", "Words.", 1);
        var state = State(phase, attention, uploadPercent);

        Assert.That(TitleText.TaskbarFor(state), Is.EqualTo(taskbar));
        Assert.That(TitleText.TaskbarPercentFor(state), Is.EqualTo(percent));
    }

    /// <summary>An upload's percent outside 0 to 100 is kept inside, in the title and on the taskbar.</summary>
    [TestCase(140, 100)]
    [TestCase(-5, 0)]
    public void An_upload_percent_is_kept_between_0_and_100(int reported, int shown)
    {
        var state = State(RecorderPhase.Uploading, uploadPercent: reported);

        Assert.That(TitleText.For(state), Is.EqualTo($"UPLOADING {shown}% Kalam | ClassScribe"));
        Assert.That(TitleText.TaskbarPercentFor(state), Is.EqualTo(shown));
    }
}
