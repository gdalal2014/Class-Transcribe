using ClassScribe;
using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

// Settings are read from the folder the app lives in, whatever folder it is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });

// ELEVENLABS_API_KEY is the variable ElevenLabs' own samples use. A key in configuration wins over it.
var apiKey = Environment.GetEnvironmentVariable(ElevenLabsOptions.ApiKeyVariable);
if (!string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(builder.Configuration[$"{ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.ApiKey)}"]))
{
    builder.Configuration.AddInMemoryCollection(
        [new KeyValuePair<string, string?>($"{ElevenLabsOptions.SectionName}:{nameof(ElevenLabsOptions.ApiKey)}", apiKey.Trim())]);
}

// Plain one-line log output by default. Logging:Console:FormatterName = "simple" brings back full stack traces.
var formatterName = builder.Configuration["Logging:Console:FormatterName"];
builder.Logging.ClearProviders();
builder.Logging
    .AddConsole(console => console.FormatterName = string.IsNullOrWhiteSpace(formatterName) ? PlainConsoleFormatter.FormatterName : formatterName)
    .AddConsoleFormatter<PlainConsoleFormatter, ConsoleFormatterOptions>();

builder.Services.AddClassScribe(builder.Configuration);

int exitCode;
using (var host = builder.Build())
{
    exitCode = await host.Services.GetRequiredService<App>().RunAsync(args);
}

// When started by double-click, keep the window open so the result can be read.
if (args.Length == 0 && !Console.IsInputRedirected)
{
    Console.WriteLine();
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
}

return exitCode;
