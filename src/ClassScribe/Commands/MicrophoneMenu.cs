using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Menu choice 7, run through <see cref="ICommandRunner.ChooseMicrophoneAsync"/>: asks whether to record your microphone
/// with the class (W), alone in person (I) or not at all (N); then lists the microphones Windows can record from, with
/// notes, for a number to pick one, or Enter to leave it to Recording:Microphone and Windows' default microphone, never a
/// Bluetooth one; and with the class, which app the microphone follows: Enter keeps the app in force, a name picks
/// another, A any app that holds the microphone, N none (V alone). The pick holds until ClassScribe is closed, whatever
/// the settings say (see <see cref="SourceChoice"/>), and the menu says which settings would keep it. Listing the
/// microphones reads what Windows says about them; nothing is opened. Esc at any question goes back without a change.
/// </summary>
public sealed class MicrophoneMenu(IMicrophoneProvider microphones, SourceChoice choice, SettingsFile settings, IOptionsSnapshot<RecordingOptions> recording, IConsoleUi ui)
{
    /// <summary>How many microphones are offered, so that one digit picks one.</summary>
    private const int MostListed = 9;

    /// <summary>The name of ClassScribe's own process, which the microphone must never follow.</summary>
    private const string OwnProcessName = "ClassScribe";

    /// <summary>Asks the questions, keeps the answers for the session and says which settings keep them. Returns the exit code, 0.</summary>
    public async Task<int> RunAsync()
    {
        ui.WriteLine("Your microphone:");
        ui.WriteLine("  W  With the class: the class and your microphone, in one transcript");
        ui.WriteLine("  I  In person: your microphone alone, for a class in the room");
        ui.WriteLine("  N  None: the class alone");
        ui.WriteLine("Press W, I or N, or Esc to go back.");
        if (await ReadModeAsync() is not { } mode) return 0;

        if (mode == RecordingMode.Class)
        {
            choice.PickMicrophone(RecordingMode.Class, namePart: null, follows: null);
            ui.WriteLine($"{Pinned()} To keep that, set Recording:Mode to \"{RecordingMode.Class}\" in {settings.Path}.");
            return 0;
        }

        var listed = microphones.GetMicrophones().Where(microphone => !microphone.IsLoopbackInput).Take(MostListed).ToList();
        ui.WriteLine("Microphones:");
        if (listed.Count == 0) ui.WriteLine("  None is connected now; the recording waits for one.");
        for (var i = 0; i < listed.Count; i++)
            ui.WriteLine($"  {i + 1}  {Describe(listed[i])}");

        var fromSettings = Normalise(recording.Value.Microphone);
        ui.WriteLine(fromSettings is null
            ? "Press a number, Enter for Windows' default microphone (never a Bluetooth one), or Esc to go back."
            : $"Press a number, Enter for the microphone Recording:Microphone names, \"{fromSettings}\", or Esc to go back.");
        var (back, picked) = await ReadMicrophoneAsync(listed);
        if (back) return 0;

        FollowRule? follows = null;
        if (mode == RecordingMode.ClassAndMicrophone)
        {
            var inForce = choice.Follows;
            ui.WriteLine($"Follow the class app: Enter keeps {DescribeInForce(inForce)}, type another app's name, A follows any app that holds the microphone, N follows none (V only).");
            ui.Write("> ");
            if (await ui.ReadLineAsync(CancellationToken.None) is not { } answer) return 0;

            follows = Follow(answer.Trim(), inForce);
        }

        choice.PickMicrophone(mode, picked?.Name, follows);
        ui.WriteLine($"{Pinned()} To keep that, {Keep(mode, picked, follows, fromSettings)} in {settings.Path}.");
        return 0;
    }

    /// <summary>
    /// A microphone as the list shows it: its name, then its notes: built in for the laptop's own array, default for
    /// Windows' default microphone, raw when it offers audio without Windows' sound processing, USB for one on USB, and
    /// for a Bluetooth one that it is never chosen unless picked here, since using it switches earbuds to call quality.
    /// </summary>
    internal static string Describe(Microphone microphone)
    {
        var notes = string.Join(", ", new[]
        {
            microphone.IsBuiltInArray ? "built in" : null,
            microphone.IsDefault ? "default" : null,
            microphone.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : null,
            microphone.RawSupported ? "raw" : null,
            microphone.IsBluetooth ? "Bluetooth: never chosen unless picked here; call-quality sound" : null,
            microphone.IsLoopbackInput ? "records what the computer plays; never recorded" : null
        }.OfType<string>());
        return notes.Length > 0 ? $"{microphone.Name}  ({notes})" : microphone.Name;
    }

