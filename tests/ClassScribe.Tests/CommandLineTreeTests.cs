using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The command line (section B of the design): each form runs the command it names with what was given; each mistake is
/// named in plain words, all of them in one run, with that command's help and exit code 2, before anything runs and
/// without a question; and every command has its help. The commands are mocked; the menu is the real one over a scripted
/// console.
/// </summary>
[TestFixture]
public class CommandLineTreeTests
{
    /// <summary>When the tests' clock says the command line was given: 23:00:02 UTC on 15 October 2026.</summary>
    private static readonly DateTimeOffset Given = new(2026, 10, 15, 23, 0, 2, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private Mock<ICommandRunner> _commands = null!;
    private List<(RecordRequest Request, StartOrigin Origin)> _recorded = null!;
    private List<MicCheckRequest> _checked = null!;
    private FakeConsoleUi _ui = null!;
    private TestCommandLine _line = null!;

    /// <summary>The folder recordings go to in these tests, given as --output.</summary>
    private string Classes => _folder.File("Classes");

    /// <summary>Creates the folders, the mocked commands, which succeed, and the command line over a console a person can type into.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _recorded = [];
        _checked = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Callback((RecordRequest request, StartOrigin origin) => _recorded.Add((request, origin)))
            .ReturnsAsync(0);
        _commands.Setup(c => c.TranscribeAsync(It.IsAny<TranscribeRequest>())).ReturnsAsync(0);
        _commands.Setup(c => c.RebuildAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        _commands.Setup(c => c.ListAsync(It.IsAny<string?>())).ReturnsAsync(0);
        _commands.Setup(c => c.DevicesAsync()).ReturnsAsync(0);
        _commands.Setup(c => c.AttachAsync(It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(0);
        _commands.Setup(c => c.ChooseMicrophoneAsync()).ReturnsAsync(0);
        _commands.Setup(c => c.MicCheckAsync(It.IsAny<MicCheckRequest>()))
            .Callback((MicCheckRequest request) => _checked.Add(request))
            .ReturnsAsync(0);
        CreateLine();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Puts the command line together again, over a console a person can, or cannot, type into.</summary>
    private void CreateLine(bool interactive = true)
    {
        _ui = new FakeConsoleUi { IsInteractive = interactive };
        _line = new TestCommandLine(_commands.Object, _ui, new TestSettings(_settingsFolder.Path), _folder.Path, new ManualTimeProvider(Given));
    }

    /// <summary>Runs the command line, failing the test rather than waiting if anything waits for a person to answer.</summary>
    private Task<int> RunAsync(params string[] args) => _line.RunAsync(args).WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>What a run that records the class from <paramref name="source"/> asks for, without its run id, which is new each time.</summary>
    private static RecordRequest Expected(string name, bool transcribe, ClassSource source, string outputRoot,
        ContinueChoice continueChoice = ContinueChoice.Ask, double? minutes = null, bool open = true) =>
        new("", name, transcribe, new SourcePlan(RecordingMode.Class, source, Microphone: null), outputRoot, continueChoice, ContinueBaseName: null, minutes, open);

    /// <summary>The source of the one recording asked for.</summary>
    private SourcePlan OnlySource() => OnlyRecording().Source;

    /// <summary>The plan that records the class from <paramref name="source"/> and the microphone <paramref name="microphone"/>, which follows <paramref name="follows"/>.</summary>
    private static SourcePlan WithMicrophone(ClassSource source, string? microphone = null, FollowRule? follows = null) =>
        new(RecordingMode.ClassAndMicrophone, source, new MicrophoneChoice(microphone, follows));

    /// <summary>The plan that records the microphone <paramref name="microphone"/> alone, in person.</summary>
    private static SourcePlan InPerson(string? microphone = null) => new(RecordingMode.Microphone, null, new MicrophoneChoice(microphone));

    /// <summary>What transcribing <paramref name="path"/> from the command line asks for, with the options given.</summary>
    private static TranscribeRequest FromCommandLine(string path, bool open = true, bool again = false) =>
        new(path, Open: open, Again: again, FromCommandLine: true);

    /// <summary>The one recording asked for, without its run id; the test fails when there was not exactly one.</summary>
    private RecordRequest OnlyRecording()
    {
        Assert.That(_recorded, Has.Count.EqualTo(1), "exactly one recording was asked for");
        Assert.That(_recorded[0].Origin, Is.EqualTo(StartOrigin.CommandLine));
        return _recorded[0].Request with { RunId = "" };
    }

    /// <summary>Asserts that the command line was refused as a mistake: exit code 2, nothing run, nothing asked, and the help written.</summary>
    private void AssertRefused(int exitCode)
    {
        Assert.That(exitCode, Is.EqualTo(2));
        _commands.VerifyNoOtherCalls();
        Assert.That(_ui.Screen, Is.Empty, "nothing was asked or shown through the console");
        Assert.That(_line.Output.ToString(), Does.Contain("Usage:"), "the help is written after the mistakes");
    }

    // ---- Every form of the command line ----

    /// <summary>record and record-only take a name, a source and --output, and record as they say, from the command line.</summary>
    [TestCase("record", true)]
    [TestCase("record-only", false)]
    public async Task The_record_commands_record_the_name_from_the_source_into_the_folder(string command, bool transcribe)
    {
        Assert.That(await RunAsync(command, "Kalam", "--device", "auto", "--output", Classes), Is.EqualTo(0));

        Assert.That(OnlyRecording(), Is.EqualTo(Expected("Kalam", transcribe, new ClassSource.Automatic(), Classes)));
    }

    /// <summary>--device with part of a name records that device, --device auto the device the class plays on, and --app one app.</summary>
    [TestCase("--device", "Pixel Buds")]
    [TestCase("--device", "auto")]
    [TestCase("--device", "AUTO")]
    [TestCase("--app", "chrome")]
    public async Task The_source_is_the_one_given(string option, string value)
    {
        await RunAsync("record-only", "Kalam", option, value, "--output", Classes);

        ClassSource expected = option == "--app" ? new ClassSource.App(value)
            : value.Equals("auto", StringComparison.OrdinalIgnoreCase) ? new ClassSource.Automatic()
            : new ClassSource.Device(value);
        Assert.That(OnlyRecording().Source, Is.EqualTo(new SourcePlan(RecordingMode.Class, expected, Microphone: null)));
    }

    /// <summary>An app is named by its process, as Windows names it: chrome.exe, as Task Manager shows it, means chrome.</summary>
    [TestCase("chrome.exe", "chrome")]
    [TestCase("Chrome.EXE", "Chrome")]
    public async Task An_app_named_with_exe_is_its_process(string given, string process)
    {
        await RunAsync("record", "Kalam", "--app", given, "--output", Classes);

        Assert.That(OnlyRecording().Source.Class, Is.EqualTo(new ClassSource.App(process)));
    }

    /// <summary>
    /// --in-person is a source of its own: the microphone alone, for a class in the room, with nothing of the class. Part of
    /// a microphone's name after it picks that microphone; nothing after it leaves the microphone to Recording:Microphone,
    /// and when that is empty too, to Windows' default microphone.
    /// </summary>
    [Test]
    public async Task In_person_records_the_microphone_alone_with_or_without_a_name()
    {
        Assert.That(await RunAsync("record", "Kalam", "--in-person", "--output", Classes), Is.EqualTo(0));
        Assert.That(await RunAsync("record-only", "Kalam", "--in-person", "OBSBOT", "--output", Classes), Is.EqualTo(0));

        Assert.That(_recorded.Select(run => (run.Request.Transcribe, run.Request.Source)), Is.EqualTo(new[]
        {
            (true, InPerson()),
            (false, InPerson("OBSBOT"))
        }));
    }

    /// <summary>
    /// --mic adds the microphone to the class, from --device or --app, as a second track: with nothing after it, the
    /// microphone is Recording:Microphone's, or Windows' default; with part of a name, that microphone. Without --mic-follows,
    /// which app the microphone follows is left to the run: the app recorded, or else Recording:MicrophoneFollows.
    /// </summary>
    [Test]
    public async Task Mic_adds_the_microphone_to_the_device_or_the_app()
    {
        await RunAsync("record", "Kalam", "--device", "auto", "--mic", "--output", Classes);
        await RunAsync("record", "Kalam", "--app", "chrome", "--mic", "Microphone Array", "--output", Classes);
        await RunAsync("record", "Kalam", "--mic", "--device", "Pixel Buds", "--output", Classes);

        Assert.That(_recorded.Select(run => run.Request.Source), Is.EqualTo(new[]
        {
            WithMicrophone(new ClassSource.Automatic()),
            WithMicrophone(new ClassSource.App("chrome"), "Microphone Array"),
            WithMicrophone(new ClassSource.Device("Pixel Buds"))
        }));
    }

    /// <summary>
    /// --mic-follows names the app whose microphone use the microphone follows: part of its process name, chrome.exe
    /// meaning chrome, or auto for any app that holds the microphone, or none to record whenever V is on; each in any case.
    /// </summary>
    [TestCase("chrome", "named", "chrome")]
    [TestCase("Chrome.exe", "named", "Chrome")]
    [TestCase("ms-teams", "named", "ms-teams")]
    [TestCase("auto", "any", null)]
    [TestCase("AUTO", "any", null)]
    [TestCase("none", "none", null)]
    [TestCase("None", "none", null)]
    public async Task Mic_follows_names_the_app_the_microphone_follows(string given, string kind, string? namePart)
    {
        Assert.That(await RunAsync("record", "Kalam", "--app", "chrome", "--mic", "--mic-follows", given, "--output", Classes), Is.EqualTo(0));

        FollowRule expected = kind switch
        {
            "named" => new FollowRule.Named(namePart!),
            "any" => new FollowRule.Any(),
            _ => new FollowRule.None()
        };
        Assert.That(OnlySource(), Is.EqualTo(WithMicrophone(new ClassSource.App("chrome"), follows: expected)));
    }

    /// <summary>--mic-follows means nothing without --mic, which is said; the microphone is not recorded by it alone.</summary>
    [Test]
    public async Task Mic_follows_without_mic_is_refused()
    {
        AssertRefused(await RunAsync("record", "Kalam", "--device", "auto", "--mic-follows", "chrome", "--output", Classes));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--mic-follows goes with --mic: it names the app whose microphone use your microphone follows" }));
    }

    /// <summary>In person there is no class app to follow, so --mic-follows with --in-person is refused.</summary>
    [Test]
    public async Task Mic_follows_with_in_person_is_refused()
    {
        AssertRefused(await RunAsync("record", "Kalam", "--in-person", "--mic-follows", "chrome", "--output", Classes));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--mic-follows cannot go with --in-person: in person there is no class app" }));
    }

    /// <summary>
    /// --mic-follows needs part of an app's name, or auto or none: an empty value names no app, and any part of ClassScribe's
    /// own name, whose own recording holds the microphone for the whole class, would follow ClassScribe and record everything.
    /// </summary>
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("ClassScribe")]
    [TestCase("classscribe.exe")]
    [TestCase("Scribe")]
    public async Task Mic_follows_needs_part_of_an_apps_name(string given)
    {
        AssertRefused(await RunAsync("record", "Kalam", "--app", "chrome", "--mic", "--mic-follows", given, "--output", Classes));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--mic-follows needs part of an app's name, such as chrome, or auto or none" }));
    }

    /// <summary>--mic-follows left without its value, so that it took the next option, says which.</summary>
    [Test]
    public async Task Mic_follows_that_took_an_option_says_which()
    {
        AssertRefused(await RunAsync("record", "Kalam", "--app", "chrome", "--mic", "--mic-follows", "--output", Classes));

        Assert.That(_line.Mistakes, Does.Contain("--mic-follows needs part of an app's name, such as chrome, or auto or none; it took the option --output instead"));
    }

    /// <summary>With --in-person the microphone is already the source, so --mic beside it is refused.</summary>
    [Test]
    public async Task Mic_with_in_person_is_refused()
    {
        AssertRefused(await RunAsync("record", "Kalam", "--in-person", "--mic", "--output", Classes));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--mic adds the microphone to --device or --app; with --in-person the microphone is already the source" }));
    }

    /// <summary>
    /// --in-person and --mic may take a microphone's name, so a recording's name right after them is taken as the
    /// microphone's; the message says so, so that the name is moved to right after the word record.
    /// </summary>
    [TestCase(new[] { "record", "--in-person", "Kalam", "--output", "{0}" }, "--in-person")]
    [TestCase(new[] { "record", "--device", "auto", "--mic", "Kalam", "--output", "{0}" }, "--mic")]
    public async Task A_name_taken_as_the_microphones_name_is_pointed_out(string[] args, string taker)
    {
        AssertRefused(await RunAsync([.. args.Select(arg => string.Format(arg, Classes))]));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { $"record needs a name, right after the word record: ClassScribe record \"Kalam\" ... ({taker} took \"Kalam\" as the microphone's name)" }));
    }

    /// <summary>--minutes, --no-open, --continue and --new travel with the request; without them it records until stopped, opens what the settings say and asks.</summary>
    [Test]
    public async Task The_record_options_travel_with_the_request()
    {
        await RunAsync("record", "Kalam", "--app", "chrome", "--output", Classes, "--minutes", "90", "--no-open", "--continue");
        await RunAsync("record-only", "Fiqh", "--device", "Pixel Buds", "--output", Classes, "--new", "--minutes", "1.5");

        Assert.That(_recorded.Select(run => run.Request with { RunId = "" }), Is.EqualTo(new[]
        {
            Expected("Kalam", true, new ClassSource.App("chrome"), Classes, ContinueChoice.Continue, minutes: 90, open: false),
            Expected("Fiqh", false, new ClassSource.Device("Pixel Buds"), Classes, ContinueChoice.New, minutes: 1.5)
        }));
    }

    /// <summary>A flag given as false counts as not given, so --continue false with --new is a new recording and no mistake.</summary>
    [Test]
    public async Task A_flag_given_as_false_counts_as_not_given()
    {
        Assert.That(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--continue", "false", "--new", "--no-open", "false"), Is.EqualTo(0));

        Assert.That(OnlyRecording(), Is.EqualTo(Expected("Kalam", true, new ClassSource.Automatic(), Classes, ContinueChoice.New)));
    }

    /// <summary>The run gets a well-formed id stamped with the time the command line was given.</summary>
    [Test]
    public async Task The_run_id_names_the_time_it_was_given()
    {
        await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes);

        var runId = _recorded.Single().Request.RunId;
        Assert.That(RunIds.IsWellFormed(runId), Is.True, runId);
        Assert.That(runId, Does.StartWith("20261015-230002Z-"));
    }

    /// <summary>--output is taken as a full path, so a folder given relative to where the command was typed stays that folder.</summary>
    [Test]
    public async Task The_output_folder_becomes_a_full_path()
    {
        await RunAsync("record", "Kalam", "--device", "auto", "--output", "Classes");

        Assert.That(OnlyRecording().OutputRoot, Is.EqualTo(Path.GetFullPath("Classes")));
    }

    /// <summary>The name is cleaned for use as a folder's name, as the menu's question cleans it; a quote inside it goes too.</summary>
    [TestCase("Kalam: lesson  1?", "Kalam lesson 1")]
    [TestCase("Reading \"Kitāb al-Tawḥīd\"", "Reading Kitāb al-Tawḥīd")]
    [TestCase("Uṣūl al-dīn", "Uṣūl al-dīn")]
    public async Task The_name_is_cleaned_for_a_folder(string given, string name)
    {
        Assert.That(await RunAsync("record", given, "--device", "auto", "--output", Classes), Is.EqualTo(0));

        Assert.That(OnlyRecording().Name, Is.EqualTo(name));
    }

    /// <summary>transcribe takes a path, --no-open and --again, in any order, as a request from the command line.</summary>
    [TestCase(new[] { "transcribe", @"C:\Classes\Kalam" }, true, false)]
    [TestCase(new[] { "transcribe", @"C:\Classes\Kalam", "--no-open" }, false, false)]
    [TestCase(new[] { "transcribe", "--again", @"C:\Classes\Kalam", "--no-open" }, false, true)]
    public async Task Transcribe_takes_its_path_and_options(string[] args, bool open, bool again)
    {
        Assert.That(await RunAsync(args), Is.EqualTo(0));

        var request = FromCommandLine(@"C:\Classes\Kalam", open, again);
        _commands.Verify(c => c.TranscribeAsync(request), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// transcribe passes the folder --output names on, as a full path, so a folder given relative to where the command was
    /// typed stays that folder. Whether it is used is the transcribe command's to say: a recording's transcript stays beside
    /// the recording.
    /// </summary>
    [Test]
    public async Task Transcribe_passes_its_output_folder_on_as_a_full_path()
    {
        Assert.That(await RunAsync("transcribe", @"C:\Classes\Kalam", "--output", "Classes", "--no-open"), Is.EqualTo(0));

        var request = new TranscribeRequest(@"C:\Classes\Kalam", Path.GetFullPath("Classes"), Open: false, FromCommandLine: true);
        _commands.Verify(c => c.TranscribeAsync(request), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The help of transcribe says what --output is for: the transcript of a file that is not a recording's goes into a
    /// folder of its name inside it, and a recording's transcript stays beside the recording. The help may be wrapped to
    /// the window's width, so its spaces and line breaks are read as single spaces.
    /// </summary>
    [Test]
    public async Task The_help_of_transcribe_says_what_its_output_folder_is_for()
    {
        Assert.That(await RunAsync("transcribe", "--help"), Is.EqualTo(0));

        var help = Regex.Replace(_line.Output.ToString(), @"\s+", " ");
        Assert.That(help, Does.Contain(
            "--output <folder> The folder for the transcript of a file that is not a recording's, such as \"D:\\Classes\": it gets a folder of its name inside it. A recording's transcript goes beside the recording, and this is not used."));
    }

    /// <summary>rebuild takes a path, and opens the new transcript at the end unless --no-open says otherwise.</summary>
    [TestCase(new[] { "rebuild", @"C:\Classes\Kalam" }, true)]
    [TestCase(new[] { "rebuild", "--no-open", @"C:\Classes\Kalam" }, false)]
    public async Task Rebuild_takes_its_path(string[] args, bool open)
    {
        Assert.That(await RunAsync(args), Is.EqualTo(0));

        _commands.Verify(c => c.RebuildAsync(@"C:\Classes\Kalam", open), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>list lists the recordings folder of the settings, or the one --output names, as a full path.</summary>
    [Test]
    public async Task List_lists_the_folder_given_or_the_settings()
    {
        await RunAsync("list");
        await RunAsync("list", "--output", "Classes");

        _commands.Verify(c => c.ListAsync(null), Times.Once);
        _commands.Verify(c => c.ListAsync(Path.GetFullPath("Classes")), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>devices lists the devices and apps.</summary>
    [Test]
    public async Task Devices_lists_the_devices()
    {
        Assert.That(await RunAsync("devices"), Is.EqualTo(0));

        _commands.Verify(c => c.DevicesAsync(), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// mic-check watches what the programs do with the microphone until it is stopped, or for --seconds, writing its log
    /// under the recordings folder, or under --log-folder as a full path.
    /// </summary>
    [Test]
    public async Task Mic_check_watches_until_stopped_or_for_the_seconds_given()
    {
        Assert.That(await RunAsync("mic-check"), Is.EqualTo(0));
        Assert.That(await RunAsync("mic-check", "--seconds", "600", "--log-folder", "Classes"), Is.EqualTo(0));

        Assert.That(_checked, Is.EqualTo(new[]
        {
            new MicCheckRequest(StopAfter: null, LogFolder: null),
            new MicCheckRequest(TimeSpan.FromSeconds(600), Path.GetFullPath("Classes"))
        }));
        _commands.Verify(c => c.MicCheckAsync(It.IsAny<MicCheckRequest>()), Times.Exactly(2));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>--seconds takes a whole number of seconds from 1 to a day; anything else is refused with the reason.</summary>
    [TestCase("0", "--seconds takes from 1 to 86400 seconds (a day)")]
    [TestCase("86401", "--seconds takes from 1 to 86400 seconds (a day)")]
    [TestCase("99999999999", "--seconds takes from 1 to 86400 seconds (a day)")]
    [TestCase("1.5", "--seconds takes a whole number of seconds, such as 600")]
    [TestCase("abc", "--seconds takes a whole number of seconds, such as 600")]
    [TestCase("", "--seconds takes a whole number of seconds, such as 600")]
    public async Task Mic_check_seconds_that_are_not_a_whole_number_in_range_are_refused(string given, string mistake)
    {
        AssertRefused(await RunAsync("mic-check", "--seconds", given));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { mistake }));
    }

    /// <summary>--seconds without a number is refused the same way.</summary>
    [Test]
    public async Task Mic_check_seconds_without_a_number_are_refused()
    {
        AssertRefused(await RunAsync("mic-check", "--seconds"));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--seconds takes a whole number of seconds, such as 600" }));
    }

    /// <summary>--log-folder is checked as --output is: a value, no character Windows refuses in a path, and not a file.</summary>
    [Test]
    public async Task Mic_check_log_folder_that_cannot_be_a_folder_is_refused()
    {
        var file = _folder.File("notes.txt");
        File.WriteAllText(file, "notes");

        AssertRefused(await RunAsync("mic-check", "--log-folder"));
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--log-folder needs a folder after it, such as \"D:\\Classes\"" }));

        CreateLine();
        AssertRefused(await RunAsync("mic-check", "--log-folder", @"D:\Classes|Logs"));
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--log-folder takes a folder, such as \"D:\\Classes\"; \"D:\\Classes|Logs\" has a character Windows does not allow in a path: < > \" | ? *" }));

        CreateLine();
        AssertRefused(await RunAsync("mic-check", "--log-folder", file));
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { $"--log-folder names a file, not a folder: {file}" }));
    }

    /// <summary>The hidden attach shows the recording in progress, or, with --alert and a run id, that run in a window the recorder opened.</summary>
    [Test]
    public async Task Attach_shows_the_recording_in_progress()
    {
        await RunAsync("attach");
        await RunAsync("attach", "--alert", "20261015-230002Z-1f3a9c07");

        _commands.Verify(c => c.AttachAsync(null, false), Times.Once);
        _commands.Verify(c => c.AttachAsync("20261015-230002Z-1f3a9c07", true), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The alert window's command line, in the words the process starter writes it with (<see cref="ProcessArguments"/>),
    /// shows the run it names in alert mode: the starter and the command line agree on the words.
    /// </summary>
    [Test]
    public async Task The_alert_windows_command_line_shows_its_run_in_alert_mode()
    {
        var runId = RunIds.New(Given);

        Assert.That(await RunAsync(ProcessArguments.Attach, ProcessArguments.Alert, runId), Is.EqualTo(0));

        _commands.Verify(c => c.AttachAsync(runId, true), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A run id that is not one is a mistake: it would name a file, a pipe and an event.</summary>
    [Test]
    public async Task Attach_refuses_a_run_id_that_is_not_one()
    {
        AssertRefused(await RunAsync("attach", "--alert", @"..\..\Windows"));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--alert takes a run id, such as 20261015-230002Z-1f3a9c07" }));
    }

    /// <summary>A file or folder alone on the command line, as when it is dropped on the exe, is transcribed from the command line.</summary>
    [Test]
    public async Task A_lone_path_is_transcribed()
    {
        var video = _folder.File("Lecture 5.mp4");
        File.WriteAllBytes(video, [1, 2, 3]);

        await RunAsync(video);
        await RunAsync(_folder.Path);

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(video, true, false)), Times.Once);
        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(_folder.Path, true, false)), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A first word in square brackets is a path like any other, never something the parser takes for itself: a class
    /// folder named "[Kalam]", typed in the folder that holds it, is transcribed, and one that is not there is reported.
    /// </summary>
    [Test]
    public async Task A_lone_word_in_square_brackets_is_a_path()
    {
        Directory.CreateDirectory(_folder.File("[Kalam]"));
        var before = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _folder.Path;
            Assert.That(await RunAsync("[Kalam]"), Is.EqualTo(0));
            _commands.Verify(c => c.TranscribeAsync(FromCommandLine(@".\[Kalam]", true, false)), Times.Once);

            CreateLine();
            AssertRefused(await RunAsync("[Fiqh]"));
            Assert.That(_line.Mistakes, Is.EqualTo(new[] { @"Nothing found at .\[Fiqh]" }));
        }
        finally
        {
            Environment.CurrentDirectory = before;
        }
    }

    /// <summary>Started without arguments by someone at the keyboard, the command line shows the menu.</summary>
    [Test]
    public async Task Without_arguments_the_menu_is_shown()
    {
        _ui.Type('q');

        Assert.That(await RunAsync(), Is.EqualTo(0));
        Assert.That(_ui.Screen, Does.Contain("Record only"));
    }

    /// <summary>Without a keyboard a menu is useless: the reason and the usage are written, the exit code is 2, and nothing runs.</summary>
    [Test]
    public async Task Without_arguments_or_a_keyboard_the_usage_is_shown()
    {
        CreateLine(interactive: false);

        AssertRefused(await RunAsync());
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "ClassScribe shows its menu only when someone can type into its window; give it a command instead, such as one of those below." }));
        Assert.That(_line.Output.ToString(), Does.Contain("record-only").And.Contain("transcribe"));
    }

