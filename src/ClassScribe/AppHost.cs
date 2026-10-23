using System.Text;
using System.Text.Json;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ClassScribe;

/// <summary>
/// Puts the app together and starts it: the settings from appsettings.json, the user's own copy when there is one and
/// else the one in the app's folder (see <see cref="SettingsPlaces"/>), once they have passed the check, with environment
/// overrides; the API key from ELEVENLABS_API_KEY alone; the console log format; the services. The settings are read again
/// and checked before each menu choice and each command (see <see cref="Commands.RunGate"/>). The recorder, started by a
/// window to record one class in the background, is put together from the settings that window handed over instead (see
/// <see cref="BuildRecorder"/>).
/// </summary>
public static class AppHost
{
    /// <summary>
    /// Builds the host. The appsettings.json that <paramref name="places"/> names is read once here and checked; settings
    /// that pass are used from the start, and when they do not, the defaults stand in until the file is fixed. A file that
    /// is not valid JSON no longer stops the start: the menu's header shows what is wrong, and the gate before the first
    /// choice or command says it again. The app's content root is the program's folder, whichever file is read.
    /// <paramref name="apiKey"/> is the value of ELEVENLABS_API_KEY, or null when it is not set; it is the only key ever
    /// used. <paramref name="configureServices"/>, when given, changes the services last, as a test does to put stand-ins in place.
    /// </summary>
    public static IHost Build(SettingsPlaces places, string? apiKey, Action<IServiceCollection>? configureServices = null)
    {
        // Without the host's defaults, it adds no settings sources of its own, so it never reads appsettings.json itself:
        // a file that is not valid JSON, or one another program holds, would otherwise stop the start, and a file the
        // host read would reach the settings unchecked. Nothing watches the file either.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = places.ProgramFolder, DisableDefaults = true });

        // The settings, lowest first. The defaults that only the file used to hold (the Logging section), so that a file
        // without them, or no file, logs as the shipped one does. Then appsettings.json, which comes in only through
        // CheckedSettingsSource, which holds what last passed the check and never reads the disk: SettingsReloader reads the
        // file and checks a staged copy before each menu choice and each command. Then environment variables, such as
        // ElevenLabs__BaseUrl. Last the key from ELEVENLABS_API_KEY, the variable ElevenLabs' own samples use: it wins over
        // every other source, so a key written into appsettings.json is never used; SettingsCheck warns about one.
        var accepted = new CheckedSettingsSource();
        builder.Configuration.AddInMemoryCollection(DefaultSettings.Values);
        builder.Configuration.Sources.Add(accepted);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddApiKeyFromEnvironment(apiKey);

        // Plain one-line log output by default. Logging:Console:FormatterName = "simple" brings back full stack traces.
        // The Logging section is read again with the other settings, so a change to it counts from the next choice too.
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging
            .AddConsole(console => console.FormatterName = string.IsNullOrWhiteSpace(console.FormatterName) ? PlainConsoleFormatter.FormatterName : console.FormatterName)
            .AddConsoleFormatter<PlainConsoleFormatter, ConsoleFormatterOptions>();

        builder.Services.AddClassScribe(builder.Configuration).AddSettingsReload(builder.Configuration, accepted, places);
        configureServices?.Invoke(builder.Services);

        // A per-run service taken outside a run's scope, or a service that cannot be made, is a mistake that shows at once.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        var host = builder.Build();
        try
        {
            // Which file is read was decided once, as the places were made; the menu's header names it, and the log keeps it.
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AppHost).FullName!)
                .LogDebug("Reading the settings from {Path} ({Origin})", places.SettingsPath, places.Origin == SettingsOrigin.UserFolder ? "your own copy" : "the file beside the program");

            // The first checked read. The reloader keeps its verdict: what it finds wrong with the file is shown in the menu's
            // header, and said by the gate before the first choice or command.
            host.Services.GetRequiredService<SettingsReloader>().TryReload();

