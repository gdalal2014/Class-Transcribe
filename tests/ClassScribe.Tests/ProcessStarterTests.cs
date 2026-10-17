using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace ClassScribe.Tests;

/// <summary>
/// Starting the recorder and the alert window. Most tests stand in for Windows and look at what it is given: the launch
/// the starter asks for, or every argument of the call of CreateProcessW itself, which a stand-in takes and refuses. The
/// one test in the WindowsProcess category starts a real process, a cmd.exe with no window that ends by itself at once;
/// it never starts ClassScribe.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public class ProcessStarterTests
{
    /// <summary>A run id as the starter makes them: the UTC start, then eight hex digits.</summary>
    private const string RunId = "20261016-230002Z-0a1b2c3d";

    /// <summary>The published exe, where classes start it from.</summary>
    private const string Exe = @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe";

    /// <summary>The folder of the published exe's files.</summary>
    private const string AppFolder = @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\";

    /// <summary>ClassScribe's own folder under %LOCALAPPDATA%, which the processes run in.</summary>
    private const string RuntimeFolder = @"C:\Users\gdala\AppData\Local\ClassScribe";

    /// <summary>dotnet itself, as it is when ClassScribe runs as "dotnet ClassScribe.dll".</summary>
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";

    /// <summary>The folder of the Debug build's files.</summary>
    private const string DebugFolder = @"C:\Users\gdala\OneDrive\Documents\GitHub\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\";

    /// <summary>The flags the recorder is started with, outside the job or not: no window, a process group of its own.</summary>
    private const ProcessCreationFlags RecorderFlags = ProcessCreationFlags.NoWindow | ProcessCreationFlags.NewProcessGroup | ProcessCreationFlags.UnicodeEnvironment;

    private FakeProcessCreationApi _api = null!;
    private FakeCreateProcess _windows = null!;
    private RecordingLogger<ProcessStarter> _log = null!;

    /// <summary>Creates the stand-ins for the starter's launches and for CreateProcessW itself, and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _api = new FakeProcessCreationApi();
        _windows = new FakeCreateProcess();
        _log = new RecordingLogger<ProcessStarter>();
    }

    /// <summary>A starter that starts through the stand-in, as if this process ran <paramref name="processPath"/> from <paramref name="appFolder"/>.</summary>
    private ProcessStarter Starter(string? processPath = Exe, string appFolder = AppFolder) =>
        new(_api, new ThisProgram(processPath, appFolder), RuntimeFolder, _log);

    /// <summary>
    /// A starter whose starts go through <see cref="NativeProcessCreationApi"/> as far as the call of CreateProcessW, which
    /// the stand-in takes in Windows' place and refuses, so that nothing starts.
    /// </summary>
    private ProcessStarter StarterUpToWindows() =>
        new(new NativeProcessCreationApi(_windows.CreateProcess), new ThisProgram(Exe, AppFolder), RuntimeFolder, _log);

    /// <summary>A STARTUPINFO with nothing in it but its size: no STARTF_ flag, no window to show, no standard handles.</summary>
    private static NativeProcessCreationApi.StartupInfo SizeOnly => new() { Size = Marshal.SizeOf<NativeProcessCreationApi.StartupInfo>() };

    /// <summary>Where <paramref name="field"/> of STARTUPINFO sits, in bytes from its start, as Windows reads it.</summary>
    private static int OffsetInStartupInfo(string field) => Marshal.OffsetOf<NativeProcessCreationApi.StartupInfo>(field).ToInt32();

    /// <summary>The words Windows has for an error code.</summary>
    private static string WindowsWords(int error) => new Win32Exception(error).Message.TrimEnd('.');

    /// <summary>The recorder is this very program, from its full path, with the recorder's argument and the run id: nothing else.</summary>
    [Test]
    public void The_recorder_is_this_program_with_the_recorder_argument_and_the_run_id()
    {
        Starter().StartRecorder(RunId, breakaway: false);

        var launch = _api.Launches.Single();
        Assert.That(launch.Application, Is.EqualTo(Exe), "the full path, so that Windows looks for the program nowhere else");
        Assert.That(launch.CommandLine, Is.EqualTo($"\"{Exe}\" --recorder {RunId}"));
    }

    /// <summary>
    /// The recorder has no window and a process group of its own, so no Ctrl+C reaches it; it inherits no handle and gets
    /// no standard handles, so a script reading the window's output never waits for it; and it runs in ClassScribe's own
    /// folder, so that it holds no other folder open.
    /// </summary>
    [Test]
    public void The_recorder_has_no_window_its_own_process_group_no_handles_and_ClassScribes_own_folder()
    {
        Starter().StartRecorder(RunId, breakaway: false);

        var launch = _api.Launches.Single();
        Assert.That(launch.Flags, Is.EqualTo(RecorderFlags));
        Assert.That(launch.InheritHandles, Is.False);
        Assert.That(launch.Show, Is.Null, "no window to show, and nothing else of STARTUPINFO given");
        Assert.That(launch.WorkingFolder, Is.EqualTo(RuntimeFolder));
    }

    /// <summary>The recorder starts outside the job only when the caller asks for it, which it does where the job allows it; the other flags stay the same.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public void Breakaway_is_asked_for_only_when_the_caller_asks_for_it(bool breakaway)
    {
        Starter().StartRecorder(RunId, breakaway);

        var flags = _api.Launches.Single().Flags;
        Assert.That(flags.HasFlag(ProcessCreationFlags.BreakawayFromJob), Is.EqualTo(breakaway));
        Assert.That(flags & ~ProcessCreationFlags.BreakawayFromJob, Is.EqualTo(RecorderFlags));
    }

    /// <summary>
    /// When this process is dotnet itself, as with "dotnet ClassScribe.dll", the recorder is dotnet running ClassScribe's
    /// dll from the app's folder, whatever the letter case of dotnet's name.
    /// </summary>
    [TestCase(Dotnet)]
    [TestCase(@"C:\Program Files\dotnet\DOTNET.EXE")]
    public void Under_dotnet_the_recorder_is_dotnet_running_ClassScribes_dll(string dotnet)
    {
        Starter(dotnet, DebugFolder).StartRecorder(RunId, breakaway: false);

        var launch = _api.Launches.Single();
        Assert.That(launch.Application, Is.EqualTo(dotnet));
        Assert.That(launch.CommandLine, Is.EqualTo($"\"{dotnet}\" \"{DebugFolder}ClassScribe.dll\" --recorder {RunId}"));
        Assert.That(launch.Flags, Is.EqualTo(RecorderFlags));
    }

    /// <summary>
    /// The alert window is this very program attached to the run in alert mode, in a console window of its own that is
    /// shown minimized without taking the focus, so that it only appears on the taskbar. It inherits no handle, runs in
    /// ClassScribe's own folder, and asks for no process group of its own, a flag Windows ignores beside a new console.
    /// Whether Ctrl+C works in it is not up to these flags: started by the recorder, it most likely begins with Ctrl+C
    /// ignored, and the attach command turns it back on if it is to work there.
    /// </summary>
    [Test]
    public void The_alert_window_is_a_console_of_its_own_shown_minimized_without_the_focus()
    {
        Starter().StartAlertWindow(RunId);

        var launch = _api.Launches.Single();
        Assert.That(launch.Application, Is.EqualTo(Exe));
        Assert.That(launch.CommandLine, Is.EqualTo($"\"{Exe}\" attach --alert {RunId}"));
        Assert.That(launch.Flags, Is.EqualTo(ProcessCreationFlags.NewConsole | ProcessCreationFlags.UnicodeEnvironment));
        Assert.That(launch.Show, Is.EqualTo(ProcessWindowShow.MinimizedNotActive));
        Assert.That(launch.InheritHandles, Is.False);
        Assert.That(launch.WorkingFolder, Is.EqualTo(RuntimeFolder));
    }

    /// <summary>Under dotnet, the alert window is dotnet running ClassScribe's dll too.</summary>
    [Test]
    public void Under_dotnet_the_alert_window_is_dotnet_running_ClassScribes_dll()
    {
        Starter(Dotnet, DebugFolder).StartAlertWindow(RunId);

        var launch = _api.Launches.Single();
        Assert.That(launch.Application, Is.EqualTo(Dotnet));
        Assert.That(launch.CommandLine, Is.EqualTo($"\"{Dotnet}\" \"{DebugFolder}ClassScribe.dll\" attach --alert {RunId}"));
    }

    /// <summary>What Windows started is handed back as it is, and the start is logged with the process's id and its command line.</summary>
    [Test]
    public void What_Windows_started_is_handed_back_and_logged()
    {
        var process = new StartedProcess(4321, new DateTimeOffset(2026, 10, 16, 23, 0, 2, TimeSpan.Zero), () => 7);
        _api.Next = process;

        var recorder = Starter().StartRecorder(RunId, breakaway: true);
        var alertWindow = Starter().StartAlertWindow(RunId);

        Assert.That(recorder, Is.SameAs(process));
        Assert.That(alertWindow, Is.SameAs(process));
        Assert.That(_log.Messages, Has.Some.Matches<string>(message =>
            message.StartsWith("Started the recorder", StringComparison.Ordinal) && message.Contains("4321") && message.Contains($"--recorder {RunId}")));
        Assert.That(_log.Messages, Has.Some.Matches<string>(message =>
            message.StartsWith("Started the alert window", StringComparison.Ordinal) && message.Contains("4321") && message.Contains($"attach --alert {RunId}")));
        Assert.That(_log.Problems, Is.Empty, "the starter's caller says what a start means for the recording");
    }

    /// <summary>
    /// A start Windows refuses throws a <see cref="Win32Exception"/> carrying Windows' error, in plain words; access denied,
    /// which a breakaway the job forbids gives, is told by its number. The refusal is logged with what was asked.
    /// </summary>
    [TestCase(IProcessStarter.AccessDenied)]
    [TestCase(2)]
    [TestCase(225)]
    public void A_start_Windows_refuses_throws_Windows_error(int error)
    {
        _api.Refusal = error;

        var recorder = Assert.Throws<Win32Exception>(() => Starter().StartRecorder(RunId, breakaway: true));
        var alertWindow = Assert.Throws<Win32Exception>(() => Starter().StartAlertWindow(RunId));

        Assert.That(recorder!.NativeErrorCode, Is.EqualTo(error));
        Assert.That(recorder.Message, Is.EqualTo($"Windows did not start the recorder: {WindowsWords(error)} (error {error})"));
        Assert.That(alertWindow!.NativeErrorCode, Is.EqualTo(error));
        Assert.That(alertWindow.Message, Is.EqualTo($"Windows did not start the alert window: {WindowsWords(error)} (error {error})"));
        Assert.That(_log.Messages, Has.Some.Matches<string>(message =>
            message.StartsWith("Windows did not start the recorder", StringComparison.Ordinal) && message.Contains($"--recorder {RunId}") && message.Contains("BreakawayFromJob")));
    }

    /// <summary>
    /// Anything but a run id is refused before anything starts, since it goes on a command line: a space, a quote or a
    /// character a shell reads could add arguments of its own, and a leading dash would read as an option. So is a plain
    /// word such as run-2, which the recorder could not use to name its run file, pipe and stop event.
    /// </summary>
    [TestCase("")]
    [TestCase("20261016 230002Z")]
    [TestCase("x\" --output \"C:\\Temp")]
    [TestCase("-x")]
    [TestCase("a&b")]
    [TestCase("..\\x")]
    [TestCase("run-2")]
    public void Anything_but_a_run_id_is_refused_before_anything_starts(string runId)
    {
        Assert.Throws<ArgumentException>(() => Starter().StartRecorder(runId, breakaway: false));
        Assert.Throws<ArgumentException>(() => Starter().StartAlertWindow(runId));

        Assert.That(_api.Launches, Is.Empty);
    }

    /// <summary>When Windows does not say which program this process runs, nothing is started, and the reason is the error a missing program gives.</summary>
    [Test]
    public void Without_the_programs_path_nothing_is_started()
    {
        var refusal = Assert.Throws<Win32Exception>(() => Starter(processPath: null).StartRecorder(RunId, breakaway: false));

        Assert.That(refusal!.NativeErrorCode, Is.EqualTo(2), "ERROR_FILE_NOT_FOUND");
        Assert.That(refusal.Message, Does.StartWith("Windows did not start the recorder"));
        Assert.That(_api.Launches, Is.Empty);
    }

    /// <summary>The flags and the way the window is shown have the values Windows documents, since a wrong one would only show on a real start.</summary>
    [Test]
    public void The_flags_have_the_values_Windows_documents()
    {
        Assert.That((uint)ProcessCreationFlags.NewConsole, Is.EqualTo(0x00000010u), "CREATE_NEW_CONSOLE");
        Assert.That((uint)ProcessCreationFlags.NewProcessGroup, Is.EqualTo(0x00000200u), "CREATE_NEW_PROCESS_GROUP");
        Assert.That((uint)ProcessCreationFlags.UnicodeEnvironment, Is.EqualTo(0x00000400u), "CREATE_UNICODE_ENVIRONMENT");
        Assert.That((uint)ProcessCreationFlags.BreakawayFromJob, Is.EqualTo(0x01000000u), "CREATE_BREAKAWAY_FROM_JOB");
        Assert.That((uint)ProcessCreationFlags.NoWindow, Is.EqualTo(0x08000000u), "CREATE_NO_WINDOW");
        Assert.That((short)ProcessWindowShow.MinimizedNotActive, Is.EqualTo((short)7), "SW_SHOWMINNOACTIVE");
    }

    /// <summary>
    /// What CreateProcessW is given for the alert window: a console of its own (CREATE_NEW_CONSOLE), no handle inherited,
    /// and a STARTUPINFO that says its wShowWindow counts (STARTF_USESHOWWINDOW, alone) and is SW_SHOWMINNOACTIVE, so that
    /// the window comes up minimized on the taskbar without taking the focus from the window Gulzar is in. Nothing else of
    /// STARTUPINFO is given: no standard handles, no title, no desktop. The stand-in refuses the call, so nothing starts.
    /// </summary>
    [Test]
    public void The_alert_window_reaches_CreateProcessW_minimized_without_the_focus()
    {
        Assert.Throws<Win32Exception>(() => StarterUpToWindows().StartAlertWindow(RunId));

        var call = _windows.Calls.Single();
        Assert.That(call.StartupInfo.Flags, Is.EqualTo(0x00000001), "STARTF_USESHOWWINDOW, and no other STARTF_ flag");
        Assert.That(call.StartupInfo.ShowWindow, Is.EqualTo((short)7), "SW_SHOWMINNOACTIVE");
        Assert.That(call.StartupInfo, Is.EqualTo(SizeOnly with { Flags = 0x00000001, ShowWindow = 7 }), "nothing else of STARTUPINFO");
        Assert.That(call.CreationFlags, Is.EqualTo(0x00000410u), "CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT");
        Assert.That(call.InheritHandles, Is.False);
        Assert.That(call.ApplicationName, Is.EqualTo(Exe));
        Assert.That(call.CommandLine, Is.EqualTo($"\"{Exe}\" attach --alert {RunId}\0"));
        Assert.That(call.CurrentDirectory, Is.EqualTo(RuntimeFolder));
        Assert.That(call.Environment, Is.EqualTo(IntPtr.Zero), "this process's environment, inherited as it is");
    }

    /// <summary>
    /// What CreateProcessW is given for the recorder: no window and a process group of its own (CREATE_NO_WINDOW,
    /// CREATE_NEW_PROCESS_GROUP and CREATE_UNICODE_ENVIRONMENT, with CREATE_BREAKAWAY_FROM_JOB only when asked for), no
    /// handle inherited, and a STARTUPINFO with nothing in it but its size. In particular there is no STARTF_USESTDHANDLES
    /// and no standard handle, so that a script reading the window's output never waits for the recorder to end.
    /// </summary>
    [TestCase(false, 0x08000600u)]
    [TestCase(true, 0x09000600u)]
    public void The_recorder_reaches_CreateProcessW_with_no_window_its_own_process_group_and_no_handles(bool breakaway, uint creationFlags)
    {
        Assert.Throws<Win32Exception>(() => StarterUpToWindows().StartRecorder(RunId, breakaway));

        var call = _windows.Calls.Single();
        Assert.That(call.CreationFlags, Is.EqualTo(creationFlags));
        Assert.That(call.InheritHandles, Is.False);
        Assert.That(call.StartupInfo.Flags & 0x00000100, Is.Zero, "never STARTF_USESTDHANDLES");
        Assert.That(call.StartupInfo, Is.EqualTo(SizeOnly), "nothing but its size: no STARTF_ flag, no window to show, no standard handles");
        Assert.That(call.ApplicationName, Is.EqualTo(Exe));
        Assert.That(call.CommandLine, Is.EqualTo($"\"{Exe}\" --recorder {RunId}\0"));
        Assert.That(call.CurrentDirectory, Is.EqualTo(RuntimeFolder));
        Assert.That(call.Environment, Is.EqualTo(IntPtr.Zero), "this process's environment, inherited as it is");
    }

    /// <summary>
    /// Every part of a launch reaches CreateProcessW as it is, whatever it holds: the program, the command line (in a copy
    /// CreateProcessW may write into, ended by a null character), whether handles are inherited, the flags, the folder and
    /// how a window is shown. No environment block and no security attributes are given. When Windows refuses, its error
    /// comes back, and no process.
    /// </summary>
    [TestCase(false, 0x08000600u, false)]
    [TestCase(true, 0x01000010u, true)]
    public void Every_part_of_a_launch_reaches_CreateProcessW_as_it_is(bool inheritHandles, uint creationFlags, bool shown)
    {
        _windows.Refusal = 2;
        var launch = new ProcessLaunch(
            @"C:\Tools\Other.exe", "\"C:\\Tools\\Other.exe\" --one two", inheritHandles, (ProcessCreationFlags)creationFlags, @"C:\Work",
            shown ? ProcessWindowShow.MinimizedNotActive : null);

        var started = new NativeProcessCreationApi(_windows.CreateProcess).Start(launch, out var error);

        Assert.That(started, Is.Null);
        Assert.That(error, Is.EqualTo(2), "Windows' error, read where Windows leaves it");
        var call = _windows.Calls.Single();
        Assert.That(call.ApplicationName, Is.EqualTo(launch.Application));
        Assert.That(call.CommandLine, Is.EqualTo(launch.CommandLine + "\0"));
        Assert.That(call.InheritHandles, Is.EqualTo(inheritHandles));
        Assert.That(call.CreationFlags, Is.EqualTo(creationFlags));
        Assert.That(call.CurrentDirectory, Is.EqualTo(launch.WorkingFolder));
        Assert.That(call.StartupInfo, Is.EqualTo(shown ? SizeOnly with { Flags = 0x00000001, ShowWindow = 7 } : SizeOnly));
        Assert.That(call.Environment, Is.EqualTo(IntPtr.Zero));
        Assert.That(call.ProcessAttributes, Is.EqualTo(IntPtr.Zero));
        Assert.That(call.ThreadAttributes, Is.EqualTo(IntPtr.Zero));
    }

    /// <summary>
    /// STARTUPINFO and PROCESS_INFORMATION have the sizes and the layout Windows documents for this process's bitness: cb
    /// is the size Windows expects; dwFlags, wShowWindow and the standard handles sit where Windows reads them; and
    /// PROCESS_INFORMATION has room for all that Windows writes into it.
    /// </summary>
    [Test]
    public void STARTUPINFO_and_PROCESS_INFORMATION_have_the_layout_Windows_documents()
    {
        var x64 = Environment.Is64BitProcess;

        Assert.That(Marshal.SizeOf<NativeProcessCreationApi.StartupInfo>(), Is.EqualTo(x64 ? 104 : 68), "sizeof(STARTUPINFOW)");
        Assert.That(OffsetInStartupInfo(nameof(NativeProcessCreationApi.StartupInfo.Flags)), Is.EqualTo(x64 ? 60 : 44), "dwFlags");
        Assert.That(OffsetInStartupInfo(nameof(NativeProcessCreationApi.StartupInfo.ShowWindow)), Is.EqualTo(x64 ? 64 : 48), "wShowWindow");
        Assert.That(OffsetInStartupInfo(nameof(NativeProcessCreationApi.StartupInfo.StdInput)), Is.EqualTo(x64 ? 80 : 56), "hStdInput");
        Assert.That(OffsetInStartupInfo(nameof(NativeProcessCreationApi.StartupInfo.StdError)), Is.EqualTo(x64 ? 96 : 64), "hStdError");
        Assert.That(Marshal.SizeOf<NativeProcessCreationApi.ProcessInformation>(), Is.EqualTo(x64 ? 24 : 16), "sizeof(PROCESS_INFORMATION)");
    }

    /// <summary>
    /// A real process, started as the recorder is: the recorder's flags, breakaway where this test's own job allows it as
    /// the launcher decides, no inherited handle, no window, the folder given. It is cmd.exe in place of ClassScribe, and
    /// ends at once by itself with the exit code 7 only when both its working folder is the one given (a marker file is
    /// there) and it inherited this process's environment (the 7 comes from a variable set here). Its exit code and start
    /// time are read through what the starter hands back.
    /// </summary>
    [Test, Category("WindowsProcess")]
    public async Task A_process_started_as_the_recorder_is_runs_in_the_folder_given_with_this_environment_and_its_exit_code_is_read()
    {
        const string variable = "CLASSSCRIBE_STARTER_TEST";
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("marker.txt"), "the working folder");
        using var seven = new TemporaryVariable(variable, "7");
        var breakaway = new JobInspector(NullLogger<JobInspector>.Instance).Inspect().Breakaway;
        new ProcessStarter(_api, new ThisProgram(Exe, AppFolder), folder.Path, _log).StartRecorder(RunId, breakaway);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var launch = _api.Launches.Single() with
        {
            Application = cmd,
            CommandLine = $"\"{cmd}\" /d /c if exist marker.txt exit %{variable}%"
        };
        var before = DateTimeOffset.UtcNow;

        var started = NativeProcessCreationApi.Instance.Start(launch, out var error);

        Assert.That(started, Is.Not.Null, $"Windows did not start cmd.exe: {WindowsWords(error)}");
        Assert.That(error, Is.Zero);
        try
        {
            await Wait.UntilAsync(() => started!.ExitCode() is not null, timeoutSeconds: 15);
            Assert.That(started!.ExitCode(), Is.EqualTo(7), "0 means the working folder was not the one given, or the environment was not inherited");
            Assert.That(started.ProcessId, Is.GreaterThan(0));
            Assert.That(started.StartedUtc, Is.InRange(before.AddSeconds(-2), DateTimeOffset.UtcNow.AddSeconds(2)));
        }
        finally
        {
            EndIfStillRunning(started!);
        }
    }

    /// <summary>
    /// Ends the test's own process if it is somehow still running, so that a test never leaves one behind. Only the
    /// process this test started is ended: the one with its id and its start time.
    /// </summary>
    private static void EndIfStillRunning(StartedProcess started)
    {
        if (started.ExitCode() is not null) return;

        try
        {
            using var process = Process.GetProcessById(started.ProcessId);
            if ((process.StartTime.ToUniversalTime() - started.StartedUtc.UtcDateTime).Duration() < TimeSpan.FromSeconds(2)) process.Kill();
        }
        catch (ArgumentException)
        {
            // Deliberately not rethrown: the process ended between the look and this one, which is what was wanted.
        }
    }
}

