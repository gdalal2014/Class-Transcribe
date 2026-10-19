using System.Collections;
using ClassScribe.Settings;

namespace ClassScribe.Desktop;

/// <summary>
/// The environment ClassScribe gives a program it starts that is not ClassScribe: yt-dlp and its helpers, and Chrome for the
/// video window (design 3.2 and 4.1). It is this process's environment without the ElevenLabs key, so that the key never
/// reaches another program; the recorder alone, which is ClassScribe, inherits the environment as it is.
/// </summary>
public static class ChildEnvironment
{
    /// <summary>
    /// <paramref name="current"/>, this process's environment, without ELEVENLABS_API_KEY or any setting of the ElevenLabs
    /// section given as an environment variable (ElevenLabs__...), in any letter case; Windows' hidden per-drive variables,
    /// whose names start with =, and nameless ones are left out too.
    /// </summary>
    public static Dictionary<string, string> WithoutTheKey(IDictionary current)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in current)
        {
            if (variable.Key is not string name || name.Length == 0 || name.StartsWith('=')) continue;
            if (name.Equals(ElevenLabsOptions.ApiKeyVariable, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith(ElevenLabsOptions.SectionName + "__", StringComparison.OrdinalIgnoreCase)) continue;

            environment[name] = variable.Value as string ?? "";
        }

        return environment;
    }
}
