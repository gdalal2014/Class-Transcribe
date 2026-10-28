using ClassScribe.Recorder;
using ClassScribe.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClassScribe.Commands;

/// <summary>
/// Registers the commands' use cases (architecture 5.2): what a run needs to record in this window or in a recorder (the
/// run itself, its recording loop and checks, its bookkeeping, how it shows its end, and the watch for times when
/// ClassScribe did not run), the starter of a run and the launcher of the recorder, transcribe, rebuild, finish-video, list,
/// devices, the microphone and camera checks, Google Drive's command, settings and class, and the report at the start that
/// reports nothing until the window's is registered. The menus, the console and the command line are the window's.
/// </summary>
public static class CommandUseCases
{
    /// <summary>Adds the commands' use cases and the services they drive.</summary>
    public static IServiceCollection AddCommandUseCases(this IServiceCollection services)
    {
        services.AddRun();
        services.AddTransient<TranscribeCommand>();
        services.AddTransient<RebuildCommand>();
        services.AddTransient<FinishVideoCommand>();

        // The microphone check of choice 8 and mic-check, as the run the runner resolves (see IMicCheckRun), which reads
        // Ctrl+C through the signals of its scope, as a transcription does.
        services.AddTransient<MicCheckCommand>();
        services.AddTransient<IMicCheckRun>(provider => provider.GetRequiredService<MicCheckCommand>());

        // Google Drive: the drive command, resolved by the runner in a scope of its own; the command reads Ctrl+C through
        // the signals of its scope.
        services.AddTransient<DriveCommand>();
        services.AddTransient<DriveUploadCommand>();

        // The commands of their own kind, settings and class, which answer on standard output and say what is wrong on
        // standard error. The window alone has the settings writer they need; a recorder never resolves them.
        services.AddTransient<SettingsCommand>();
        services.AddTransient<ClassCommand>();

        // Reports nothing until the real report at the start is registered; added only when no report is there yet.
        services.TryAddSingleton<IStartupReport, NoStartupReport>();

        // What starts a run, and what the window's side runs: made once per run, in the run's scope, when it takes the
        // settings of a run; the launcher, which reads the settings each time it uses them, is one for the process.
        services.AddScoped<IRecordStarter, RecordStarter>();
        services.AddTransient<ListCommand>();
        services.AddTransient<DevicesCommand>();
        services.AddTransient<CameraCheckCommand>();
        services.AddSingleton<RecorderLauncher>();
        return services;
    }

    /// <summary>
    /// Adds what one run needs, in this window and in a recorder alike: the record command, as the run; its loop and the
    /// checks it makes once a second; its bookkeeping and the context its starter fills; the watch for times when
    /// ClassScribe did not run; and how a run in this window shows its end. Each run resolves these in a scope of its own,
    /// so it gets its own. A recorder's host replaces what differs there: the result shower.
    /// </summary>
    private static IServiceCollection AddRun(this IServiceCollection services)
    {
        services.AddScoped<FreezeWatch>();
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