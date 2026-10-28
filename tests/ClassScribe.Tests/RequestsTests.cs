using System.Text.Json;
using System.Text.Json.Nodes;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// What the windows and the recorders hand each other: a run's request and its source, the run file, the request file and
/// the recorder's state, as they are written down and read back (<see cref="RecorderJson"/>), and what a file that cannot
/// be read throws; the run ids that name runs; the start-up report that reports nothing; the stand-in recording the run's
/// tests drive; and a record request handed to the starter through the command runner of the app as it is put together.
/// </summary>
[TestFixture]
public class RequestsTests
{
    /// <summary>A run id as <see cref="RunIds.New"/> makes them.</summary>
    private const string RunId = "20261015-230002Z-1f3a9c07";

    private TempFolder _folder = null!;
    private TestRuntime _runtime = null!;

    /// <summary>Creates a folder for the app's appsettings.json, and ClassScribe's own folder for the run files.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _runtime = new TestRuntime();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _runtime.Dispose();
        _folder.Dispose();
    }

    /// <summary>Every kind of source a run can record from: the three class sources alone, and the microphone with the class and in person.</summary>
    private static IEnumerable<TestCaseData> Sources()
    {
        yield return new TestCaseData(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null), "automatic").SetArgDisplayNames("--device auto");
        yield return new TestCaseData(new SourcePlan(RecordingMode.Class, new ClassSource.Device("Pixel Buds"), null), "device").SetArgDisplayNames("--device");
        yield return new TestCaseData(new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null), "app").SetArgDisplayNames("--app");
        yield return new TestCaseData(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("msedge"), new MicrophoneChoice("Array")), "app").SetArgDisplayNames("--app with --mic");
        yield return new TestCaseData(new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice(null)), null).SetArgDisplayNames("--in-person");
    }

    /// <summary>A request with every field set: a recording of Kalam from <paramref name="source"/>, continuing an earlier one, for 90 and a half minutes.</summary>
    private static RecordRequest Request(SourcePlan source) => new(
        RunId, "Kalam", Transcribe: true, source, @"D:\Classes", ContinueChoice.Continue, "Kalam_2026-10-15_22-10-00Z", Minutes: 90.5, Open: false);

    /// <summary>A run file with every field set, for a run in <paramref name="state"/> that ended as <paramref name="outcome"/> says, if it has.</summary>
    private static RunRecord Run(RunState state, RunOutcome? outcome) => new(
        RunRecord.CurrentVersion,
        RunId,
        ProcessId: 1234,
        ProcessStartedUtc: new DateTimeOffset(2026, 10, 15, 23, 0, 1, 250, TimeSpan.Zero),
        ExePath: @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe",
        Build: "1.0.0+6b34569",
        Protocol: 1,
        Pipe: $"ClassScribe-{RunId}",
        RunHome.Separate,
        Request(new SourcePlan(RecordingMode.Class, new ClassSource.Device("Pixel Buds"), null)),
        BaseName: "Kalam_2026-10-15_23-00-02Z",
        Folder: @"D:\Classes\Kalam",
        state,
        StartedUtc: new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero),
        HeartbeatUtc: new DateTimeOffset(2026, 10, 15, 23, 47, 12, TimeSpan.Zero),
        Position: TimeSpan.FromSeconds(2830.5),
        UploadPercent: 40,
        EndedBy: RunEndCause.Windows,
        outcome,
        Seen: false);

    /// <summary>
    /// A request to record Kalam as a file holds it, with <paramref name="classSource"/> as its class source and
    /// <paramref name="continueChoice"/> as its choice to continue: one recording of the class alone, kept in D:\Classes,
    /// recorded only, with no limit on its length.
    /// </summary>
    private static string RequestJson(string classSource, string continueChoice = "New") => $$"""
        {
          "runId": "{{RunId}}", "name": "Kalam", "transcribe": false,
          "source": { "mode": "Class", "class": {{classSource}}, "microphone": null },
          "outputRoot": "D:\\Classes", "continue": "{{continueChoice}}", "continueBaseName": null, "minutes": null, "open": true
        }
        """;

    /// <summary>A request with each kind of source, the microphone modes included, is read back from its JSON exactly as it was written.</summary>
    [TestCaseSource(nameof(Sources))]
    public void A_record_request_round_trips_with_each_kind_of_source(SourcePlan source, string? kind)
    {
        var request = Request(source);

        var json = JsonSerializer.Serialize(request, RecorderJson.Default.RecordRequest);

        Assert.That(JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest), Is.EqualTo(request));
    }

    /// <summary>
    /// The saved class a run was started as travels with it as classname (layout-config 4.1), read back as written; a run
    /// started from its own options writes none, so its files are as before, and a file without one reads with none.
    /// </summary>
    [Test]
    public void A_saved_classs_name_round_trips_and_is_written_only_when_set()
    {
        var request = Request(new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null)) with { ClassName = "Mantiq (Chrome)" };

        var json = JsonSerializer.Serialize(request, RecorderJson.Default.RecordRequest);

        Assert.That(JsonNode.Parse(json)!["className"]!.GetValue<string>(), Is.EqualTo("Mantiq (Chrome)"));
        Assert.That(JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest), Is.EqualTo(request));
        Assert.That(JsonSerializer.Serialize(request with { ClassName = null }, RecorderJson.Default.RecordRequest), Does.Not.Contain("className"));
        Assert.That(JsonSerializer.Deserialize(RequestJson("""{ "kind": "automatic" }"""), RecorderJson.Default.RecordRequest)!.ClassName, Is.Null);
    }

    /// <summary>
    /// The glossary the window decided travels with the request to the recorder (architecture 8.2, item 7), read back as
    /// written, its class and warning only when set; a request without one, as an older window writes it, has no glossary
    /// field, and a file without one reads with none, so that the recorder decides by its own ElevenLabs:KeytermsFile.
    /// </summary>
    [Test]
    public void The_decided_glossary_round_trips_and_is_written_only_when_set()
    {
        var plan = new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null);
        var ofClass = Request(plan) with { Glossary = new GlossaryPick("mantiq.txt", @"C:\Users\x\AppData\Roaming\ClassScribe\mantiq.txt", "the saved class \"Mantiq\"", "Mantiq") };
        var none = Request(plan) with { Glossary = new GlossaryPick("", "", "ElevenLabs:KeytermsFile", Warning: "Two classes disagree") };

        var json = JsonSerializer.Serialize(ofClass, RecorderJson.Default.RecordRequest);
        var glossary = JsonNode.Parse(json)!["glossary"]!;

        Assert.That(glossary.AsObject().Select(field => field.Key), Is.EqualTo(new[] { "setting", "path", "from", "className" }));
        Assert.That(JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest), Is.EqualTo(ofClass));
        Assert.That(JsonSerializer.Deserialize(JsonSerializer.Serialize(none, RecorderJson.Default.RecordRequest), RecorderJson.Default.RecordRequest), Is.EqualTo(none));
        Assert.That(JsonSerializer.Serialize(ofClass with { Glossary = null }, RecorderJson.Default.RecordRequest), Does.Not.Contain("glossary"));
        Assert.That(JsonSerializer.Deserialize(RequestJson("""{ "kind": "automatic" }"""), RecorderJson.Default.RecordRequest)!.Glossary, Is.Null);
    }

    /// <summary>
    /// Each kind of class source is named in the file by "kind", next to its own field, so that a reader can tell a device
    /// from an app; a recording in person has no class source at all.
    /// </summary>
    [TestCaseSource(nameof(Sources))]
    public void Each_kind_of_class_source_is_named_in_the_file(SourcePlan source, string? kind)
    {
        var written = JsonNode.Parse(JsonSerializer.Serialize(Request(source), RecorderJson.Default.RecordRequest))!;
        var classSource = written["source"]!["class"];

        if (kind is null)
        {
            Assert.That(classSource, Is.Null);
            return;
        }

        Assert.That(classSource!["kind"]!.GetValue<string>(), Is.EqualTo(kind));
        switch (source.Class)
        {
            case ClassSource.Device device:
                Assert.That(classSource["namePart"]!.GetValue<string>(), Is.EqualTo(device.NamePart));
                break;
            case ClassSource.App app:
                Assert.That(classSource["processName"]!.GetValue<string>(), Is.EqualTo(app.ProcessName));
                break;
        }
    }

    /// <summary>A class source is read whatever the order of its fields, its kind last included, as a file edited by hand may have them.</summary>
    [Test]
    public void A_class_source_is_read_whatever_the_order_of_its_fields()
    {
        var json = RequestJson("""{ "namePart": "Pixel Buds", "kind": "device" }""");

        var read = JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest);

        Assert.That(read, Is.EqualTo(new RecordRequest(
            RunId, "Kalam", Transcribe: false, new SourcePlan(RecordingMode.Class, new ClassSource.Device("Pixel Buds"), null), @"D:\Classes",
            ContinueChoice.New, ContinueBaseName: null, Minutes: null, Open: true)));
    }

    /// <summary>A run file is read back exactly as it was written, while the run records and once it has ended with its outcome.</summary>
    [Test]
    public void A_run_file_round_trips_while_recording_and_once_ended()
    {
        RunRecord[] runs =
        [
            Run(RunState.Recording, outcome: null),
            Run(RunState.Finished, new RunOutcome(OutcomeKind.TranscriptFailed, 1, "The transcript failed; the audio is kept.", null, @"D:\Classes\Kalam",
                @"& ""C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe"" transcribe ""D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.wav""")),
            Run(RunState.Finished, new RunOutcome(OutcomeKind.Transcribed, 0, "Transcript saved.", @"D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md", @"D:\Classes\Kalam", null))
                with { Seen = true, Pipe = null, Home = RunHome.InWindow, EndedBy = null, UploadPercent = null }
        ];

        foreach (var run in runs)
        {
            var json = JsonSerializer.Serialize(run, RecorderJson.Default.RunRecord);

            Assert.That(JsonSerializer.Deserialize(json, RecorderJson.Default.RunRecord), Is.EqualTo(run), json);
        }
    }

    /// <summary>
    /// The request file a window hands its recorder is read back with the request and every setting, a setting without a
    /// value included.
    /// </summary>
    [Test]
    public void A_request_file_round_trips_with_its_settings()
    {
        var file = new RunRequestFile(
            RunRequestFile.CurrentVersion,
            Request(new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null)),
            new Dictionary<string, string?>
            {
                ["Recording:OutputFolder"] = @"D:\Classes",
                ["Recording:StopAfterHours"] = "9",
                ["ElevenLabs:BaseUrl"] = "http://localhost:5001/",
                ["Logging:LogLevel:Default"] = "Information",
                ["Transcript:IncludeTimestamps"] = null
            });

        var json = JsonSerializer.Serialize(file, RecorderJson.Default.RunRequestFile);
        var read = JsonSerializer.Deserialize(json, RecorderJson.Default.RunRequestFile)!;

        Assert.That(read.Version, Is.EqualTo(RunRequestFile.CurrentVersion));
        Assert.That(read.Request, Is.EqualTo(file.Request));
        Assert.That(read.Settings, Is.EquivalentTo(file.Settings));
    }

    /// <summary>The recorder's state is read back exactly as it was written: while recording with the microphone and a stop set, and while uploading with nothing to attend to.</summary>
    [Test]
    public void The_recorders_state_round_trips()
    {
        RecorderState[] states =
        [
            new(RecorderPhase.Recording, "Kalam", TimeSpan.FromSeconds(4362.25), "Speakers (Realtek(R) Audio)", WaitingFor: null,
                new MicrophoneState("Microphone Array (Intel® Smart Sound Technology)", On: false, Capturing: true, Problem: null),
                UploadPercent: null, StopsAtUtc: new DateTimeOffset(2026, 10, 16, 0, 30, 0, TimeSpan.Zero),
                new Attention(AttentionLevel.Notice, "no sound 2 min", "No sound for 2 minutes.", 3), InWindow: true),
            new(RecorderPhase.Uploading, "Kalam", TimeSpan.FromHours(1), "chrome", "\"chrome\" to play sound", Microphone: null,
                UploadPercent: 40, StopsAtUtc: null, Attention.None, InWindow: false)
        ];

        foreach (var state in states)
        {
            var json = JsonSerializer.Serialize(state, RecorderJson.Default.RecorderState);

            Assert.That(JsonSerializer.Deserialize(json, RecorderJson.Default.RecorderState), Is.EqualTo(state), json);
        }
    }

    /// <summary>
    /// When nothing needs attention, and nothing has yet, the level is None, there are no words, and the count is 0, from
    /// which every new event counts, so that a window flashes once for each.
    /// </summary>
    [Test]
    public void Nothing_needing_attention_has_no_words_and_counts_from_zero()
    {
        Assert.That(Attention.None, Is.EqualTo(new Attention(AttentionLevel.None, "", "", 0)));
    }

    /// <summary>
    /// The states, homes, modes and choices are written by name, not by number, so that a run file means the same to a
    /// build whose lists of them are ordered differently, and a person can read it.
    /// </summary>
    [Test]
    public void Choices_and_states_are_written_by_name()
    {
        var run = Run(RunState.Transcribing, new RunOutcome(OutcomeKind.Cancelled, 130, "Cancelled.", null, null, null));

        var json = JsonSerializer.Serialize(run, RecorderJson.Default.RunRecord);

        Assert.That(json, Does.Contain("\"state\":\"Transcribing\"")
            .And.Contain("\"home\":\"Separate\"")
            .And.Contain("\"mode\":\"Class\"")
            .And.Contain("\"continue\":\"Continue\"")
            .And.Contain("\"kind\":\"Cancelled\""));
    }

    /// <summary>
    /// A run file is written exactly as every build reads it, because a run file one build writes is read by another: the
    /// layout's version 1, the fields' names in camelCase, choices and states by name, what ended the recording in the
    /// words of <see cref="RunEndCause"/>, and all of it on one line.
    /// </summary>
    [Test]
    public void A_run_file_is_written_as_every_build_reads_it()
    {
        var run = Run(RunState.Finished, new RunOutcome(OutcomeKind.Transcribed, 0, "Transcript saved.", @"D:\Classes\Kalam\Kalam_2026-10-15_23-00-02Z.md",
            @"D:\Classes\Kalam", RetryCommand: null)) with { EndedBy = RunEndCause.Q, UploadPercent = 100 };
        // The lines are joined without their breaks. The + in the build is written as \u002B, as System.Text.Json's
        // default encoder writes it in a string; the + in a time is not.
        const string expected = """
            {"version":1,"runId":"20261015-230002Z-1f3a9c07","processId":1234,"processStartedUtc":"2026-10-15T23:00:01.25+00:00",
            "exePath":"C:\\Users\\gdala\\AppData\\Local\\Programs\\ClassScribe\\ClassScribe.exe","build":"1.0.0\u002B6b34569","protocol":1,
            "pipe":"ClassScribe-20261015-230002Z-1f3a9c07","home":"Separate",
            "request":{"runId":"20261015-230002Z-1f3a9c07","name":"Kalam","transcribe":true,
            "source":{"mode":"Class","class":{"kind":"device","namePart":"Pixel Buds"},"microphone":null},
            "outputRoot":"D:\\Classes","continue":"Continue","continueBaseName":"Kalam_2026-10-15_22-10-00Z","minutes":90.5,"open":false},
            "baseName":"Kalam_2026-10-15_23-00-02Z","folder":"D:\\Classes\\Kalam","state":"Finished",
            "startedUtc":"2026-10-15T23:00:02+00:00","heartbeatUtc":"2026-10-15T23:47:12+00:00","position":"00:47:10.5000000",
            "uploadPercent":100,"endedBy":"Q",
            "outcome":{"kind":"Transcribed","exitCode":0,"message":"Transcript saved.",
            "transcriptPath":"D:\\Classes\\Kalam\\Kalam_2026-10-15_23-00-02Z.md","folder":"D:\\Classes\\Kalam","retryCommand":null},
            "seen":false}
            """;

        Assert.That(JsonSerializer.Serialize(run, RecorderJson.Default.RunRecord), Is.EqualTo(expected.ReplaceLineEndings("")));
    }

    /// <summary>
    /// A request file is written exactly as the recorder reads it: the layout's version 1, the request, and the settings by
    /// their names as the configuration spells them, a setting without a value included, all on one line.
    /// </summary>
    [Test]
    public void A_request_file_is_written_as_the_recorder_reads_it()
    {
        var file = new RunRequestFile(
            RunRequestFile.CurrentVersion,
            Request(new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null)),
            new Dictionary<string, string?> { ["Recording"] = null, ["Recording:StopAfterHours"] = "9" });
        // The lines are joined without their breaks.
        const string expected = """
            {"version":1,
            "request":{"runId":"20261015-230002Z-1f3a9c07","name":"Kalam","transcribe":true,
            "source":{"mode":"Class","class":{"kind":"app","processName":"chrome"},"microphone":null},
            "outputRoot":"D:\\Classes","continue":"Continue","continueBaseName":"Kalam_2026-10-15_22-10-00Z","minutes":90.5,"open":false},
            "settings":{"Recording":null,"Recording:StopAfterHours":"9"}}
            """;

        Assert.That(JsonSerializer.Serialize(file, RecorderJson.Default.RunRequestFile), Is.EqualTo(expected.ReplaceLineEndings("")));
    }

    /// <summary>
    /// What ended a recording is written in the run file in the words every build reads, as design 1.7 names them: Q,
    /// Minutes, SafetyStop, Windows, WindowClosed and Error.
    /// </summary>
    [TestCase(RunEndCause.Q, "Q")]
    [TestCase(RunEndCause.Minutes, "Minutes")]
    [TestCase(RunEndCause.SafetyStop, "SafetyStop")]
    [TestCase(RunEndCause.Windows, "Windows")]
    [TestCase(RunEndCause.WindowClosed, "WindowClosed")]
    [TestCase(RunEndCause.Error, "Error")]
    public void What_ended_a_recording_is_written_in_the_words_every_build_reads(string cause, string written)
    {
        Assert.That(cause, Is.EqualTo(written));
    }

    /// <summary>
    /// A field the reader does not know, such as one a later build adds, is passed over, in the run file, in the request
    /// inside it and in the request's plan, so that an older build still reads a newer build's run files; and it is kept
    /// (amendment 2), so that this build writing the file back, as marking a run seen does, loses none of them.
    /// </summary>
    [Test]
    public void A_field_the_reader_does_not_know_is_passed_over_and_kept_when_written_back()
    {
        var run = Run(RunState.Recording, outcome: null);
        var written = JsonNode.Parse(JsonSerializer.Serialize(run, RecorderJson.Default.RunRecord))!.AsObject();
        written["addedLater"] = 7;
        written["request"]!.AsObject()["alsoAddedLater"] = "something";
        written["request"]!["source"]!.AsObject()["planAddedLater"] = new JsonObject { ["namePart"] = "OBSBOT" };

        var read = JsonSerializer.Deserialize(written.ToJsonString(), RecorderJson.Default.RunRecord)!;
        var seen = JsonNode.Parse(JsonSerializer.Serialize(read with { Seen = true }, RecorderJson.Default.RunRecord))!.AsObject();

        Assert.That(read with { Extra = null, Request = read.Request with { Extra = null, Source = read.Request.Source with { Extra = null } } }, Is.EqualTo(run));
        Assert.That(seen["addedLater"]!.GetValue<int>(), Is.EqualTo(7));
        Assert.That(seen["request"]!["alsoAddedLater"]!.GetValue<string>(), Is.EqualTo("something"));
        Assert.That(seen["request"]!["source"]!["planAddedLater"]!["namePart"]!.GetValue<string>(), Is.EqualTo("OBSBOT"));
        Assert.That(seen["seen"]!.GetValue<bool>(), Is.True);
    }

    /// <summary>
    /// A run with a camera writes its plan's camera and the size the window resolved, which an older build passes over; a
    /// run without one writes no camera at all, so that its files are as before; and nothing unknown writes no extension data.
    /// </summary>
    [Test]
    public void A_camera_and_its_size_are_written_only_for_a_run_with_one()
    {
        var run = Run(RunState.Recording, outcome: null);
        var withCamera = run with { Request = run.Request with { Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), new CameraChoice("OBSBOT", "720p")) } };

        var plain = JsonSerializer.Serialize(run, RecorderJson.Default.RunRecord);
        var camera = JsonSerializer.Serialize(withCamera, RecorderJson.Default.RunRecord);

        Assert.That(plain, Does.Not.Contain("camera").And.Not.Contain("extra"));
        Assert.That(JsonNode.Parse(camera)!["request"]!["source"]!["camera"]!.ToJsonString(), Is.EqualTo("""{"namePart":"OBSBOT","resolution":"720p"}"""));
        Assert.That(JsonSerializer.Deserialize(camera, RecorderJson.Default.RunRecord), Is.EqualTo(withCamera));
    }

    /// <summary>Class sources without their kind: a device's name alone, nothing at all, and Kind in capitals, which is not its name.</summary>
    private static IEnumerable<TestCaseData> ClassSourcesWithoutTheirKind()
    {
        yield return new TestCaseData("""{ "namePart": "Pixel Buds" }""").SetArgDisplayNames("a device's name alone");
        yield return new TestCaseData("{}").SetArgDisplayNames("nothing");
        yield return new TestCaseData("""{ "Kind": "device", "namePart": "Pixel Buds" }""").SetArgDisplayNames("Kind in capitals");
    }

    /// <summary>
    /// A class source without its "kind", as a file edited by hand may have it, cannot be read, and the reader says so with
    /// a NotSupportedException, not a JsonException. Whoever reads a run file or a request file through
    /// <see cref="RecorderJson"/> treats both as a file that cannot be read.
    /// </summary>
    [TestCaseSource(nameof(ClassSourcesWithoutTheirKind))]
    public void A_class_source_without_its_kind_cannot_be_read(string classSource)
    {
        var json = RequestJson(classSource);

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest));
    }

    /// <summary>Requests that cannot be read for other reasons: a kind of class source or a choice this build does not know, as a later build might write them, and a file cut short.</summary>
    private static IEnumerable<TestCaseData> RequestsThatCannotBeRead()
    {
        yield return new TestCaseData(RequestJson("""{ "kind": "microphone" }""")).SetArgDisplayNames("a kind it does not know");
        yield return new TestCaseData(RequestJson("""{ "kind": "automatic" }""", continueChoice: "Later")).SetArgDisplayNames("a choice it does not know");
        yield return new TestCaseData(RequestJson("""{ "kind": "automatic" }""")[..80]).SetArgDisplayNames("cut short");
    }

    /// <summary>
    /// A kind of class source or a choice this build does not know, as a later build might write them, and a file cut
    /// short, cannot be read either, and the reader says so with a JsonException.
    /// </summary>
    [TestCaseSource(nameof(RequestsThatCannotBeRead))]
    public void An_unknown_kind_or_choice_and_a_file_cut_short_throw_a_JsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest));
    }

    /// <summary>
    /// A run id is the run's start time in UTC, to the second, and 8 random hexadecimal digits: runs started in the same
    /// second still get ids of their own, and ids sort by start time.
    /// </summary>
    [Test]
    public void A_run_id_is_its_start_time_in_UTC_and_eight_random_hex_digits()
    {
        var started = new DateTimeOffset(2026, 10, 15, 19, 0, 2, TimeSpan.FromHours(-4));

        var first = RunIds.New(started);
        var second = RunIds.New(started);

        Assert.That(first, Does.Match("^20261015-230002Z-[0-9a-f]{8}$"));
        Assert.That(second, Does.Match("^20261015-230002Z-[0-9a-f]{8}$").And.Not.EqualTo(first));
        Assert.That(RunIds.IsWellFormed(first), Is.True);
    }

    /// <summary>
    /// Only a run id as ClassScribe makes them is taken, before it names a file, a pipe or an event: nothing that could
    /// reach outside the runs folder, no line break at its end, no other digits than 0 to 9, and lower-case hex only.
    /// </summary>
    [TestCase("20261015-230002Z-1f3a9c07", true)]
    [TestCase("20261015-230002Z-1F3A9C07", false)]
    [TestCase("20261015-230002Z-1f3a9c0", false)]
    [TestCase("20261015-230002Z-1f3a9c07\n", false)]
    [TestCase("20261015-230002Z-1f3a9c07\\..\\..\\x", false)]
    [TestCase(@"..\..\Windows\win", false)]
    [TestCase("２0261015-230002Z-1f3a9c07", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void Only_a_run_id_as_ClassScribe_makes_them_is_taken(string? text, bool wellFormed)
    {
        Assert.That(RunIds.IsWellFormed(text), Is.EqualTo(wellFormed));
    }

    /// <summary>The start-up report that stands in until the real one is registered reports nothing: no choice, no header lines, no recording now.</summary>
    [Test]
    public async Task The_start_up_report_that_stands_in_reports_nothing()
    {
        IStartupReport report = new NoStartupReport();

        Assert.That(await report.ShowAsync(interactive: true), Is.Null);
        Assert.That(await report.ShowAsync(interactive: false), Is.Null);
        Assert.That(report.HeaderLines(), Is.Empty);
        Assert.That(report.RecordingNow(), Is.Null);
    }

    /// <summary>
    /// The stand-in recording the run's tests drive behaves as a recording does, seen through <see cref="IRecording"/>: its
    /// position is the offset it was started at plus what this run recorded, nothing is recorded while paused, the checks
    /// see the track as the test sets it, a recording of the class alone answers V with null, and stopping reports what the
    /// test says was recorded. Its factory hands out the recordings queued first, then new ones of the plan's mode: with the
    /// class and the microphone, the microphone has a track of its own, which hands over audio all the time and which V
    /// switches off.
    /// </summary>
    [Test]
    public void The_stand_in_recording_behaves_as_a_recording_does()
    {
        var factory = new FakeRecordingFactory();
        var queued = factory.Enqueue(new FakeRecording { HeardAnything = false, MainSegments = ["Kalam_2026-10-15_23-00-02Z_part2.wav"] });
        var files = new RecordingFiles(@"D:\Classes\Kalam", "Kalam_2026-10-15_23-00-02Z");
        var notes = new List<TimelineNote>();

        var recording = factory.Create(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null));
        var withMicrophone = factory.Create(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)));
        recording.NoteWorthy += (_, note) => notes.Add(note);
        recording.Start(files, TimeSpan.FromMinutes(47));
        queued.Advance(TimeSpan.FromMinutes(2));
        recording.Pause();
        queued.Advance(TimeSpan.FromMinutes(5));
        queued.Main = FakeRecording.Track(sinceLastSound: TimeSpan.FromSeconds(70), soundWhilePaused: true);
        var paused = recording.GetSnapshot();
        recording.Resume();
        queued.Advance(TimeSpan.FromMinutes(1));
        var microphone = recording.SetMicrophone(false);
        recording.Reopen(Track.Main, "no audio has arrived for 5 seconds");
        queued.Note(TimeSpan.FromMinutes(48), "Resumed at 23:51 UTC");
        var summary = recording.Stop();
        recording.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recording, Is.SameAs(queued));
            Assert.That(queued.Files, Is.EqualTo(files));
            Assert.That(paused, Is.EqualTo(new RecordingSnapshot(TimeSpan.FromMinutes(49), Paused: true,
                FakeRecording.Track(written: TimeSpan.FromMinutes(2), sinceLastSound: TimeSpan.FromSeconds(70), soundWhilePaused: true), Microphone: null)));
            Assert.That(recording.Position, Is.EqualTo(TimeSpan.FromMinutes(50)));
            Assert.That(microphone, Is.Null, "a recording of the class alone has no microphone");
            Assert.That(summary, Is.EqualTo(new RecordingSummary(TimeSpan.FromMinutes(3), queued.MainSegments, queued.MicrophoneSegments, HeardAnything: false)));
            Assert.That(notes, Is.EqualTo(new[] { new TimelineNote(TimeSpan.FromMinutes(48), "Resumed at 23:51 UTC") }));
            Assert.That(queued.Reopened, Is.EqualTo(new[] { (Track.Main, "no audio has arrived for 5 seconds") }));
            Assert.That(queued.Calls, Is.EqualTo(new[] { "Start", "Pause", "Resume", "Microphone off", "Reopen Main", "Stop", "Dispose" }));
            Assert.That(withMicrophone.SetMicrophone(false), Is.False);
            Assert.That(withMicrophone.GetSnapshot().Microphone, Is.Not.Null.And.Property(nameof(TrackSnapshot.Off)).True
                .And.Property(nameof(TrackSnapshot.DeliversContinuously)).True);
            Assert.That(factory.Plans.Select(plan => plan.Mode), Is.EqualTo(new[] { RecordingMode.Class, RecordingMode.ClassAndMicrophone }));
        }
    }

    /// <summary>
    /// In person the stand-in records your microphone as its main track, as the recording it stands in for does: the
    /// checks see the microphone, which hands over audio all the time, as the main track, with no microphone track beside
    /// it, and V switches the main track off and on, answering whether it is on now.
    /// </summary>
    [Test]
    public void In_person_the_stand_in_recording_has_the_microphone_as_its_main_track()
    {
        var factory = new FakeRecordingFactory();

        var recording = factory.Create(new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice(null)));
        var before = recording.GetSnapshot();
        var switchedOff = recording.SetMicrophone(false);
        var whileOff = recording.GetSnapshot();
        var switchedOn = recording.SetMicrophone(true);
        var after = recording.GetSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.Main, Is.EqualTo(FakeRecording.Track("Microphone Array", deliversContinuously: true)));
            Assert.That(before.Microphone, Is.Null, "in person there is no microphone track beside the main one");
            Assert.That(switchedOff, Is.False);
            Assert.That(whileOff.Main.Off, Is.True, "V switches the main track off in person");
            Assert.That(whileOff.Microphone, Is.Null);
            Assert.That(switchedOn, Is.True);
            Assert.That(after.Main.Off, Is.False);
        }
    }

    /// <summary>
    /// A record request run through the command runner of the app as AppHost puts it together reaches the starter whole,
    /// with where it was started from, in a scope of its own, and the starter's exit code comes back: the source, the
    /// folder, the minutes, the choice to continue and whether to open the results all travel with it. The starter is a
    /// stand-in, so nothing records and no recorder is started; ClassScribe's own folder is the test's.
    /// </summary>
    [Test]
    public async Task A_record_request_reaches_the_starter_whole_in_a_scope_of_its_own()
    {
        File.WriteAllText(_folder.File("appsettings.json"), "{}");
        var played = new PlayedRuns { StartExitCode = 130 };
        using var app = AppHost.Build(new SettingsPlaces(_folder.Path, userFolder: null), "dummy-key-for-a-local-test", services => services
            .AddSingleton(_runtime.Folder)
            .AddScoped<IRecordStarter>(provider => new FakeRecordStarter(played, provider)));
        var runner = app.Services.GetRequiredService<ICommandRunner>();
        var request = new RecordRequest(RunIds.New(DateTimeOffset.UtcNow), "Kalam", Transcribe: true, new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), null),
            @"D:\Classes", ContinueChoice.Continue, "Kalam_2026-10-15_22-10-00Z", Minutes: 90.5, Open: false);

        var exitCode = await runner.RecordAsync(request, StartOrigin.CommandLine);

        Assert.That(exitCode, Is.EqualTo(130));
        Assert.That(played.Started.Select(run => (run.Request, run.Origin)), Is.EqualTo(new[] { (request, StartOrigin.CommandLine) }));
        Assert.That(played.Started.Single().Services, Is.Not.SameAs(app.Services), "in a scope of its own");
    }
}