            // Environment variables reach the settings without the file's check, and unlike the file they cannot be fixed
            // while ClassScribe runs: every part that reads the settings would fail on a value from one that cannot be
            // used, the menu's header first. So such a value stops the start, with the reason.
            if (host.Services.GetRequiredService<SettingsCheck>().FindProblems(builder.Configuration) is [var first, ..] problems)
            {
                var variable = first.Split(' ')[0].Replace(":", "__", StringComparison.Ordinal);
                throw new InvalidOperationException(
                    $"An environment variable sets a value ClassScribe cannot use. {string.Join(" ", problems)} The variable is named after the setting, with __ for each colon, as in {variable}: change or remove it, then start ClassScribe again.");
            }

            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the recorder's host: from the settings the window handed over in <paramref name="start"/>, which it had read
    /// and checked, and the key in the recorder's own environment (<paramref name="apiKey"/>, the value of
    /// ELEVENLABS_API_KEY, or null when it is not set), and nothing else: no appsettings.json, which is never read here, and
    /// no environment variables, whose overrides the snapshot carries already. The settings are checked once more, as a
    /// guard against a build that differs from the window's: a value the check refuses throws an
    /// <see cref="InvalidOperationException"/> naming it. The recorder has no console, so every logger writes into
    /// <paramref name="log"/> alone, at the levels the snapshot's Logging section names; <paramref name="folder"/> is
    /// ClassScribe's own folder, <paramref name="places"/> where the glossary is looked for, the same answer as the window's
    /// (see <see cref="SettingsPlaces"/>), and <paramref name="time"/> the clock, the computer's unless given. The run's
    /// services are those of a window, with the recorder's own in place of what differs there (see <see cref="AddRecorder"/>).
    /// <paramref name="configureServices"/>, when given, changes the services last, as a test does.
    /// </summary>
    public static IHost BuildRecorder(RecorderStart start, string? apiKey, RecorderLog log, RuntimeFolder folder, SettingsPlaces places, TimeProvider? time = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = places.ProgramFolder, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(start.Settings);
        builder.Configuration.AddApiKeyFromEnvironment(apiKey);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        builder.Services.AddClassScribe(builder.Configuration).AddRecorder(log, folder, places, time ?? TimeProvider.System);
        configureServices?.Invoke(builder.Services);

        // The window's services that a recorder never uses, such as the settings gate, need what only the window registers,
        // so the container is not validated as a whole here: what the run needs is resolved in its scope, and tested.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true }));
        var host = builder.Build();
        try
        {
            if (host.Services.GetRequiredService<SettingsCheck>().FindProblems(builder.Configuration) is [_, ..] problems)
            {
                throw new InvalidOperationException(
                    $"The settings handed to the recorder cannot be used: {string.Join(" ", problems)} The window that started it had checked them, so this build of ClassScribe differs from the window's.");
            }

            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs the command line with the app's services and returns the exit code. The settings are read again and checked
    /// before each menu choice and each command, before anything that uses them runs, so a mistake in them stops that
    /// choice or command before it records or uploads anything.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args) =>
        await services.GetRequiredService<App>().RunAsync(args);

    /// <summary>
    /// Sends every console log line to standard error, as settings and class do with --json, so that standard output holds
    /// the JSON alone (layout-config 1.8). Program passes it to <see cref="Build"/> as the services' last change.
    /// </summary>
    public static void LogToStandardError(IServiceCollection services) =>
        services.Configure<ConsoleLoggerOptions>(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

    /// <summary>
    /// Puts the recorder's own services in place of the window's: the clock, the log every logger writes to, ClassScribe's
    /// own folder and the places of the settings and the glossary as given; the link's console, whose keys come from the
    /// windows; no window signals, since a recorder has no window; for each run, the link's status sink, which sends the
    /// states to the windows and the alerts, and the recorder's result shower; and, for the process, the server, the alerts
    /// that bring a window back, and the starter of that window.
    /// </summary>
    private static IServiceCollection AddRecorder(this IServiceCollection services, RecorderLog log, RuntimeFolder folder, SettingsPlaces places, TimeProvider time)
    {
        services.Replace(ServiceDescriptor.Singleton(time));
        services.Replace(ServiceDescriptor.Singleton(log));
        services.Replace(ServiceDescriptor.Singleton(folder));
        services.AddSingleton(places);
        services.Replace(ServiceDescriptor.Singleton<IConsoleUi, LinkConsoleUi>());
        services.Replace(ServiceDescriptor.Singleton<IWindowSignals, NoWindowSignals>());
        services.Replace(ServiceDescriptor.Scoped<IRecorderStatusSink, LinkStatusSink>());
        services.AddScoped<WindowResultShower>();
        services.AddScoped<RecorderResultShower>();
        services.Replace(ServiceDescriptor.Scoped<IResultShower>(provider => provider.GetRequiredService<RecorderResultShower>()));
        services.AddSingleton<RecorderServer>();
        services.AddSingleton<UnattendedAlerts>();
        services.AddSingleton<IProcessStarter>(provider => OperatingSystem.IsWindows()
            ? new ProcessStarter(folder.Root, provider.GetRequiredService<ILogger<ProcessStarter>>())
            : throw new PlatformNotSupportedException("Opening a ClassScribe window from the recorder needs Windows."));
        return services;
    }

    /// <summary>
    /// Explains in plain words why the app could not be put together: one line per exception in the chain, and no stack
    /// trace. For a file that is not valid JSON it gives the line, counted from 1 as an editor counts, and says how to
    /// write a path, the usual cause. For a configuration error it names the settings file that was read, as
    /// <paramref name="places"/> decided it.
    /// </summary>
    public static string DescribeFailure(Exception failure, SettingsPlaces places)
    {
        var chain = new List<Exception>();
        for (var exception = failure; exception is not null; exception = exception.InnerException) chain.Add(exception);

        var text = new StringBuilder("ClassScribe could not start.");
        foreach (var exception in chain)
            text.AppendLine().Append("  ").Append(exception is JsonException json ? SettingsReloader.DescribeJsonMistake(json) : exception.Message);

        if (chain.Any(exception => exception is JsonException))
            text.AppendLine().Append(SettingsReloader.JsonPathAdvice);
        else if (chain.Any(exception => exception.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase)))
            text.AppendLine().Append($"Check the settings in {places.SettingsPath}.");

        return text.ToString();
    }
}
