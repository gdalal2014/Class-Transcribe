using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Commands;

/// <summary>Registers the console, the desktop, the gate, the commands, the menu and the app that picks between them.</summary>
public static class CommandServices
{
    /// <summary>Adds the commands and what they use to talk to the person at the computer.</summary>
    public static IServiceCollection AddCommands(this IServiceCollection services)
    {
        services.AddSingleton<IConsoleUi, ConsoleUi>();
        services.AddSingleton<IDesktop, WindowsDesktop>();
        services.AddSingleton<IExitWarnings, ProcessExitWarnings>();

        // One per run: each recording or transcription gets fresh Ctrl+C signals (see ICommandRunner).
        services.AddScoped<IUserSignals, ConsoleUserSignals>();

        // One for the process, so that a warning about the settings is given once.
        services.AddSingleton<RunGate>();

        services.AddTransient<RecordCommand>();
        services.AddTransient<TranscribeCommand>();
        services.AddTransient<RebuildCommand>();
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<RecordingNamePrompt>();
        services.AddTransient<MenuCommand>();
        services.AddTransient<App>();
        return services;
    }
}
