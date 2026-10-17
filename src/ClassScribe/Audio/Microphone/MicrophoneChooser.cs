using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Microphones;

/// <summary>The outcome of one look at the microphones.</summary>
/// <param name="Chosen">The microphone to record, or null while there is none to record.</param>
/// <param name="Why">Why that one, or what is waited for, in words for the log.</param>
/// <param name="FellBack">True while another microphone stands in for the chosen one, which has gone.</param>
public sealed record MicrophonePick(Microphone? Chosen, string Why, bool FellBack);

/// <summary>
/// Decides which microphone to record, at each look at what Windows lists. With no name, Windows' default microphone,
/// unless it is Bluetooth (using earbuds switches them to call quality, for the class too) or an input that records what
/// the computer plays (Stereo Mix, which would record the class twice); then the laptop's own microphone array; then the
/// first microphone that is neither; otherwise nothing, and the wait says that a Bluetooth microphone has to be named. A
/// name picks the first microphone whose name contains it, in any case, Bluetooth allowed. The microphone the class app
/// uses comes before all of that, so that his voice is recorded as the class hears it. The choice is kept by endpoint id
/// for the whole recording, so earbuds that connect mid-class and become Windows' default are not followed; when the
/// chosen microphone has been gone for <see cref="FallBackAfter"/>, another stands in by the no-name rule, and the
/// recording moves back as soon as the chosen one returns. Nothing here opens a microphone, and one look runs at a time.
/// </summary>
/// <param name="namePart">Part of the name of the microphone to record, or null or blank for Windows' default.</param>
/// <param name="time">The clock the absence of the chosen microphone is timed on.</param>
/// <param name="logger">Where moves between microphones are noted.</param>
public sealed class MicrophoneChooser(string? namePart, TimeProvider time, ILogger<MicrophoneChooser> logger)
{
    /// <summary>How long the chosen microphone may be gone before another stands in for it.</summary>
    public static readonly TimeSpan FallBackAfter = TimeSpan.FromSeconds(10);

    /// <summary>Why the chosen microphone is recorded at a later look.</summary>
    private const string Kept = "the microphone chosen at the start";

    /// <summary>What a microphone that records what the computer plays is, for the messages.</summary>
    private const string RecordsThePlayback = "records what the computer plays";

    private readonly string? _namePart = string.IsNullOrWhiteSpace(namePart) ? null : namePart.Trim();

    /// <summary>The microphone the recording is kept on, as last listed; null until one has been chosen.</summary>
    private Microphone? _pinned;

    /// <summary>The microphone the last look picked, chosen or standing in; null when it picked none.</summary>
    private Microphone? _current;

    /// <summary>When the chosen microphone was first found missing, on the clock; null while it is listed.</summary>
    private long? _goneSince;

    /// <summary>True while another microphone stands in for the chosen one.</summary>
    private bool _fellBack;

    /// <summary>The class app's microphone that was refused because it records what the computer plays, said once.</summary>
    private string? _refusedAppMicrophoneId;

    /// <summary>The part of a name the microphone is chosen by, or null when Windows' default is taken.</summary>
    public string? NamePart => _namePart;

    /// <summary>Windows' identifier of the microphone the recording is kept on; null until one has been chosen.</summary>
    public string? PinnedId => _pinned?.Id;

    /// <summary>
    /// What the recording waits for while there is no microphone to record, in words for the log, such as the microphone
    /// "OBSBOT", or a microphone. Set by each look that finds nothing.
    /// </summary>
    public string WaitingFor { get; private set; } = string.IsNullOrWhiteSpace(namePart) ? "a microphone" : $"the microphone \"{namePart.Trim()}\"";

    /// <summary>
    /// One look: picks the microphone to record from <paramref name="all"/>, the microphones Windows lists now, or returns
    /// a pick with none while there is nothing to record. <paramref name="appMicrophoneId"/> is the microphone the class
    /// app listens on, when it is known, which is then recorded instead of the one chosen so far.
    /// </summary>
    public MicrophonePick Choose(IReadOnlyList<Microphone> all, string? appMicrophoneId = null)
    {
        if (appMicrophoneId is not null && all.FirstOrDefault(microphone => microphone.Id == appMicrophoneId) is { } appMicrophone)
        {
            if (appMicrophone.IsLoopbackInput)
            {
                if (_refusedAppMicrophoneId != appMicrophone.Id)
                {
                    _refusedAppMicrophoneId = appMicrophone.Id;
                    logger.LogWarning("The class app uses \"{App}\", which {Records}; recording \"{Current}\" still",
                        appMicrophone.Name, RecordsThePlayback, _current?.Name ?? "nothing");
                }
            }
            else if (_pinned?.Id != appMicrophone.Id)
            {
                // The app's microphone is the one standing in only while fallen back: it becomes the choice, and the chosen
                // one's return then no longer moves the recording.
                if (_current?.Id == appMicrophone.Id)
                    logger.LogInformation("The class app uses the microphone \"{App}\", the one standing in; keeping it", appMicrophone.Name);
                else if (_current is { } current)
                    logger.LogInformation("The class app uses the microphone \"{App}\"; recording it instead of \"{Current}\"", appMicrophone.Name, current.Name);
                else
                    logger.LogInformation("The class app uses the microphone \"{App}\"; recording it", appMicrophone.Name);
                return Picked(Pin(appMicrophone), "the microphone the class app uses");
            }
        }

        if (_pinned is { } pinned) return ChooseWithPin(all, pinned);

        var (chosen, why) = _namePart is null ? ByDefault(all) : ByName(all);
        return chosen is null ? Nothing($"waiting for {WaitingFor}") : Picked(Pin(chosen), why);
    }

