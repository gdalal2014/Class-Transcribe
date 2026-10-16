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
/// Puts the app together and starts it: the settings from appsettings.json in the app's folder, with
/// environment overrides, read once at start; the API key from ELEVENLABS_API_KEY alone; the console
/// log format; the services. Then the settings check runs, before anything else.
/// </summary>
public static class AppHost
{
    /// <summary>The host setting that decides whether appsettings.json is read again each time the file changes.</summary>
    private const string ReloadOnChangeSetting = "hostBuilder:reloadConfigOnChange";

    /// <summary>
    /// Builds the host, with the settings read from <paramref name="contentRoot"/> once: an edit to
    /// appsettings.json counts from the next start. <paramref name="apiKey"/> is the value of
    /// ELEVENLABS_API_KEY, or null when it is not set; it is the only key ever used. An appsettings.json
    /// that is not valid JSON throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static IHost Build(string contentRoot, string? apiKey)
    {
        // The settings are read here, once, and checked once, in RunAsync. By default the host reads
        // appsettings.json again whenever it changes, and a value edited while ClassScribe runs, as the
        // menu stays open from class to class, would skip the check and could fail, or change the
        // transcript, only after the paid upload.
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([new(ReloadOnChangeSetting, "false")]);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = contentRoot, Configuration = configuration });

        // ELEVENLABS_API_KEY is the variable ElevenLabs' own samples use. Added last, it wins over every
        // other source, so a key written into appsettings.json is never used; SettingsCheck warns about one.
        builder.Configuration.AddApiKeyFromEnvironment(apiKey);

        // Plain one-line log output by default. Logging:Console:FormatterName = "simple" brings back full stack traces.
        var formatterName = builder.Configuration["Logging:Console:FormatterName"];
        builder.Logging.ClearProviders();
        builder.Logging
            .AddConsole(console => console.FormatterName = string.IsNullOrWhiteSpace(formatterName) ? PlainConsoleFormatter.FormatterName : formatterName)
            .AddConsoleFormatter<PlainConsoleFormatter, ConsoleFormatterOptions>();

        builder.Services.AddClassScribe(builder.Configuration);
        return builder.Build();
    }

    /// <summary>
    /// Runs the command line with the app's services. The settings are checked first, before anything
    /// that uses them is created, so a mistake in them stops ClassScribe before it records or uploads
    /// anything. They are not read again later (see <see cref="Build"/>), so the values checked are
    /// those used for the whole run. Returns the exit code.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (!services.GetRequiredService<SettingsCheck>().AreUsable()) return 1;

        return await services.GetRequiredService<App>().RunAsync(args);
    }

    /// <summary>
    /// Explains in plain words why the app could not be put together, mostly because appsettings.json
    /// cannot be read: one line per exception in the chain, and no stack trace. For a file that is not
    /// valid JSON it gives the line, counted from 1 as an editor counts, and says how to write a path,
    /// the usual cause.
    /// </summary>
    public static string DescribeFailure(Exception failure, string contentRoot)
    {
        var chain = new List<Exception>();
        for (var exception = failure; exception is not null; exception = exception.InnerException) chain.Add(exception);

        var text = new StringBuilder("ClassScribe could not start.");
        foreach (var exception in chain)
            text.AppendLine().Append("  ").Append(exception is JsonException json ? Describe(json) : exception.Message);

        if (chain.Any(exception => exception is JsonException))
            text.AppendLine().Append("In JSON, write each backslash in a path twice, as in \"D:\\\\Classes\", or use forward slashes, as in \"D:/Classes\".");
        else if (chain.Any(exception => exception.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase)))
            text.AppendLine().Append($"Check the settings in {Path.Combine(contentRoot, "appsettings.json")}.");

        return text.ToString();
    }

    /// <summary>A JSON error with its line counted from 1, rather than the parser's own count from 0.</summary>
    private static string Describe(JsonException json)
    {
        if (json.LineNumber is not { } line) return json.Message;

        var message = json.Message;
        var position = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return $"The mistake is on line {line + 1}: {(position >= 0 ? message[..position] : message)}";
    }
}
