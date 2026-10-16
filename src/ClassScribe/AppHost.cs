using System.Text;
using System.Text.Json;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ClassScribe;

/// <summary>
/// Puts the app together and starts it: the settings from appsettings.json in the app's folder, once they have passed
/// the check, with environment overrides; the API key from ELEVENLABS_API_KEY alone; the console log format; the
/// services. The settings are read again and checked before each menu choice and each command (see
/// <see cref="Commands.RunGate"/>).
/// </summary>
public static class AppHost
{
    /// <summary>
    /// Builds the host. appsettings.json in <paramref name="contentRoot"/> is read once here and checked; settings that
    /// pass are used from the start, and when they do not, the defaults stand in until the file is fixed. A file that is
    /// not valid JSON no longer stops the start: the menu's header shows what is wrong, and the gate before the first
    /// choice or command says it again.
    /// <paramref name="apiKey"/> is the value of ELEVENLABS_API_KEY, or null when it is not set; it is the only key ever
    /// used. <paramref name="configureServices"/>, when given, changes the services last, as a test does to put stand-ins in place.
    /// </summary>
    public static IHost Build(string contentRoot, string? apiKey, Action<IServiceCollection>? configureServices = null)
    {
        // Without the host's defaults, it adds no settings sources of its own, so it never reads appsettings.json itself:
        // a file that is not valid JSON, or one another program holds, would otherwise stop the start, and a file the
        // host read would reach the settings unchecked. Nothing watches the file either.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = contentRoot, DisableDefaults = true });

        // The settings, lowest first. appsettings.json comes in only through CheckedSettingsSource, which holds what last
        // passed the check and never reads the disk: SettingsReloader reads the file and checks a staged copy before each
        // menu choice and each command. Then environment variables, such as ElevenLabs__BaseUrl. Last the key from
        // ELEVENLABS_API_KEY, the variable ElevenLabs' own samples use: it wins over every other source, so a key written
        // into appsettings.json is never used; SettingsCheck warns about one.
        var accepted = new CheckedSettingsSource();
        builder.Configuration.Sources.Add(accepted);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddApiKeyFromEnvironment(apiKey);

        // Plain one-line log output by default. Logging:Console:FormatterName = "simple" brings back full stack traces.
        // The Logging section is read again with the other settings, so a change to it counts from the next choice too.
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging
            .AddConsole(console => console.FormatterName = string.IsNullOrWhiteSpace(console.FormatterName) ? PlainConsoleFormatter.FormatterName : console.FormatterName)
            .AddConsoleFormatter<PlainConsoleFormatter, ConsoleFormatterOptions>();

        builder.Services.AddClassScribe(builder.Configuration).AddSettingsReload(builder.Configuration, accepted, contentRoot);
        configureServices?.Invoke(builder.Services);

        // A per-run service taken outside a run's scope, or a service that cannot be made, is a mistake that shows at once.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        var host = builder.Build();
        try
        {
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
    /// Runs the command line with the app's services and returns the exit code. The settings are read again and checked
    /// before each menu choice and each command, before anything that uses them runs, so a mistake in them stops that
    /// choice or command before it records or uploads anything.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args) =>
        await services.GetRequiredService<App>().RunAsync(args);

    /// <summary>
    /// Explains in plain words why the app could not be put together: one line per exception in the chain, and no stack
    /// trace. For a file that is not valid JSON it gives the line, counted from 1 as an editor counts, and says how to
    /// write a path, the usual cause.
    /// </summary>
    public static string DescribeFailure(Exception failure, string contentRoot)
    {
        var chain = new List<Exception>();
        for (var exception = failure; exception is not null; exception = exception.InnerException) chain.Add(exception);

        var text = new StringBuilder("ClassScribe could not start.");
        foreach (var exception in chain)
            text.AppendLine().Append("  ").Append(exception is JsonException json ? SettingsReloader.DescribeJsonMistake(json) : exception.Message);

        if (chain.Any(exception => exception is JsonException))
            text.AppendLine().Append(SettingsReloader.JsonPathAdvice);
        else if (chain.Any(exception => exception.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase)))
            text.AppendLine().Append($"Check the settings in {Path.Combine(contentRoot, SettingsFile.FileName)}.");

        return text.ToString();
    }
}
