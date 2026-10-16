namespace ClassScribe.Tests;

/// <summary>
/// The computer's clock, with a local time zone the test chooses, so that a time shown in local time reads the same on
/// every machine that runs the tests.
/// </summary>
public sealed class ZonedTimeProvider(TimeZoneInfo zone) : TimeProvider
{
    /// <summary>US Eastern time in October (four hours behind UTC), where an evening class stamped 23:00 UTC began at 19:00.</summary>
    public static ZonedTimeProvider EasternSummer { get; } =
        new(TimeZoneInfo.CreateCustomTimeZone("ClassScribe test Eastern", TimeSpan.FromHours(-4), "Eastern (test)", "Eastern (test)"));

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => zone;
}