/// <summary>The words on the command lines of the processes a run starts, and what counts as a run id there.</summary>
[TestFixture]
public class ProcessArgumentsTests
{
    /// <summary>A run id is one as runs are named: the time the run started, then eight hex digits, as <see cref="RunIds.New"/> makes them.</summary>
    [Test]
    public void A_run_id_is_one_as_runs_are_named()
    {
        Assert.That(ProcessArguments.IsRunId("20261016-230002Z-0a1b2c3d"), Is.True);
        Assert.That(ProcessArguments.IsRunId(RunIds.New(new DateTimeOffset(2026, 10, 16, 23, 0, 2, TimeSpan.Zero))), Is.True);
    }

    /// <summary>
    /// Nothing else is a run id: nothing at all, a leading dash, a space, a quote, a path's characters, a character a shell
    /// reads, a letter or digit outside ASCII, which could look like another one, or any other mix of letters, digits and
    /// dashes, which the run files, the pipe and the stop event would not take.
    /// </summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("-recorder")]
    [TestCase("2026 10 16")]
    [TestCase("a\"b")]
    [TestCase("..\\runs")]
    [TestCase("a/b")]
    [TestCase("a.b")]
    [TestCase("a|b")]
    [TestCase("a%b%")]
    [TestCase("Kalām")]
    [TestCase("٢٠٢٦")]
    [TestCase("a")]
    [TestCase("7")]
    [TestCase("run-2")]
    [TestCase("20261016-230002Z-0A1B2C3D")]
    [TestCase("20261016-230002Z-0a1b2c3d-2")]
    public void Nothing_else_is_a_run_id(string? runId)
    {
        Assert.That(ProcessArguments.IsRunId(runId), Is.False);
    }

    /// <summary>
    /// The starter takes exactly the run ids that the run files, the stop event and the command line take
    /// (<see cref="RunIds.IsWellFormed"/>), so a recorder is never started with an id it cannot use for its run file, its
    /// pipe and its stop event.
    /// </summary>
    [TestCase("20261016-230002Z-0a1b2c3d")]
    [TestCase("20261016-230002Z-0a1b2c3")]
    [TestCase("run-2")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("20261016-230002Z-0a1b2c3d\n")]
    [TestCase(null)]
    public void The_starter_takes_the_run_ids_the_run_files_take(string? runId)
    {
        Assert.That(ProcessArguments.IsRunId(runId), Is.EqualTo(RunIds.IsWellFormed(runId)));
    }

    /// <summary>The recorder's argument and the alert window's command and option are the words the rest of ClassScribe reads.</summary>
    [Test]
    public void The_words_are_the_ones_the_command_lines_use()
    {
        Assert.That(ProcessArguments.Recorder, Is.EqualTo("--recorder"));
        Assert.That(ProcessArguments.Attach, Is.EqualTo("attach"));
        Assert.That(ProcessArguments.Alert, Is.EqualTo("--alert"));
    }
}
