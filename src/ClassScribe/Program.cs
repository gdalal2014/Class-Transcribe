using ClassScribe;
using ClassScribe.Commands;
using ClassScribe.Settings;

// First, before anything writes to the console, so that the log shows Arabic, transliteration and
// device names such as "Intel®" as they are.
ConsoleUi.UseUnicode();

// Settings are read from the folder the app lives in, whatever folder it is started from.
var contentRoot = AppContext.BaseDirectory;
int exitCode;
try
{
    using var host = AppHost.Build(contentRoot, Environment.GetEnvironmentVariable(ElevenLabsOptions.ApiKeyVariable));
    exitCode = await AppHost.RunAsync(host.Services, args);
}
catch (Exception ex)
{
    // Deliberately not rethrown: this is the last stop before the process exits, and once App runs it
    // reports its own failures. What arrives here happened while ClassScribe was being put together,
    // mostly an appsettings.json that cannot be read, before any logger exists; a stack trace would
    // bury the one line that matters, so the reason is printed plainly and becomes exit code 1.
    Console.Error.WriteLine(AppHost.DescribeFailure(ex, contentRoot));
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
