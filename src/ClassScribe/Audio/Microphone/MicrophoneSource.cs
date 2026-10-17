namespace ClassScribe.Audio.Microphones;

/// <summary>
/// Your microphone as a recording session's source: the microphones Windows lists, with the chooser picking which to
/// record from them (Windows' default, one named, or the one the class app uses) and the provider opening the capture
/// with the processing asked for. A session on it looks every ten seconds, and at once when Windows reports a change
/// among the microphones or the class app is found listening on another one (see <see cref="FollowApp"/>). Nothing
/// here opens a microphone until the session asks for a capture.
/// </summary>
/// <param name="provider">Lists the microphones, tells when they change, and opens captures of them.</param>
/// <param name="chooser">Decides which microphone to record at each look, and keeps the choice for the whole recording.</param>
/// <param name="processing">Whether to ask Windows for the microphone's audio without its sound processing (RAW), falling back to Default.</param>
public sealed class MicrophoneSource(IMicrophoneProvider provider, MicrophoneChooser chooser, MicrophoneProcessing processing) : ICaptureSource
{
    /// <summary>How often a session looks at the microphones by itself (see <see cref="CheckInterval"/>).</summary>
    public static readonly TimeSpan EveryTenSeconds = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();

    /// <summary>The microphones the last look listed, by id: what <see cref="Open"/> can open.</summary>
    private readonly Dictionary<string, Microphone> _listed = [];

    private EventHandler? _changed;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>The microphone the class app listens on, when it is known, which the chooser then records instead; null when not known.</summary>
    private string? _appMicrophoneId;

    /// <summary>What the last look picked, with the reason, for the words of a move.</summary>
    private MicrophonePick? _lastPick;

    /// <summary>What the last look picked, with the chooser's reason, for the run's microphone line; null before the first look.</summary>
    public MicrophonePick? LastPick
    {
        get { lock (_gate) return _lastPick; }
    }

    /// <summary>
    /// Raised after each look with what it picked, on the thread that looked: the session's at its start, or a thread
    /// pool's at a later look. Whoever follows the pick rather than the capture, such as the watch on the class app's
    /// microphone sessions, learns of it here, whether or not a capture is then opened: a track switched off still looks.
    /// </summary>
    public event EventHandler<MicrophonePick>? Picked;

    /// <summary>
    /// Raised when Windows reports a change among the microphones, and when the class app has been found listening on
    /// another microphone: the session then looks at once. The provider's notifications are listened to while anyone
    /// listens here, and let go of when the last listener leaves.
    /// </summary>
    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                if (!_subscribed && !_disposed)
                {
                    provider.MicrophonesChanged += OnMicrophonesChanged;
                    _subscribed = true;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null) UnsubscribeLocked();
            }
        }
    }

    /// <summary>The microphone, for both: "Cannot capture the microphone right now", "Could not start capturing the microphone yet".</summary>
    public SourceWording Wording => new("the microphone", "the microphone");

    /// <summary>Every ten seconds: Windows announces a microphone added, removed or made the default at once, through <see cref="Changed"/>, and the look catches the rest.</summary>
    public TimeSpan CheckInterval => EveryTenSeconds;

    /// <inheritdoc />
    public string WaitingFor => chooser.WaitingFor;

    /// <summary>Null: no meter is read for a microphone.</summary>
    public float? RecordedLevel => null;

    /// <summary>
    /// One look: lists the microphones, which opens none of them, and has the chooser pick from them, the class app's
    /// microphone first when it is known; the pick is then raised through <see cref="Picked"/>. What is recorded now and
    /// how long ago it carried sound play no part: the chooser keeps its choice by endpoint id itself.
    /// </summary>
    public CaptureTarget? Choose(string? recordedId, TimeSpan? sinceRecordedSound)
    {
        var all = provider.GetMicrophones();
        string? appMicrophoneId;
        lock (_gate)
        {
            _listed.Clear();
            foreach (var microphone in all) _listed[microphone.Id] = microphone;
            appMicrophoneId = _appMicrophoneId;
        }

        var pick = chooser.Choose(all, appMicrophoneId);
        lock (_gate) _lastPick = pick;
        Picked?.Invoke(this, pick);
        return pick.Chosen is { } chosen ? new CaptureTarget(chosen.Id, chosen.Name) : null;
    }

    /// <summary>Opens a capture of the microphone the last look picked, asking for the processing wanted. Only a microphone that look listed can be opened.</summary>
    public IAudioCapture Open(CaptureTarget target)
    {
        Microphone? microphone;
        lock (_gate) _listed.TryGetValue(target.Id, out microphone);
        if (microphone is null)
            throw new InvalidOperationException($"The microphone \"{target.Name}\" is not among the microphones the last look listed.");

        return provider.CreateCapture(microphone, processing);
    }

    /// <summary>
    /// Why the recording moves to <paramref name="chosen"/>, in the chooser's words: the microphone the class app uses,
    /// the one chosen at the start back again, or another standing in while the chosen one is gone.
    /// </summary>
    public string DescribeMove(CaptureTarget chosen, string from)
    {
        MicrophonePick? pick;
        lock (_gate) pick = _lastPick;

        if (pick is { FellBack: true }) return $"Recording the microphone \"{chosen.Name}\" instead of \"{from}\" until it returns";
        var why = pick?.Chosen?.Id == chosen.Id ? pick.Why : "the microphone the last look picked";
        return $"Recording the microphone \"{chosen.Name}\", {why}, instead of \"{from}\"";
    }

    /// <summary>
    /// Tells the source which microphone the class app listens on, by endpoint id, or null when that is not known: the
    /// next look records it instead of the one chosen so far, so that your voice is recorded as the class hears it, and
    /// a change here raises <see cref="Changed"/> so that the look comes at once.
    /// </summary>
    public void FollowApp(string? endpointId)
    {
        lock (_gate)
        {
            if (_appMicrophoneId == endpointId) return;
            _appMicrophoneId = endpointId;
        }

        RaiseChanged();
    }

    /// <summary>Lets go of the provider's notifications. The provider belongs to the app and is not disposed here.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            UnsubscribeLocked();
            _changed = null;
        }
    }

    /// <summary>Stops listening to the provider's notifications, if listening. Call with the lock held.</summary>
    private void UnsubscribeLocked()
    {
        if (!_subscribed) return;
        provider.MicrophonesChanged -= OnMicrophonesChanged;
        _subscribed = false;
    }

    /// <summary>Passes on Windows' notification about the microphones. Runs on a thread that belongs to Windows.</summary>
    private void OnMicrophonesChanged(object? sender, EventArgs e) => RaiseChanged();

    /// <summary>Raises <see cref="Changed"/> for whoever listens now, outside the lock.</summary>
    private void RaiseChanged()
    {
        EventHandler? handlers;
        lock (_gate) handlers = _changed;
        handlers?.Invoke(this, EventArgs.Empty);
    }
}
