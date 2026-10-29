using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of the ports of the prompts and the run starter (architecture 4.3, slice 3b4) over their real adapters in
/// Cli: the continue prompt (<see cref="IContinuePrompt"/>) over <see cref="ContinueQuestion"/>, with a recording that stopped
/// recently in a temporary folder and the run files in another; the camera prompt (<see cref="ICameraPrompt"/>) over
/// <see cref="CameraStart"/> and the camera's fakes, which open nothing; the run starter (<see cref="IRunStarter"/>) over the
/// real <see cref="CommandRunner"/>, with a starter of runs the test plays in each scope; and that the window's host and the
/// recorder's resolve the prompts in a scope to the questions they hold and the starter to the runner.
/// </summary>
[TestFixture]
public class PromptPortTests
{
    /// <summary>Now: 23:51:02 UTC, four minutes after the recording below stopped.</summary>
    private static readonly DateTimeOffset Now = TestRuns.Start.AddMinutes(51);

    private TestRuntime _runtime = null!;
    private TempFolder _recordings = null!;
    private TestOptions<RecordingOptions> _options = null!;
    private FakeConsoleUi _ui = null!;

    /// <summary>Makes the recordings folder, ClassScribe's own folder, the clock at <see cref="Now"/> and a console to type into.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        _runtime.Time.Advance(Now - TestRuns.Start);
        _recordings = new TempFolder();
        _options = TestOptions.Create(new RecordingOptions { OutputFolder = _recordings.Path });
        _ui = new FakeConsoleUi();
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _recordings.Dispose();
        _runtime.Dispose();
    }

    /// <summary>The continue prompt over the window's question, in the temporary folders.</summary>
    private IContinuePrompt ContinuePrompt() =>
        new ContinueQuestion(new ClassFolders(_options, _runtime.Time, NullLogger<ClassFolders>.Instance), new RecordingFolder(), _runtime.Journal, _ui, _options, _runtime.Time,
            NullLogger<ContinueQuestion>.Instance);

    /// <summary>The camera prompt over the window's camera start, the cameras, Windows' camera facts, its record and the registry stand-ins.</summary>
    private ICameraPrompt CameraPrompt() =>
        new CameraStart(new FakeCameraProvider(), new CameraRecorderNotBuilt(), new CameraSetupFacts(new FakeCameraSystem(), new FakeCameraUsageRecord(), new FakeRegistryValues()),
            new SourceChoice(_options), _ui, NullLogger<CameraStart>.Instance);

    /// <summary>
    /// Writes a recording of "Kalam" that started at 23:00:02 UTC and stopped at 23:47:02, four minutes before now, holding
    /// 00:47:10 of audio, as a crash, a closed window or a restart leaves it.
    /// </summary>
    private RecordingFiles WriteStopped()
    {
        var files = RecordingFiles.ForNew(Path.Combine(_recordings.Path, "Kalam"), "Kalam", TestRuns.Start);
        Directory.CreateDirectory(files.Folder);
        using (var writer = new WavSegmentWriter(files.Segment(1), 10))
            writer.Write(new byte[(int)TimeSpan.FromSeconds(2830).TotalSeconds * 10 * 2]);
        File.SetLastWriteTimeUtc(files.Segment(1), Now.AddMinutes(-4).UtcDateTime);
        return files;
    }

    /// <summary>A request to record "Tajwid 4" in person with the OBSBOT's microphone and <paramref name="camera"/>.</summary>
    private static RecordRequest Lesson(CameraChoice? camera) =>
        TestRuns.Request(TestRuns.Id(1), "Tajwid 4") with { Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT"), camera) };

    /// <summary>The menu's question through the port: C continues the recording that stopped, with its line; N starts a new one; Esc goes back.</summary>
    [Test]
    public async Task The_continue_prompt_asks_and_C_continues_N_starts_anew_and_Esc_goes_back()
    {
        var files = WriteStopped();

        _ui.Type('c');
        var continued = await ContinuePrompt().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);
        _ui.Type('n');
        var anew = await ContinuePrompt().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);
        _ui.Type('\u001b');
        var back = await ContinuePrompt().AskAsync("Kalam", RecordingMode.Class, _recordings.Path);

        Assert.Multiple(() =>
        {
            Assert.That(continued, Is.EqualTo(new ContinueAnswer(GoBack: false, files, "Continuing Kalam_2026-10-15_23-00-02Z as its next part (00:47:10 recorded so far).")));
            Assert.That(anew, Is.EqualTo(new ContinueAnswer(GoBack: false, Continue: null, Hint: null)));
            Assert.That(back, Is.EqualTo(new ContinueAnswer(GoBack: true, Continue: null, Hint: null)));
            Assert.That(_ui.Screen, Does.Contain("Kalam_2026-10-15_23-00-02Z stopped 4 minutes ago (00:47:10 recorded, no transcript yet). C continues it as its next part, N starts a new recording, Esc goes back."));
        });
    }

    /// <summary>
    /// The command line's flags through the port, which never asks: --continue continues the recording, or says there is
    /// none and starts a new one; --new says nothing; neither starts a new one, with how to continue the other one instead.
    /// </summary>
    [Test]
    public void The_continue_prompt_resolves_the_command_lines_flags_without_asking()
    {
        var none = ContinuePrompt().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Continue);
        var files = WriteStopped();

        var continued = ContinuePrompt().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Continue);
        var anew = ContinuePrompt().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.New);
        var asked = ContinuePrompt().Resolve("Kalam", RecordingMode.Class, _recordings.Path, ContinueChoice.Ask);

        Assert.Multiple(() =>
        {
            Assert.That(none.Continue, Is.Null);
            Assert.That(none.Hint, Does.StartWith("There is no recording of \"Kalam\" to continue").And.EndWith("so a new recording starts."));
            Assert.That(continued, Is.EqualTo(new ContinueAnswer(GoBack: false, files, "Continuing Kalam_2026-10-15_23-00-02Z as its next part (00:47:10 recorded so far).")));
            Assert.That(anew, Is.EqualTo(new ContinueAnswer(GoBack: false, Continue: null, Hint: null)));
            Assert.That(asked, Is.EqualTo(new ContinueAnswer(GoBack: false, Continue: null,
                "Kalam_2026-10-15_23-00-02Z stopped 4 minutes ago without a transcript; to add to it instead, run the same command with --continue.")));
            Assert.That(_ui.KeyReads, Is.Zero, "nothing is asked");
        });
    }

    /// <summary>
    /// The camera prompt through the port: a plan without a camera goes on as it is, nothing asked; one with a camera, in
    /// this build, is refused from the command line with the port's words and exit code 1, and from the menu offers the
    /// sound only, which Enter takes.
    /// </summary>
    [Test]
    public async Task The_camera_prompt_lets_a_plan_without_a_camera_go_on_and_decides_one_with_it()
    {
        var withoutCamera = await CameraPrompt().DecideAsync(Lesson(null), StartOrigin.Menu);
        var refused = await CameraPrompt().DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.CommandLine);
        var refusedScreen = _ui.Screen;
        _ui.Type('\r');
        var soundOnly = await CameraPrompt().DecideAsync(Lesson(new CameraChoice("OBSBOT")), StartOrigin.Menu);

        Assert.Multiple(() =>
        {
            Assert.That(withoutCamera, Is.EqualTo(new CameraDecision.Go(Lesson(null))));
            Assert.That(refused, Is.EqualTo(new CameraDecision.Refused(1)));
            Assert.That(refusedScreen, Is.EqualTo($"{CameraRecorderNotBuilt.Words}. {ICameraPrompt.NothingStarted}"));
            Assert.That(soundOnly, Is.EqualTo(new CameraDecision.Go(Lesson(null))));
            Assert.That(_ui.KeyReads, Is.EqualTo(1), "only the menu asks");
        });
    }

    /// <summary>
    /// The run starter through the port, over the real runner: each run is handed to the starter of runs resolved in a
    /// scope of its own, with the request and the origin as given, and answers with the starter's exit code.
    /// </summary>
    [Test]
    public async Task The_run_starter_starts_each_run_in_a_scope_of_its_own()
    {
        var played = new PlayedRuns { StartExitCode = 7 };
        await using var container = new ServiceCollection()
            .AddScoped<IRecordStarter>(provider => new FakeRecordStarter(played, provider))
            .BuildServiceProvider(validateScopes: true);
        IRunStarter starter = new CommandRunner(container.GetRequiredService<IServiceScopeFactory>(), container.GetRequiredService<IServiceProviderIsService>(), NullLogger<CommandRunner>.Instance);
        var first = TestRuns.Request(TestRuns.Id(1), "Risala");
        var second = TestRuns.Request(TestRuns.Id(2), "Risala");

        var exitCodes = (await starter.RecordAsync(first, StartOrigin.VideoBatch), await starter.RecordAsync(second, StartOrigin.Menu));

        Assert.Multiple(() =>
        {
            Assert.That(exitCodes, Is.EqualTo((7, 7)));
            Assert.That(played.Started.Select(run => (run.Request, run.Origin)), Is.EqualTo(new[] { (first, StartOrigin.VideoBatch), (second, StartOrigin.Menu) }));
            Assert.That(played.Started[0].Services, Is.Not.SameAs(played.Started[1].Services), "a scope of its own for each run");
            Assert.That(played.Started[0].Services, Is.Not.SameAs(container), "never the root");
        });
    }

    /// <summary>
    /// Both hosts resolve the prompts, in a scope, to the very question and camera start of that scope, and the run starter
    /// to the runner, one for the process. The cameras, Windows' camera facts, its record and the registry are stand-ins,
    /// since resolving the camera start constructs what reads them.
    /// </summary>
    [Test]
    public void Both_hosts_resolve_the_prompts_in_a_scope_and_the_run_starter_to_the_runner()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(temp.Path, userFolder: null);
        using var window = AppHost.Build(places, "test-key", services =>
        {
            services.AddSingleton(_runtime.Folder).AddSingleton(new RoamingFolder(temp.File("Roaming")));
            CameraFakes(services);
        });
        var runId = TestRuns.Id();
        using var log = new RecorderLog(_runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, places, _runtime.Time,
            CameraFakes);

        Assert.Multiple(() =>
        {
            foreach (var (name, services) in new[] { ("the window", window.Services), ("the recorder", recorder.Services) })
            {
                using var scope = services.CreateScope();
                Assert.That(scope.ServiceProvider.GetRequiredService<IContinuePrompt>(), Is.SameAs(scope.ServiceProvider.GetRequiredService<ContinueQuestion>()), $"{name}: the continue prompt");
                Assert.That(scope.ServiceProvider.GetRequiredService<ICameraPrompt>(), Is.SameAs(scope.ServiceProvider.GetRequiredService<CameraStart>()), $"{name}: the camera prompt");
                Assert.That(services.GetRequiredService<IRunStarter>(), Is.SameAs(services.GetRequiredService<ICommandRunner>()), $"{name}: the run starter");
                Assert.That(() => services.GetRequiredService<IContinuePrompt>(), Throws.InvalidOperationException, $"{name}: a prompt belongs to a run");
            }
        });
    }

    /// <summary>Puts the camera's stand-ins in place of what reads the cameras, Windows' camera facts, its record and the registry.</summary>
    private static void CameraFakes(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ICameraProvider>(new FakeCameraProvider()));
        services.Replace(ServiceDescriptor.Singleton<ICameraSystem>(new FakeCameraSystem()));
        services.Replace(ServiceDescriptor.Singleton<ICameraUsageRecord>(new FakeCameraUsageRecord()));
        services.Replace(ServiceDescriptor.Singleton<IRegistryValues>(new FakeRegistryValues()));
    }
}
