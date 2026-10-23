using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Menu choice 7, run through <see cref="ICommandRunner.ChooseMicrophoneAsync"/>: asks whether to record your microphone
/// with the class (W), alone in person (I) or not at all (N); then lists the microphones Windows can record from, with
/// notes, for a number to pick one, or Enter to leave it to Recording:Microphone and Windows' default microphone, never a
/// Bluetooth one; and with the class, which app the microphone follows: Enter keeps the app in force, a name picks
/// another, A any app that holds the microphone, N none (V alone); in person, which camera records the lesson's picture
/// too, a number or Enter for none, and then the picture's size (amendment 39). The pick holds until ClassScribe is closed,
/// whatever the settings say (see <see cref="SourceChoice"/>), and the menu says which settings would keep it; the camera is
/// no setting, and every start from the menu asks first whether to use it. Listing the microphones and the cameras reads
/// what Windows says about them; nothing is opened. Esc at any question goes back without a change.
/// </summary>
/// <param name="microphones">Lists the microphones.</param>
/// <param name="choice">What is chosen for the session.</param>
/// <param name="settings">The settings file, named in the advice.</param>
/// <param name="recording">The run's recording settings.</param>
/// <param name="ui">The window the questions are asked in.</param>
/// <param name="cameras">Lists the cameras, for the camera question in person; null asks no camera question.</param>
/// <param name="writer">
/// Saves the pick as the default when S is pressed after it (layout-config 1.8): Recording:Mode, the microphone, the app
/// it follows and the picture's size, as picked; null names the settings that would keep it instead, as before.
/// </param>
public sealed class MicrophoneMenu(IMicrophoneProvider microphones, SourceChoice choice, SettingsFile settings, IOptionsSnapshot<RecordingOptions> recording, IConsoleUi ui,
    ICameraProvider? cameras = null, SettingsWriter? writer = null)
{
    /// <summary>What the menu says once a camera is picked: it is no setting, and each start asks first.</summary>
    public const string CameraIsAsked = "The camera is no setting: it holds until ClassScribe is closed, and each start from the menu asks first (Enter records with the camera; S records the sound only).";
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
            if (writer is null) ui.WriteLine($"{Pinned()} To keep that, set Recording:Mode to \"{RecordingMode.Class}\" in {settings.Path}.");
            else await OfferToSaveAsync(RecordingMode.Class, picked: null, follows: null, size: null);
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

        string? camera = null;
        CameraSize? size = null;
        if (mode == RecordingMode.Microphone && cameras is not null)
        {
            var asked = await AskCameraAsync(cameras);
            if (asked.Back) return 0;

            (camera, size) = (asked.Camera?.Name, asked.Size);
        }

        choice.PickMicrophone(mode, picked?.Name, follows, camera, size);
        if (writer is null) ui.WriteLine($"{Pinned()} To keep that, {Keep(mode, picked, follows, fromSettings, size)} in {settings.Path}.");
        if (camera is not null) ui.WriteLine(CameraIsAsked);
        if (writer is not null) await OfferToSaveAsync(mode, picked, follows, size);
        return 0;
    }

    /// <summary>
    /// After the pick (layout-config 1.8): says what is recorded now, and S keeps it as the default, through the writer:
    /// Recording:Mode, Recording:Microphone when one was picked, Recording:MicrophoneFollows when an app was, and
    /// Recording:CameraResolution when a size was; the camera itself is no setting. Any other key uses it until ClassScribe
    /// is closed.
    /// </summary>
    private async Task OfferToSaveAsync(RecordingMode mode, Microphone? picked, FollowRule? follows, CameraSize? size)
    {
        ui.WriteLine(Pinned());
        ui.WriteLine($"Press S to keep this as the default (saved in {writer!.FilePath}), or any other key to use it until ClassScribe is closed.");
        if ((await ui.ReadKeyAsync(CancellationToken.None))?.Key != ConsoleKey.S) return;

        List<(string Name, string[] Values)> saved = [("Recording:Mode", [mode.ToString()])];
        if (picked is not null) saved.Add(("Recording:Microphone", [picked.Name]));
        if (follows is not null) saved.Add(("Recording:MicrophoneFollows", [follows.Word]));
        if (size is not null) saved.Add(("Recording:CameraResolution", [size.Text]));
        writer.SetAll(saved);
    }

    /// <summary>
    /// In person: lists the colour cameras, numbered, for one to record the lesson's picture too, or Enter for none; then,
    /// for a camera picked, asks the picture's size (see <see cref="AskSizeAsync"/>). Esc, Q or Ctrl+C go back at either.
    /// </summary>
    private async Task<(bool Back, CameraDevice? Camera, CameraSize? Size)> AskCameraAsync(ICameraProvider provider)
    {
        var list = await provider.GetCamerasAsync(CancellationToken.None);
        var listed = list.Cameras.Take(MostListed).ToList();
        ui.WriteLine("Camera, for a lesson video:");
        if (list.Problem is { } problem) ui.WriteLine($"  The cameras could not be listed: {problem}.");
        else if (listed.Count == 0) ui.WriteLine("  None is connected now.");
        for (var i = 0; i < listed.Count; i++)
            ui.WriteLine($"  {i + 1}  {listed[i].Described}");

        ui.WriteLine("Press a camera's number to record its picture too, Enter for none, or Esc to go back.");
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return (true, null, null);
            if (pressed.Key == ConsoleKey.Enter) return (false, null, null);
            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < listed.Count)
            {
                var camera = listed[pressed.KeyChar - '1'];
                var (back, size) = await AskSizeAsync();
                return back ? (true, null, null) : (false, camera, size);
            }
        }
    }

    /// <summary>
    /// The picture's size (amendment 39): Enter for the size Recording:CameraResolution gives (null, the setting), 1, 2 or 3
    /// for 480p, 720p or 1080p, T to type a size, checked by <see cref="CameraSize.TryParse"/> and asked again with the rule
    /// when it is not one. Esc, Q or Ctrl+C go back.
    /// </summary>
    private async Task<(bool Back, CameraSize? Size)> AskSizeAsync()
    {
        var setting = CameraSize.TryParse(recording.Value.CameraResolution, out var set) ? set : CameraSize.Default;
        while (true)
        {
            ui.WriteLine($"Picture size: Enter for {setting.Text} (your setting), 1 480p, 2 720p, 3 1080p, T to type a size");
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return (true, null);
            if (pressed.Key == ConsoleKey.Enter) return (false, null);
            if (pressed.KeyChar is >= '1' and <= '3') return (false, CameraSize.Presets[pressed.KeyChar - '1'].Size);
            if (pressed.Key != ConsoleKey.T) continue;

            ui.Write("Width and height, such as 1024x576: ");
            if (await ui.ReadLineAsync(CancellationToken.None) is not { } typed) return (true, null);
            if (CameraSize.TryParse(typed, out var size)) return (false, size);

            ui.WriteLine($"\"{typed.Trim()}\" is not a size ClassScribe takes: {CameraSize.Rule}.");
        }
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
    /// picked, Recording:MicrophoneFollows when an app was, and Recording:CameraResolution when a picture's size was; for
    /// Enter at the microphone, that Recording:Microphone stays as it is.
    /// </summary>
    private static string Keep(RecordingMode mode, Microphone? picked, FollowRule? follows, string? fromSettings, CameraSize? size = null)
    {
        List<string> sets = [$"Recording:Mode to \"{mode}\""];
        if (picked is not null) sets.Add($"Recording:Microphone to \"{picked.Name}\"");
        if (follows is not null) sets.Add($"Recording:MicrophoneFollows to \"{follows.Word}\"");
        if (size is not null) sets.Add($"Recording:CameraResolution to \"{size.Text}\"");

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
