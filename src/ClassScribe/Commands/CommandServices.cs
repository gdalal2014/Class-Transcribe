using ClassScribe.Recorder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// Registers the console, the desktop, the gate, the commands, the menu, the command line, the report at the start and
/// the app that puts them together; and what a run needs to record in this window or in a recorder: the run itself, its
/// recording loop and checks, its bookkeeping, how it shows its end, and the recorder's log.
/// </summary>
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

        services.AddRun();
        services.AddTransient<TranscribeCommand>();
        services.AddTransient<RebuildCommand>();
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<RecordingNamePrompt>();
        services.AddTransient<MenuCommand>();

        // Reports nothing until the real report at the start is registered; added only when no report is there yet.
        services.TryAddSingleton<IStartupReport, NoStartupReport>();

        // The command line writes its help to standard output and its mistakes to standard error, as a script expects.
        services.AddTransient(provider => new CommandLineTree(
            provider.GetRequiredService<ICommandRunner>(),
            provider.GetRequiredService<MenuCommand>(),
            provider.GetRequiredService<IConsoleUi>(),
            provider.GetRequiredService<TimeProvider>(),
            Console.Out,
            Console.Error));
        services.AddTransient<App>();
        return services;
    }

    /// <summary>
    /// Adds what one run needs, in this window and in a recorder alike: the record command, as the run; its loop and the
    /// checks it makes once a second; its bookkeeping and the context its starter fills; and how a run in this window shows
    /// its end. Each run resolves these in a scope of its own, so it gets its own. A recorder's host replaces what differs
    /// there: the console, the status sink and the result shower.
    /// </summary>
    private static IServiceCollection AddRun(this IServiceCollection services)
    {
        // The recorder's log, to which every logger writes as well as to the console: the run names its file beside the
        // recording, <base>.log. A window keeps the lines between runs in memory only; a recorder's host replaces it with
        // one that writes those to recorder.log. One provider however often this is called.
        services.AddSingleton(provider => new RecorderLog(provider.GetRequiredService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RecorderLogProvider>());

        // .NET's warning of an error nobody caught, which a run hears before the exit warnings, to write down the right cause.
        services.AddSingleton<ICrashWarnings, AppDomainCrashWarnings>();

        services.AddScoped<RunContextSlot>();
        services.AddScoped<RunBookkeeping>();
        services.AddScoped<RecordingMonitor>();
        services.AddScoped<RecordingLoop>();
        services.AddScoped<IResultShower, WindowResultShower>();
        services.AddTransient<RecordCommand>();
        services.AddTransient<IRecordRun>(provider => provider.GetRequiredService<RecordCommand>());
        return services;
    }
}
