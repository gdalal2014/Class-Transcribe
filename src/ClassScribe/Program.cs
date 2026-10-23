using ClassScribe;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;

// A recorder, started by a window as "ClassScribe.exe --recorder <run id>" to record one class in the background: it
// has no window, so it is handed its run before anything touches the console, and it never waits for Enter at the end,
// which nobody could press. Its key comes from its own environment, inherited from the window, and from nowhere else;
// its glossary from the same places as the window's.
if (RecorderHost.IsRecorderStart(args, out var recorderRunId))
    return await new RecorderHost { Places = SettingsPlaces.ForThisUser(AppContext.BaseDirectory) }.RunAsync(recorderRunId, Environment.GetEnvironmentVariable(ElevenLabsOptions.ApiKeyVariable));

// A camera process, started by a window or a recorder as "ClassScribe.exe --camera-process <job id>" in a job that ends it
// with its starter: it speaks over its standard input and output, which are pipes, so it too is handed its job before
// anything touches the console. It needs no key and reads no settings: its job carries what it needs.
if (CameraProtocol.IsCameraProcessStart(args, out var cameraJobId))
    return await new CameraHost().RunAsync(cameraJobId, Console.OpenStandardInput(), Console.OpenStandardOutput());

// First, before anything writes to the console, so that the log shows Arabic, transliteration and
// device names such as "Intel®" as they are.
ConsoleUi.UseUnicode();

// settings and class answer a script through a pipe in UTF-8, and with --json keep standard output for the JSON alone.
if (CommandLineTree.IsOwnKind(args)) ConsoleUi.UseUtf8WhenRedirected();

// Settings are read from the user's own folder when it holds a file, else from the folder the app lives in, whatever
// folder it is started from; which one is decided again at every read (see SettingsPlaces).
var places = SettingsPlaces.ForThisUser(AppContext.BaseDirectory);
int exitCode;
try
{
    using var host = AppHost.Build(places, Environment.GetEnvironmentVariable(ElevenLabsOptions.ApiKeyVariable),
        CommandLineTree.WantsJson(args) ? AppHost.LogToStandardError : null);
    exitCode = await AppHost.RunAsync(host.Services, args);
}
catch (Exception ex)
{
    // Deliberately not rethrown: this is the last stop before the process exits, and once App runs it
    // reports its own failures. What arrives here happened while ClassScribe was being put together,
    // mostly an appsettings.json that cannot be read, before any logger exists; a stack trace would
    // bury the one line that matters, so the reason is printed plainly and becomes exit code 1.
    Console.Error.WriteLine(AppHost.DescribeFailure(ex, places));
    exitCode = 1;
}

// A window opened for this run alone (a double-click, or a file dropped on the exe) closes the moment
// the process ends, so it waits for Enter when there is something left to read: a command's result,
// or a failure. The menu, once left with Q, needs no pause.
if (ClosePrompt.IsNeeded(args, exitCode, interactive: !Console.IsInputRedirected, ownsWindow: ConsoleUi.OwnsConsoleWindow()))
{
    using var ui = new ConsoleUi();
    await new ClosePrompt(ui).WaitAsync();
}

return exitCode;