    // ---- Mistakes: named, all at once, with that command's help and exit code 2, and nothing asked ----

    /// <summary>
    /// record without its name, its source and --output names all three in one run, in the order they go, with record's
    /// help, exit code 2 and no question, even with someone at the keyboard.
    /// </summary>
    [TestCase("record")]
    [TestCase("record-only")]
    public async Task Each_missing_piece_is_named_in_one_run(string command)
    {
        _ui.TypeLine("Kalam");

        AssertRefused(await RunAsync(command));

        Assert.That(_line.Mistakes, Is.EqualTo(new[]
        {
            $"{command} needs a name, right after the word {command}: ClassScribe {command} \"Kalam\" ...",
            $"{command} needs a source: --device \"<part of its name>\", --device auto, --app <process> or --in-person",
            $"{command} needs --output \"<folder>\", the folder the recording goes to"
        }));
        Assert.That(_line.Output.ToString(), Does.Contain("--device").And.Contain("--minutes").And.Not.Contain("Commands:"), "the help is record's own");
    }

    /// <summary>Each piece, missing on its own, is named on its own.</summary>
    [TestCase(new[] { "record", "--device", "auto", "--output", @"D:\Classes" }, "record needs a name, right after the word record: ClassScribe record \"Kalam\" ...")]
    [TestCase(new[] { "record", "Kalam", "--output", @"D:\Classes" }, "record needs a source: --device \"<part of its name>\", --device auto, --app <process> or --in-person")]
    [TestCase(new[] { "record", "Kalam", "--app", "chrome" }, "record needs --output \"<folder>\", the folder the recording goes to")]
    [TestCase(new[] { "record", "Kalam", "--in-person" }, "record needs --output \"<folder>\", the folder the recording goes to")]
    public async Task A_single_missing_piece_is_named(string[] args, string mistake)
    {
        AssertRefused(await RunAsync(args));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { mistake }));
    }

    /// <summary>One source at a time: --device, --app and --in-person are refused together, in any pair and all three.</summary>
    [TestCase(new[] { "--device", "auto", "--app", "chrome" }, "--device and --app")]
    [TestCase(new[] { "--device", "auto", "--in-person" }, "--device and --in-person")]
    [TestCase(new[] { "--app", "chrome", "--in-person" }, "--app and --in-person")]
    [TestCase(new[] { "--in-person", "--device", "auto", "--app", "chrome" }, "--device, --app and --in-person")]
    public async Task Two_sources_are_refused(string[] sources, string given)
    {
        AssertRefused(await RunAsync(["record", "Kalam", .. sources, "--output", Classes]));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { $"record takes one source; you gave {given}" }));
    }

    /// <summary>
    /// --minutes takes digits with at most one dot, read the same way whatever the computer's language, more than 0 and at
    /// most 1440: a comma is refused rather than read as a thousands separator (1,5 would otherwise be 15).
    /// </summary>
    [TestCase("1.5", 1.5)]
    [TestCase("90", 90.0)]
    [TestCase("1440", 1440.0)]
    [TestCase(".5", 0.5)]
    [TestCase("+20", 20.0)]
    public async Task Minutes_written_with_a_dot_are_taken(string given, double minutes)
    {
        Assert.That(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--minutes", given), Is.EqualTo(0));

        Assert.That(OnlyRecording().Minutes, Is.EqualTo(minutes));
    }

    /// <summary>Anything but a number written with a dot is refused with the reason.</summary>
    [TestCase("1,5")]
    [TestCase("1.2.3")]
    [TestCase("abc")]
    [TestCase("1e3")]
    [TestCase("NaN")]
    [TestCase("Infinity")]
    [TestCase("٣")]
    public async Task Minutes_not_written_with_a_dot_are_refused(string given)
    {
        AssertRefused(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--minutes", given));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--minutes takes a number of minutes written with a dot, such as 1.5" }));
    }

    /// <summary>A number of minutes outside the range is refused with the range.</summary>
    [TestCase("0")]
    [TestCase("0.0")]
    [TestCase("-5")]
    [TestCase("1441")]
    [TestCase("1440.5")]
    public async Task Minutes_out_of_range_are_refused(string given)
    {
        AssertRefused(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--minutes", given));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--minutes takes more than 0 and at most 1440 minutes (24 hours)" }));
    }

    /// <summary>--minutes without a number is refused the same way.</summary>
    [Test]
    public async Task Minutes_without_a_number_are_refused()
    {
        AssertRefused(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--minutes"));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--minutes takes a number of minutes written with a dot, such as 1.5" }));
    }

    /// <summary>
    /// On a computer set to German, which writes one and a half as 1,5 and puts a dot between thousands, --minutes 1.5 is
    /// still one and a half minutes: read the computer's way, it would be 15.
    /// </summary>
    [Test]
    [SetCulture("de-DE")]
    public async Task Minutes_with_a_dot_mean_the_same_whatever_the_computers_language()
    {
        Assert.That(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--minutes", "1.5"), Is.EqualTo(0));

        Assert.That(OnlyRecording().Minutes, Is.EqualTo(1.5));
    }

    /// <summary>
    /// On a computer set to German, --minutes 1,5 is refused with the reason, as everywhere else, although German writes one
    /// and a half that way: the same command line means the same on every computer.
    /// </summary>
    [Test]
    [SetCulture("de-DE")]
    public async Task Minutes_with_a_comma_are_refused_whatever_the_computers_language()
    {
        AssertRefused(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--minutes", "1,5"));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--minutes takes a number of minutes written with a dot, such as 1.5" }));
    }

    /// <summary>A bad --minutes is named in the same run as the missing pieces, so one correction is enough.</summary>
    [Test]
    public async Task A_bad_value_is_named_with_the_missing_pieces()
    {
        AssertRefused(await RunAsync("record", "--minutes", "1,5"));

        Assert.That(_line.Mistakes, Is.EqualTo(new[]
        {
            "record needs a name, right after the word record: ClassScribe record \"Kalam\" ...",
            "record needs a source: --device \"<part of its name>\", --device auto, --app <process> or --in-person",
            "record needs --output \"<folder>\", the folder the recording goes to",
            "--minutes takes a number of minutes written with a dot, such as 1.5"
        }));
    }

    /// <summary>
    /// When an option whose value may be left out took the word meant as the name, the message says so: here a recording
    /// named True, taken by --new as its value.
    /// </summary>
    [Test]
    public async Task A_name_taken_by_an_option_is_pointed_out()
    {
        AssertRefused(await RunAsync("record", "--new", "True", "--device", "auto", "--output", Classes));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "record needs a name, right after the word record: ClassScribe record \"Kalam\" ... (--new took \"True\" as its value)" }));
    }

    /// <summary>--continue and --new contradict each other.</summary>
    [Test]
    public async Task Continue_and_new_together_are_refused()
    {
        AssertRefused(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes, "--continue", "--new"));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--continue and --new cannot go together" }));
    }

    /// <summary>An option left without its value, or given an empty one, is named; one that took the next option instead says which.</summary>
    [TestCase(new[] { "record", "Kalam", "--device", "", "--output", "{0}" }, "--device needs part of a device's name after it, or auto")]
    [TestCase(new[] { "record", "Kalam", "--app", "", "--output", "{0}" }, "--app needs an app's process name after it, such as chrome")]
    [TestCase(new[] { "record", "Kalam", "--device", "auto", "--output", "" }, "--output needs a folder after it, such as \"D:\\Classes\"")]
    [TestCase(new[] { "record", "Kalam", "--app", "--output", "{0}" }, "--app needs an app's process name after it, such as chrome; it took the option --output instead")]
    public async Task An_option_without_its_value_is_named(string[] args, string mistake)
    {
        AssertRefused(await RunAsync([.. args.Select(arg => string.Format(arg, Classes))]));

        Assert.That(_line.Mistakes, Does.Contain(mistake));
    }

    /// <summary>A folder Windows could not make, or a file where the folder should be, is refused before anything starts.</summary>
    [Test]
    public async Task An_output_that_cannot_be_a_folder_is_refused()
    {
        var file = _folder.File("notes.txt");
        File.WriteAllText(file, "notes");

        AssertRefused(await RunAsync("record", "Kalam", "--device", "auto", "--output", @"D:\Classes|Kalam"));
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "--output takes a folder, such as \"D:\\Classes\"; \"D:\\Classes|Kalam\" has a character Windows does not allow in a path: < > \" | ? *" }));

        CreateLine();
        AssertRefused(await RunAsync("list", "--output", file));
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { $"--output names a file, not a folder: {file}" }));
    }

    /// <summary>A name with nothing Windows allows in a folder's name cannot name a recording.</summary>
    [Test]
    public async Task A_name_that_cannot_name_a_folder_is_refused()
    {
        AssertRefused(await RunAsync("record", "???", "--device", "auto", "--output", Classes));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "record cannot use \"???\" as a name: nothing is left of it once the characters Windows does not allow in a folder's name are taken out" }));
    }

    /// <summary>transcribe and rebuild without a path say what the path may be.</summary>
    [TestCase("transcribe", "transcribe needs a path: a recording's folder, one of its files, or any audio or video file, such as ClassScribe transcribe \"D:\\Classes\\Kalam\"")]
    [TestCase("rebuild", "rebuild needs a path: a recording's folder, one of its files, or an answer from ElevenLabs saved as .json, such as ClassScribe rebuild \"D:\\Classes\\Kalam\"")]
    public async Task A_missing_path_is_named(string command, string mistake)
    {
        AssertRefused(await RunAsync(command, "--no-open"));

        Assert.That(_line.Mistakes, Is.EqualTo(new[] { mistake }));
    }

    /// <summary>
    /// A word that is neither a command nor a file or folder is a mistake, with the usage and exit code 2; so is a word
    /// too many, or an option the command does not take.
    /// </summary>
    [TestCase(new[] { "frobnicate" }, "Nothing found at frobnicate")]
    [TestCase(new[] { "record", "Kalam", "extra", "--device", "auto", "--output", @"D:\Classes" }, "Unrecognized command or argument 'extra'.")]
    [TestCase(new[] { "transcribe", @"C:\Classes\Kalam", @"C:\Classes\Fiqh" }, @"Unrecognized command or argument 'C:\Classes\Fiqh'.")]
    [TestCase(new[] { "rebuild", @"C:\Classes\Kalam", "--again" }, "Unrecognized command or argument '--again'.")]
    [TestCase(new[] { "devices", "--output", @"D:\Classes" }, "Unrecognized command or argument '--output'.")]
    public async Task An_unknown_word_is_a_mistake(string[] args, string mistake)
    {
        AssertRefused(await RunAsync(args));

        Assert.That(_line.Mistakes, Does.Contain(mistake));
    }

    /// <summary>An argument that starts with @ is a path like any other, not a file of arguments to read.</summary>
    [Test]
    public async Task An_argument_starting_with_at_stays_a_path()
    {
        await RunAsync("transcribe", "@notes.wav");
        _commands.Verify(c => c.TranscribeAsync(FromCommandLine("@notes.wav", true, false)), Times.Once);

        CreateLine();
        AssertRefused(await RunAsync("@notes.wav"));
        Assert.That(_line.Mistakes, Is.EqualTo(new[] { "Nothing found at @notes.wav" }));
    }

    // ---- Tidying: any letter case, a stray quote ----

    /// <summary>Command and option names are taken in any case, as typed by habit; values keep theirs.</summary>
    [Test]
    public async Task Names_are_taken_in_any_case()
    {
        await RunAsync("RECORD-ONLY", "Kalam", "--DEVICE", "Pixel Buds", "--Output", Classes, "--No-Open", "--MINUTES", "45", "--MIC", "--Mic-Follows", "chrome");
        await RunAsync("Transcribe", "--AGAIN", @"C:\Classes\Kalam");
        await RunAsync("REBUILD", @"C:\Classes\Kalam", "--NO-OPEN");
        await RunAsync("List");
        await RunAsync("DEVICES");
        await RunAsync("MIC-CHECK", "--SECONDS", "5");

        Assert.That(OnlyRecording(), Is.EqualTo(Expected("Kalam", false, new ClassSource.Device("Pixel Buds"), Classes, minutes: 45, open: false)
            with { Source = WithMicrophone(new ClassSource.Device("Pixel Buds"), follows: new FollowRule.Named("chrome")) }));
        var again = FromCommandLine(@"C:\Classes\Kalam", again: true);
        _commands.Verify(c => c.TranscribeAsync(again), Times.Once);
        _commands.Verify(c => c.RebuildAsync(@"C:\Classes\Kalam", false), Times.Once);
        _commands.Verify(c => c.ListAsync(null), Times.Once);
        _commands.Verify(c => c.DevicesAsync(), Times.Once);
        _commands.Verify(c => c.MicCheckAsync(new MicCheckRequest(TimeSpan.FromSeconds(5), null)), Times.Once);
    }

    /// <summary>
    /// Windows PowerShell 5.1 hands a quoted path that ends in a backslash over with a stray quote at the end, and joins
    /// what follows it into the same argument: a quote cannot be part of a Windows path, so it goes, and the options after
    /// it count again, with their values.
    /// </summary>
    [Test]
    public async Task A_stray_quote_from_PowerShell_is_taken_off()
    {
        await RunAsync("transcribe", @"C:\My Classes"" --again --no-open");
        await RunAsync("rebuild", @"C:\My Classes""");
        await RunAsync("record-only", "Kalam", "--device", "auto", "--output", @"D:\My Classes""");
        await RunAsync("record", "Fiqh", "--output", @"D:\My Classes"" --app chrome --minutes 90");

        var transcribed = FromCommandLine(@"C:\My Classes", open: false, again: true);
        _commands.Verify(c => c.TranscribeAsync(transcribed), Times.Once);
        _commands.Verify(c => c.RebuildAsync(@"C:\My Classes", true), Times.Once);
        Assert.That(_recorded.Select(run => run.Request with { RunId = "" }), Is.EqualTo(new[]
        {
            Expected("Kalam", false, new ClassSource.Automatic(), @"D:\My Classes"),
            Expected("Fiqh", true, new ClassSource.App("chrome"), @"D:\My Classes", minutes: 90)
        }));
    }

    /// <summary>A stray quote on a lone path goes too, so a folder dropped or tab-completed in PowerShell is still found.</summary>
    [Test]
    public async Task A_lone_path_with_a_stray_quote_is_found()
    {
        await RunAsync(_folder.Path + "\"");

        _commands.Verify(c => c.TranscribeAsync(FromCommandLine(_folder.Path, true, false)), Times.Once);
    }

    // ---- Help ----

    /// <summary>--help on every command, the hidden attach included, writes that command's help with an example and exits 0, whatever else is missing.</summary>
    [TestCase("record")]
    [TestCase("record-only")]
    [TestCase("transcribe")]
    [TestCase("rebuild")]
    [TestCase("list")]
    [TestCase("devices")]
    [TestCase("mic-check")]
    [TestCase("attach")]
    public async Task Every_command_has_its_help(string command)
    {
        Assert.That(await RunAsync(command, "--help"), Is.EqualTo(0));

        Assert.That(_line.Output.ToString(), Does.Contain("Usage:").And.Contain(command).And.Contain("Examples:").And.Contain($"ClassScribe {command}"));
        Assert.That(_line.Errors.ToString(), Is.Empty);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Help is asked for with --help, -h, -? or /?, in any case, and with the word help before a command or alone.</summary>
    [TestCase("record", "-h")]
    [TestCase("record", "-?")]
    [TestCase("record", "/?")]
    [TestCase("record", "--HELP")]
    [TestCase("help", "record")]
    [TestCase("HELP", "record")]
    [TestCase("record", "Kalam", "--minutes", "1,5", "--help")]
    public async Task Help_is_asked_for_in_several_ways(params string[] args)
    {
        Assert.That(await RunAsync(args), Is.EqualTo(0));

        Assert.That(_line.Output.ToString(), Does.Contain("--device").And.Contain("--output"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>The help of record names every option, the microphone's among them, says what is needed, and gives examples, the microphone's among them.</summary>
    [Test]
    public async Task The_help_of_record_names_its_options()
    {
        await RunAsync("record", "--help");

        Assert.That(_line.Output.ToString(),
            Does.Contain("--device").And.Contain("--app").And.Contain("--in-person").And.Contain("--mic").And.Contain("--mic-follows")
                .And.Contain("--output").And.Contain("--minutes").And.Contain("--no-open").And.Contain("--continue").And.Contain("--new")
                .And.Contain("ClassScribe record \"Kalam\" --device auto --output \"D:\\Classes\"")
                .And.Contain("ClassScribe record \"Kalam\" --app chrome --mic --mic-follows chrome --output \"D:\\Classes\"")
                .And.Contain("ClassScribe record \"Kalam\" --in-person --output \"D:\\Classes\""));
    }

    /// <summary>The help of mic-check says what it does, that it opens no microphone, and gives examples with its options.</summary>
    [Test]
    public async Task The_help_of_mic_check_says_what_it_does()
    {
        await RunAsync("mic-check", "--help");

        var help = Regex.Replace(_line.Output.ToString(), @"\s+", " ");
        Assert.That(help, Does.Contain("--seconds").And.Contain("--log-folder").And.Contain("Opens no microphone")
            .And.Contain("ClassScribe mic-check --seconds 600 --log-folder \"D:\\Classes\""));
    }

    /// <summary>The help of the app as a whole lists the commands, though not the hidden one, and examples.</summary>
    [TestCase("--help")]
    [TestCase("help")]
    [TestCase("/?")]
    public async Task The_help_of_the_app_lists_the_commands(string ask)
    {
        Assert.That(await RunAsync(ask), Is.EqualTo(0));

        var help = _line.Output.ToString();
        foreach (var command in new[] { "record", "record-only", "transcribe", "rebuild", "list", "devices", "mic-check" })
            Assert.That(help, Does.Contain(command));
        Assert.That(help, Does.Not.Contain("attach"), "the hidden command");
        Assert.That(help, Does.Contain("Examples:").And.Contain("ELEVENLABS_API_KEY"));
    }

    /// <summary>The help of the app as a whole opens with the build, the version and the commit, before the usage.</summary>
    [Test]
    public async Task The_help_of_the_app_starts_with_the_build()
    {
        Assert.That(await RunAsync("--help"), Is.EqualTo(0));

        var help = Regex.Replace(_line.Output.ToString(), @"\s+", " ");
        var build = help.IndexOf($"ClassScribe {BuildInfo.Describe()} records the sound your computer plays during a class", StringComparison.Ordinal);
        Assert.That(build, Is.GreaterThanOrEqualTo(0), "the first line names the build");
        Assert.That(build, Is.LessThan(help.IndexOf("Usage:", StringComparison.Ordinal)));
    }

    /// <summary>
    /// --version, in any case, writes the informational version as the project stamped it (the version, a plus sign and
    /// the commit) on one line with exit code 0. Nothing runs and nothing is asked, so it needs no keyboard either.
    /// </summary>
    [TestCase("--version", true)]
    [TestCase("--Version", true)]
    [TestCase("--version", false)]
    public async Task Version_prints_the_informational_version(string ask, bool interactive)
    {
        CreateLine(interactive);

        Assert.That(await RunAsync(ask), Is.EqualTo(0));

        Assert.That(_line.Output.ToString(), Is.EqualTo(BuildInfo.InformationalVersion + Environment.NewLine));
        Assert.That(_line.Mistakes, Is.Empty);
        Assert.That(_ui.Screen, Is.Empty, "no menu, nothing asked");
        _commands.VerifyNoOtherCalls();
    }

    // ---- What the app does around a command ----

    /// <summary>App opens the settings gate and prints the report at the start only for commands; help, the version, the menu and attach need neither.</summary>
    [Test]
    public void Each_command_line_says_what_it_needs()
    {
        File.WriteAllBytes(_folder.File("Lecture.mp4"), [1]);

        Assert.That(_line.KindOf("--help"), Is.EqualTo(CommandKind.Help));
        Assert.That(_line.KindOf("record", "--help"), Is.EqualTo(CommandKind.Help));
        Assert.That(_line.KindOf("--version"), Is.EqualTo(CommandKind.Version));
        Assert.That(_line.KindOf(), Is.EqualTo(CommandKind.Menu));
        Assert.That(_line.KindOf("attach"), Is.EqualTo(CommandKind.Attach));
        Assert.That(_line.KindOf("attach", "--alert", "20261015-230002Z-1f3a9c07"), Is.EqualTo(CommandKind.Attach));
        Assert.That(_line.KindOf("record", "Kalam", "--device", "auto", "--output", Classes), Is.EqualTo(CommandKind.Command));
        Assert.That(_line.KindOf("record-only", "Kalam", "--app", "chrome", "--output", Classes), Is.EqualTo(CommandKind.Command));
        Assert.That(_line.KindOf("transcribe", @"C:\Classes\Kalam"), Is.EqualTo(CommandKind.Command));
        Assert.That(_line.KindOf("rebuild", @"C:\Classes\Kalam"), Is.EqualTo(CommandKind.Command));
        Assert.That(_line.KindOf("list"), Is.EqualTo(CommandKind.Command));
        Assert.That(_line.KindOf("devices"), Is.EqualTo(CommandKind.Command));
        Assert.That(_line.KindOf("mic-check", "--seconds", "5"), Is.EqualTo(CommandKind.Command), "the log goes under Recording:OutputFolder, so the settings are read first");
        Assert.That(_line.KindOf("record", "Kalam", "--in-person", "--output", Classes), Is.EqualTo(CommandKind.Command));
        Assert.That(_line.KindOf(_folder.File("Lecture.mp4")), Is.EqualTo(CommandKind.Command));
    }

    /// <summary>
    /// Ctrl+C stays with ClassScribe: a command is run without System.CommandLine's own handling, which would end the process
    /// 2 seconds after a Ctrl+C, and an exception reaches App, which turns it into the exit code. This is asked of the
    /// configuration each command was actually run with, for a recording and for the menu, which records too, so a command
    /// run any other way fails here.
    /// </summary>
    [Test]
    public async Task Ctrl_C_and_failures_stay_with_ClassScribe()
    {
        Assert.That(await RunAsync("record", "Kalam", "--device", "auto", "--output", Classes), Is.EqualTo(0));
        var record = _line.LastRun!.InvocationConfiguration;
        _ui.Type('q');
        Assert.That(await RunAsync(), Is.EqualTo(0));
        var menu = _line.LastRun!.InvocationConfiguration;

        foreach (var (what, used) in new[] { ("record", record), ("the menu", menu) })
        {
            Assert.That(used.ProcessTerminationTimeout, Is.Null, $"{what} is run without a process termination timeout");
            Assert.That(used.EnableDefaultExceptionHandler, Is.False, $"{what} is run without the default exception handler");
            Assert.That(used.Output, Is.SameAs(_line.Output), $"{what} writes to the command line's output");
            Assert.That(used.Error, Is.SameAs(_line.Errors), $"{what} writes its errors to the command line's error");
        }
    }

    /// <summary>A command that fails lets its exception through to App, rather than System.CommandLine printing it and returning 1.</summary>
    [Test]
    public void A_failing_command_lets_its_exception_through()
    {
        _commands.Setup(c => c.DevicesAsync()).ThrowsAsync(new InvalidOperationException("No output device."));

        var failure = Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("devices"));

        Assert.That(failure!.Message, Is.EqualTo("No output device."));
        Assert.That(_line.Errors.ToString(), Is.Empty, "System.CommandLine wrote nothing of its own");
    }
}
