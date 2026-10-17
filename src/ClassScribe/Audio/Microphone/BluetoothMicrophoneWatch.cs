using System.Runtime.InteropServices;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Microphones;

/// <summary>An app taking sound from a Bluetooth microphone, as one look found it.</summary>
/// <param name="App">The app's program without .exe, such as chrome, or "process N" when it could not be named.</param>
/// <param name="Microphone">The microphone's name, as Windows shows it, such as Headset (2- Pixel Buds Pro 2).</param>
public sealed record BluetoothMicrophoneUse(string App, string Microphone);

/// <summary>What one look of <see cref="BluetoothMicrophoneWatch"/> found.</summary>
/// <param name="Began">The uses that were not there at the look before: each to be warned about, once.</param>
/// <param name="Ended">The uses of the look before that are gone: each to be said once.</param>
/// <param name="Current">Every use found now.</param>
/// <param name="BuiltInMicrophone">The name of the laptop's own microphone array, when one is connected, for the advice.</param>
/// <param name="Problem">Why nothing could be read now, in words, when nothing could; the uses are then those of the look before.</param>
public sealed record BluetoothMicrophoneLook(
    IReadOnlyList<BluetoothMicrophoneUse> Began,
    IReadOnlyList<BluetoothMicrophoneUse> Ended,
    IReadOnlyList<BluetoothMicrophoneUse> Current,
    string? BuiltInMicrophone,
    string? Problem);

/// <summary>
/// Watches, while the class is recorded, whether the class app is taking sound from a Bluetooth microphone: the headset
/// or earbuds are then in call mode, and the class's sound may reach them in a way Windows' loopback cannot record, so
/// the recording is silent. It joins two of Windows' own lists, both read without opening anything: the microphones with their
/// buses (see <see cref="IMicrophoneProvider.GetMicrophones"/>) and the apps' sessions on each (see
/// <see cref="IMicrophoneSessions.ReadAll"/>), every <see cref="Interval"/>, as the class microphone watch reads every
/// microphone, and the devices command joins them. It remembers the uses of the last look, so that each new one is warned
/// about once and each end said once. ClassScribe's own processes and Windows' sounds never count. One per run.
/// </summary>
/// <param name="sessions">Reads the apps' sessions on every microphone.</param>
/// <param name="microphones">Lists the microphones, with which are Bluetooth.</param>
/// <param name="own">Tells ClassScribe's own processes, whose sessions never count.</param>
/// <param name="time">The steady clock the interval is kept on.</param>
/// <param name="logger">The log.</param>
public sealed class BluetoothMicrophoneWatch(IMicrophoneSessions sessions, IMicrophoneProvider microphones, IOwnProcesses own, TimeProvider time,
    ILogger<BluetoothMicrophoneWatch> logger)
{
    /// <summary>How often the lists are read: as often as the class microphone watch reads every microphone.</summary>
    public static readonly TimeSpan Interval = ClassMicrophoneWatch.AllMicrophonesInterval;

    /// <summary>When the last look was made, on the steady clock; null before the first.</summary>
    private long? _lastLook;

    /// <summary>The uses found at the last look that read.</summary>
    private HashSet<BluetoothMicrophoneUse> _current = [];

    /// <summary>True while the lists cannot be read, from the look that noted it, so that a stretch of failures is noted once.</summary>
    private bool _failing;

    /// <summary>
    /// Looks when a look is due, at the first call and every <see cref="Interval"/> after, for sessions of <paramref name="apps"/>
    /// (any app when the list is empty); null between looks.
    /// </summary>
    public BluetoothMicrophoneLook? Look(IReadOnlyList<FollowRule.Named> apps, long now)
    {
        if (_lastLook is { } last && time.GetElapsedTime(last, now) < Interval) return null;

        _lastLook = now;
        return LookNow(apps);
    }

    /// <summary>
    /// Looks now, whatever the time: the apps of <paramref name="apps"/> (any app when the list is empty) taking sound from
    /// a Bluetooth microphone, against the look before. Lists that cannot be read now give a look that says so and keeps the
    /// uses of the look before, so that nothing is said to have ended; the next look reads again.
    /// </summary>
    public BluetoothMicrophoneLook LookNow(IReadOnlyList<FollowRule.Named> apps)
    {
        IReadOnlyList<Microphone> list;
        MicrophoneListings listings;
        try
        {
            list = microphones.GetMicrophones();
            listings = sessions.ReadAll();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            // Deliberately not rethrown: this watch only words a warning, and a look at Windows' lists that fails, as while
            // its audio service restarts, must not keep the run's other checks from running; the next look reads again.
            return Unread(ex.Message);
        }

        if (listings.Problem is { } problem) return Unread(problem);

        if (_failing)
        {
            _failing = false;
            logger.LogDebug("Windows' microphone lists can be read again");
        }

        var bluetooth = list.Where(microphone => microphone.IsBluetooth).ToDictionary(microphone => microphone.Id, microphone => microphone.Name);
        var builtIn = list.FirstOrDefault(microphone => microphone.IsBuiltInArray)?.Name;
        var found = new HashSet<BluetoothMicrophoneUse>();
        foreach (var listing in listings.Listings)
        {
            if (!bluetooth.TryGetValue(listing.EndpointId, out var name)) continue;

            foreach (var session in listing.Sessions)
            {
                if (session.State != CaptureSessionState.Active || session.SystemSounds || session.ProcessId <= 0) continue;
                if (own.IsOwn(session.ProcessId, session.ImageName)) continue;
                if (apps.Count > 0 && !(session.ImageName is { } image && apps.Any(app => app.Matches(image)))) continue;

                found.Add(new BluetoothMicrophoneUse(session.ImageName ?? $"process {session.ProcessId}", name));
            }
        }

        var began = found.Except(_current).OrderBy(use => use.App, StringComparer.OrdinalIgnoreCase).ThenBy(use => use.Microphone, StringComparer.Ordinal).ToList();
        var ended = _current.Except(found).OrderBy(use => use.App, StringComparer.OrdinalIgnoreCase).ThenBy(use => use.Microphone, StringComparer.Ordinal).ToList();
        _current = found;
        return new BluetoothMicrophoneLook(began, ended, [.. found.OrderBy(use => use.App, StringComparer.OrdinalIgnoreCase)], builtIn, Problem: null);
    }

    /// <summary>A look that read nothing, for <paramref name="problem"/>, noted at Debug once per stretch; the uses stay as they were.</summary>
    private BluetoothMicrophoneLook Unread(string problem)
    {
        if (!_failing)
        {
            _failing = true;
            logger.LogDebug("Could not read Windows' microphone lists for the Bluetooth check ({Problem}); it looks again at the next look", problem);
        }

        return new BluetoothMicrophoneLook([], [], [.. _current], BuiltInMicrophone: null, problem);
    }
}