    /// <summary>The look once a microphone has been chosen: it when listed, a wait while it has just gone, then a stand-in.</summary>
    private MicrophonePick ChooseWithPin(IReadOnlyList<Microphone> all, Microphone pinned)
    {
        if (all.FirstOrDefault(microphone => microphone.Id == pinned.Id) is { } present)
        {
            var back = _goneSince is not null;
            _goneSince = null;
            _fellBack = false;
            _pinned = present;
            if (back) logger.LogInformation("The microphone \"{Chosen}\" is back; recording it again", present.Name);
            return Picked(present, back ? $"{Kept}, back again" : Kept);
        }

        // The chosen microphone is gone: what is waited for is the one asked for by name, or else the chosen one itself.
        var waitingFor = _namePart is null ? $"the microphone \"{pinned.Name}\"" : $"the microphone \"{_namePart}\"";
        var now = time.GetTimestamp();
        _goneSince ??= now;
        if (time.GetElapsedTime(_goneSince.Value, now) < FallBackAfter)
        {
            WaitingFor = waitingFor;
            _current = null;
            return new MicrophonePick(null, $"waiting for \"{pinned.Name}\" to come back", FellBack: false);
        }

        var (standIn, why) = ByDefault(all, forFallback: true);
        if (standIn is null)
        {
            WaitingFor = waitingFor;
            return Nothing($"waiting for {WaitingFor}");
        }

        var seconds = FallBackAfter.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        if (!_fellBack || _current?.Id != standIn.Id)
            logger.LogWarning("The microphone \"{Chosen}\" has been gone for {Seconds} s; recording \"{StandIn}\" instead until it returns", pinned.Name, seconds, standIn.Name);
        _fellBack = true;
        _current = standIn;
        return new MicrophonePick(standIn, $"{why}, instead of \"{pinned.Name}\", which has been gone for {seconds} s", FellBack: true);
    }

    /// <summary>
    /// The no-name rule: Windows' default microphone unless it is Bluetooth or records what the computer plays, then the
    /// laptop's array, then the first microphone that is neither. The reason says why the default was passed over, except
    /// for a stand-in, where the reason is what stands in. Sets <see cref="WaitingFor"/> when nothing is allowed.
    /// </summary>
    private (Microphone? Chosen, string Why) ByDefault(IReadOnlyList<Microphone> all, bool forFallback = false)
    {
        var fallbackDefault = all.FirstOrDefault(microphone => microphone.IsDefault);
        if (fallbackDefault is not null && IsAllowedByItself(fallbackDefault)) return (fallbackDefault, "Windows' default microphone");

        var because = fallbackDefault is null ? "Windows names no default microphone"
            : fallbackDefault.IsBluetooth ? $"Windows' default \"{fallbackDefault.Name}\" is Bluetooth"
            : $"Windows' default \"{fallbackDefault.Name}\" {RecordsThePlayback}";
        if (all.FirstOrDefault(microphone => microphone.IsBuiltInArray && IsAllowedByItself(microphone)) is { } array)
            return (array, forFallback ? "the laptop's microphone array" : $"the laptop's microphone array, because {because}");
        if (all.FirstOrDefault(IsAllowedByItself) is { } other)
            return (other, forFallback ? "another microphone" : $"another microphone, because {because} and the laptop has no microphone array");

        WaitingFor = all.FirstOrDefault(microphone => microphone.IsBluetooth) is { } bluetooth
            ? $"a microphone that is not Bluetooth (\"{bluetooth.Name}\" is used only when named in Recording:Microphone)"
            : "a microphone";
        return (null, WaitingFor);
    }

    /// <summary>The name rule: the first microphone whose name contains the name part, in any case, Bluetooth allowed, never one that records what the computer plays.</summary>
    private (Microphone? Chosen, string Why) ByName(IReadOnlyList<Microphone> all)
    {
        var matching = all.Where(microphone => microphone.Name.Contains(_namePart!, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.FirstOrDefault(microphone => !microphone.IsLoopbackInput) is { } chosen) return (chosen, $"the microphone named \"{_namePart}\"");

        WaitingFor = matching.Count > 0
            ? $"a microphone other than \"{matching[0].Name}\", which {RecordsThePlayback}"
            : $"the microphone \"{_namePart}\"";
        return (null, WaitingFor);
    }

    /// <summary>True for a microphone the no-name rule may take: neither Bluetooth nor an input that records what the computer plays.</summary>
    private static bool IsAllowedByItself(Microphone microphone) => !microphone.IsBluetooth && !microphone.IsLoopbackInput;

    /// <summary>Keeps the recording on <paramref name="microphone"/> from now on.</summary>
    private Microphone Pin(Microphone microphone)
    {
        _pinned = microphone;
        _goneSince = null;
        _fellBack = false;
        return microphone;
    }

    /// <summary>The pick of <paramref name="microphone"/>, noted as the current one.</summary>
    private MicrophonePick Picked(Microphone microphone, string why)
    {
        _current = microphone;
        return new MicrophonePick(microphone, why, FellBack: false);
    }

    /// <summary>The pick of nothing, with <see cref="WaitingFor"/> already set by the rule that found nothing.</summary>
    private MicrophonePick Nothing(string why)
    {
        _current = null;
        _fellBack = false;
        return new MicrophonePick(null, why, FellBack: false);
    }
}
