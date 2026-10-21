using System.CommandLine;
using ClassScribe.Settings;

namespace ClassScribe.Commands;

/// <summary>The settings command of the command line (layout-config 1.8): path, list, get, set, reset, open and fill.</summary>
public sealed partial class CommandLineTree
{
    /// <summary>The command words of the commands of their own kind, which run without the gate and the report at the start.</summary>
    private static readonly string[] OwnKindWords = ["settings", "class"];

    /// <summary>
    /// True when <paramref name="args"/> start with a command of its own kind, settings or class, in any case: Program then
    /// writes their redirected output in UTF-8, so that PowerShell reads names such as Ṣughrā as they are (layout-config 1.8).
    /// </summary>
    public static bool IsOwnKind(string[] args) => args is [var first, ..] && OwnKindWords.Contains(first.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="args"/> ask a command of its own kind for JSON (--json): Program then sends every log line
    /// to standard error, so that standard output holds the JSON alone.
    /// </summary>
    public static bool WantsJson(string[] args) => IsOwnKind(args) && args.Any(argument => argument.Trim().Equals("--json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// settings: path; list and get, each with --json; set with the setting and its value, a list's items one by one; reset
    /// with the setting, or --all; open; fill. settings alone is a mistake that names them all. A mistake found as the command
    /// runs, such as an unknown setting, a value the check refuses or the ElevenLabs key, is exit 2 with the command's help.
    /// </summary>
    private Command SettingsCommandTree(Dictionary<Command, string> examples)
    {
        var path = new Command("path", "Show the full path of your settings file, alone on the line.");
        path.SetAction(parsed => RunSettingsAsync(parsed, new SettingsRequest.ShowPath()));
        examples[path] = SettingsExamples;

        var listJson = JsonOption();
        var list = new Command("list", "List every setting: its value, its default when the value differs, and where the value comes from.") { listJson };
        list.SetAction(parsed => RunSettingsAsync(parsed, new SettingsRequest.List(parsed.GetValue(listJson))));
        examples[list] = SettingsExamples;

        var getName = new Argument<string>("setting") { Description = "The setting, such as Recording:OutputFolder." };
        var getJson = JsonOption();
        var get = new Command("get", "Show one setting's value, alone: a list's items a line each.") { getName, getJson };
        get.SetAction(parsed => RunSettingsAsync(parsed, new SettingsRequest.Get(parsed.GetValue(getName)!, parsed.GetValue(getJson))));
        examples[get] = SettingsExamples;

        var setName = new Argument<string>("setting") { Description = "The setting, such as Recording:OutputFolder, or an entry such as Logging:LogLevel:ClassScribe." };
        var setValues = new Argument<string[]>("value") { Arity = ArgumentArity.ZeroOrMore, Description = "Its value; for a list, all its items, each in its own quotes." };
        var set = new Command("set", "Change one setting in your settings file, made at your first change as a full copy of every setting.") { setName, setValues };
        set.Validators.Add(result =>
        {
            if (result.GetValue(setValues) is not { Length: > 0 })
                result.AddError("settings set needs the setting and its value, such as ClassScribe settings set Recording:OutputFolder \"D:\\Classes\"; settings reset puts a setting back at its default");
        });
        set.SetAction(parsed => RunSettingsAsync(parsed, new SettingsRequest.Set(parsed.GetValue(setName)!, parsed.GetValue(setValues) ?? [])));
        examples[set] = SettingsExamples;

        var resetName = new Argument<string?>("setting") { Arity = ArgumentArity.ZeroOrOne, Description = "The setting to put back at its default." };
        var all = new Option<bool>("--all") { Description = "Every setting back at its default; your saved classes are kept, and the file before is kept as appsettings.previous.json." };
        var reset = new Command("reset", "Put a setting back at its default, written into your file; or with --all, every setting.") { resetName, all };
        reset.Validators.Add(result =>
        {
            var named = !string.IsNullOrWhiteSpace(TokenOf(result.GetResult(resetName)));
            if (named == result.GetValue(all))
                result.AddError("settings reset needs one setting, such as ClassScribe settings reset Recording:Device, or --all for every one, not both");
        });
        reset.SetAction(parsed => RunSettingsAsync(parsed, new SettingsRequest.Reset(parsed.GetValue(all) ? null : parsed.GetValue(resetName))));
        examples[reset] = SettingsExamples;

        var open = new Command("open", "Open your settings file in Notepad, made first as a full copy if you have none yet.");
        open.SetAction(parsed => RunSettingsAsync(parsed, new SettingsRequest.Open()));
        examples[open] = SettingsExamples;

        var fill = new Command("fill", "Add to your settings file every setting this ClassScribe knows and the file lacks, at its default. Never makes a file.");
        fill.SetAction(parsed => RunSettingsAsync(parsed, new SettingsRequest.Fill()));
        examples[fill] = SettingsExamples;

        var settings = new Command("settings", "Show or change your settings from PowerShell: path, list, get, set, reset, open, fill.") { path, list, get, set, reset, open, fill };
        settings.Validators.Add(result =>
        {
            if (!result.Children.OfType<System.CommandLine.Parsing.CommandResult>().Any())
                result.AddError("settings needs one of path, list, get, set, reset, open or fill, such as ClassScribe settings list");
        });

        // Never run: the validator above refuses settings alone.
        settings.SetAction(_ => Mistake);
        examples[settings] = SettingsExamples;
        return settings;
    }

    /// <summary>--json, for list, get and show: JSON alone on standard output, every log line on standard error.</summary>
    private static Option<bool> JsonOption() => new("--json")
    {
        Description = "Write JSON alone on standard output, every character beyond ASCII escaped, for PowerShell's ConvertFrom-Json."
    };

    /// <summary>Runs <paramref name="request"/> through the runner; a mistake it finds, exit 2, is followed by the command's help.</summary>
    private async Task<int> RunSettingsAsync(System.CommandLine.ParseResult parsed, SettingsRequest request)
    {
        var exitCode = await commands.SettingsAsync(request);
        if (exitCode == Mistake) WriteHelp(parsed);
        return exitCode;
    }

    /// <summary>The examples and notes under the help of settings and its commands.</summary>
    private const string SettingsExamples = """
        Examples:
          ClassScribe settings path
          ClassScribe settings list
          ClassScribe settings list --json | ConvertFrom-Json
          ClassScribe settings get Recording:OutputFolder
          ClassScribe settings set Recording:OutputFolder "D:\Classes"
          ClassScribe settings set YouTube:CourseChannels "@Channel1" "@Channel2"
          ClassScribe settings set Logging:LogLevel:ClassScribe.Drive Debug
          ClassScribe settings reset Recording:Device
          ClassScribe settings reset --all
          ClassScribe settings open
          ClassScribe settings fill

        Your settings file is a full copy of every setting, made at your first change; an update of ClassScribe never changes it, and a later one adds its new settings to it at their defaults.
        The ElevenLabs key is never a setting: it is read from ELEVENLABS_API_KEY alone.
        To read names such as Ṣughrā through a pipe, first run: [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
        Exit codes: 0 done; 1 a file that is not valid JSON, has comments of your own, cannot be written, keeps changing or is missing; 2 an unknown setting, the key, a value the check refuses, or another mistake.
        """;
}
