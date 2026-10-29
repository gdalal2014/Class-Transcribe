using ClassScribe.Desktop;
using ClassScribe.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ClassScribe.Settings;

namespace ClassScribe.Commands;

/// <summary>
/// Registers the window's side of the commands (architecture 5.2): the console and the person's Ctrl+C, the gate, the
/// runner, the menu and its screens (the microphone menu, Google Drive's menu, the classes' and the settings' menus,
/// YouTube's menu with what it remembers), the output of settings and class, the command line, the app that puts them
/// together, and the status sink a run in this window shows its states through. The use cases are registered apart (see
/// <see cref="CommandUseCases"/>).
/// </summary>
public static class CommandServices
{
    /// <summary>Adds the commands' side that talks to the person at the computer.</summary>
    public static IServiceCollection AddCommands(this IServiceCollection services)
    {
        services.AddSingleton<IConsoleUi, ConsoleUi>();

        // One per run: each recording or transcription gets fresh Ctrl+C signals (see ICommandRunner).
        services.AddScoped<IUserSignals, ConsoleUserSignals>();

        // One for the process, so that a warning about the settings is given once.
        services.AddSingleton<RunGate>();

        // A run in this window shows its states in the window's title and taskbar button, and flashes once for each new
        // Alert, with a status sink of its own; a recorder's host replaces it with the link's.
        services.AddScoped<IRecorderStatusSink, WindowStatusSink>();

        services.AddSingleton<ICommandRunner, CommandRunner>();

        // The runner is the run starter a YouTube batch records each of its videos through, in a scope of the run's own.
        services.AddSingleton<IRunStarter>(provider => provider.GetRequiredService<ICommandRunner>());
        services.AddSingleton<RecordingNamePrompt>();
        services.AddTransient<MenuCommand>();

        // Menu choice 7, which the runner resolves in a scope of its own, as it does the other commands.
        services.AddTransient<MicrophoneMenu>();

        // Google Drive: menu choice 9, resolved by the runner in a scope of its own.
        services.AddTransient<DriveMenu>();

        // YouTube: menu choice Y, with the course it remembers for the process.
        services.AddSingleton<YouTubeMenuMemory>();
        services.AddScoped<YouTubeMenu>();

        // What settings and class answer on standard output and say is wrong on standard error, read at the moment the
        // output is made, so that a UTF-8 writer Program put in place for redirected output is the one used; and the two
        // that write the use cases' answers there, as text or JSON (architecture question 4).
        services.AddSingleton(_ => new CommandOutput(Console.Out, Console.Error));
        services.AddTransient<SettingsOutput>();
        services.AddTransient<ClassOutput>();
        services.AddTransient<ClassesMenu>();
        services.AddTransient<SettingsMenu>();

        // The command line writes its help to standard output and its mistakes to standard error, as a script expects; it
        // reads the saved classes and the recordings folder as a command runs.
        services.AddTransient(provider => new CommandLineTree(
            provider.GetRequiredService<ICommandRunner>(),
            provider.GetRequiredService<MenuCommand>(),
            provider.GetRequiredService<IConsoleUi>(),
            provider.GetRequiredService<TimeProvider>(),
            Console.Out,
            Console.Error,
            provider.GetRequiredService<ISavedClasses>(),
            provider.GetRequiredService<IOptionsMonitor<RecordingOptions>>()));
        services.AddTransient<App>();
        return services;
    }
}