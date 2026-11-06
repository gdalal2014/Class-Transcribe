using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Transcription;

/// <summary>
/// Registers the transcription's use cases and the services they drive (architecture 5.2): which glossary a run or a
/// transcript takes, the transcript layout, the workflow that joins them with its progress, and the report of what a
/// transcript cost. The ElevenLabs client, the account's reader and the glossary's files are registered with the adapters.
/// </summary>
public static class TranscriptionUseCases
{
    /// <summary>Adds the transcription's use cases and the services they drive.</summary>
    public static IServiceCollection AddTranscriptionUseCases(this IServiceCollection services)
    {
        // Which glossary a run or a transcript takes, with the settings of its scope.
        services.AddScoped<GlossaryChoice>();
        services.AddSingleton<TranscriptFormatter>();
        services.AddTransient<TranscriptionWorkflow>();
        services.AddScoped<TranscriptionProgress>();

        // The report of what a transcript cost, one per run, since the workflow begins it and the command ends it.
        services.AddScoped<UsageReport>();
        return services;
    }
}