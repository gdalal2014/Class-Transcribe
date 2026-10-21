namespace ClassScribe.Desktop;

/// <summary>
/// What keeps every test from a real camera or microphone (teach-video section 11): a test run turns on
/// <see cref="RefuseTheRealDevicesSwitch"/> (TestGuards, the assembly's set-up), and then the real start of a camera process
/// and Windows' real activation of a capture endpoint (a microphone) behind the microphone's capture factory refuse, naming the
/// fake to use instead, so that a test that forgets a fake fails at once rather than opening a device, and its light or its
/// microphone, on the laptop. Registering a fake is then no longer the only protection. ClassScribe itself never turns it on;
/// an [Explicit] test that must open a real device turns it off for itself and on again in a finally block.
/// </summary>
public static class DeviceGuards
{
    /// <summary>The AppContext switch a test run turns on: with it on, a real camera or microphone is never opened.</summary>
    public const string RefuseTheRealDevicesSwitch = "ClassScribe.Devices.RefuseTheRealDevices";

    /// <summary>True while <see cref="RefuseTheRealDevicesSwitch"/> is on, as in a test run.</summary>
    public static bool RefusingTheRealDevices => AppContext.TryGetSwitch(RefuseTheRealDevicesSwitch, out var refused) && refused;

    /// <summary>
    /// Throws, while <see cref="RefuseTheRealDevicesSwitch"/> is on, an <see cref="InvalidOperationException"/> saying that a
    /// test asked for <paramref name="what"/> and naming <paramref name="fake"/>, what to use instead; does nothing otherwise.
    /// </summary>
    public static void RefuseInTests(string what, string fake)
    {
        if (RefusingTheRealDevices)
            throw new InvalidOperationException($"A test asked for {what}, which would open a real camera or microphone on this computer. Use {fake} instead.");
    }
}