    /// <summary>Waits for W, I or N and returns the mode it means; null for Esc, Q, Ctrl+C, or when nobody can type. Other keys are ignored.</summary>
    private async Task<RecordingMode?> ReadModeAsync()
    {
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return null;

            switch (pressed.Key)
            {
                case ConsoleKey.W:
                    return RecordingMode.ClassAndMicrophone;
                case ConsoleKey.I:
                    return RecordingMode.Microphone;
                case ConsoleKey.N:
                    return RecordingMode.Class;
            }
        }
    }

    /// <summary>
    /// Waits for a number among <paramref name="listed"/>, which picks that microphone, or Enter, which picks none and
    /// leaves the microphone to the settings; Esc, Q, Ctrl+C, or nobody able to type, goes back. Other keys are ignored.
    /// </summary>
    private async Task<(bool Back, Microphone? Picked)> ReadMicrophoneAsync(IReadOnlyList<Microphone> listed)
    {
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return (true, null);
            if (pressed.Key == ConsoleKey.Enter) return (false, null);
            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < listed.Count) return (false, listed[pressed.KeyChar - '1']);
        }
    }

    /// <summary>
    /// The rule <paramref name="answer"/> to the follow question spells: nothing keeps <paramref name="inForce"/>; A or
    /// auto follows any app; N or none follows none; anything else is part of an app's name, with .exe dropped, unless it
    /// is part of ClassScribe's own, whose own recording holds the microphone for the whole class, which is refused with
    /// why, keeping the rule in force.
    /// </summary>
    private FollowRule? Follow(string answer, FollowRule? inForce)
    {
        if (answer.Length == 0) return inForce;
        if (answer.Equals("A", StringComparison.OrdinalIgnoreCase)) return new FollowRule.Any();
        if (answer.Equals("N", StringComparison.OrdinalIgnoreCase)) return new FollowRule.None();
        if (!FollowRule.TryParse(answer, out var rule))
        {
            ui.WriteLine($"\"{answer}\" is not part of an app's name. Keeping {DescribeInForce(inForce)}.");
            return inForce;
        }

        if (rule is FollowRule.Named named && named.Matches(OwnProcessName))
        {
            ui.WriteLine($"\"{answer}\" names ClassScribe itself, whose own recording holds the microphone for the whole class; following it would record everything. Keeping {DescribeInForce(inForce)}.");
            return inForce;
        }

        return rule;
    }

    /// <summary>The rule in force, in words for the follow question: the app named, with whether it is the one recorded, any app, or none.</summary>
    private string DescribeInForce(FollowRule? inForce) => inForce switch
    {
        FollowRule.Named named when choice.App is { } app && named.Matches(app) => $"\"{named.NamePart}\", the app being recorded",
        FollowRule.Named named => $"\"{named.NamePart}\"",
        FollowRule.Any => $"\"{FollowRule.AnyWord}\" (any app that holds the microphone)",
        FollowRule.None => $"\"{FollowRule.NoneWord}\" (V only)",
        _ => "what Recording:MicrophoneFollows says"
    };

    /// <summary>What was pinned for the session, in words, as menu choice 6 says it.</summary>
    private string Pinned() => $"Recording from {choice.Description} until ClassScribe is closed, whatever the settings say.";

    /// <summary>
    /// The settings that would keep the pick, as one instruction: Recording:Mode, Recording:Microphone when a microphone was
    /// picked, and Recording:MicrophoneFollows when an app was; for Enter, that Recording:Microphone stays as it is.
    /// </summary>
    private static string Keep(RecordingMode mode, Microphone? picked, FollowRule? follows, string? fromSettings)
    {
        List<string> sets = [$"Recording:Mode to \"{mode}\""];
        if (picked is not null) sets.Add($"Recording:Microphone to \"{picked.Name}\"");
        if (follows is not null) sets.Add($"Recording:MicrophoneFollows to \"{follows.Word}\"");

        var set = sets.Count == 1 ? $"set {sets[0]}" : $"set {string.Join(", ", sets[..^1])} and {sets[^1]}";
        return picked is not null ? set
            : fromSettings is null ? $"{set}, and leave Recording:Microphone empty"
            : $"{set}, and keep Recording:Microphone as \"{fromSettings}\"";
    }

    /// <summary>Trims a name; a blank one names nothing.</summary>
    private static string? Normalise(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>True for the keys that go back to the menu: Esc, Q and Ctrl+C.</summary>
    private static bool IsBack(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Escape or ConsoleKey.Q || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));
}
