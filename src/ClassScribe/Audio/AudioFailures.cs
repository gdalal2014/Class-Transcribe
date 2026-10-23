using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClassScribe.Audio;

/// <summary>
/// What a failed answer from one of Windows' audio calls can reach a caller as. ClassScribe's own declarations throw a
/// <see cref="COMException"/> for every failed result (see <c>CoreAudio.Check</c>); NAudio, which reads the output
/// devices' names, registers for the device notifications and opens the captures, lets .NET's own mapping make exceptions
/// of other types of some results, such as ArgumentException of E_INVALIDARG, UnauthorizedAccessException of E_ACCESSDENIED
/// and NotImplementedException of E_NOTIMPL; and asking an object Windows handed out for an interface it does not offer is
/// an InvalidCastException. The places that turn a failed look at Windows' lists into a listing that says it failed catch
/// these, so that one failed call costs that look alone.
/// </summary>
public static partial class AudioFailures
{
    /// <summary>
    /// The key under which ClassScribe's own declarations note, in a failure's <see cref="Exception.Data"/>, which of
    /// Windows' calls answered with it, such as GetState: which call failed with the Zoom app in a meeting on 18 October
    /// was not known, and a listing that names the call says so the next time.
    /// </summary>
    public const string CallKey = "ClassScribe.AudioCall";

    /// <summary>True for an exception a failed result of one of Windows' audio calls can reach a caller as (see <see cref="AudioFailures"/>).</summary>
    public static bool IsFailedResult(Exception exception) =>
        exception is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or NotImplementedException;

    /// <summary>The call a failure came from, such as GetState, when ClassScribe's own declarations noted it (see <see cref="CallKey"/>); null otherwise.</summary>
    public static string? CallOf(Exception exception) => exception.Data[CallKey] as string;

    /// <summary>
    /// A failure in words for a listing or a line: the call it came from when that was noted, its message and its result,
    /// such as "GetState: The parameter is incorrect. (0x80070057)", or "The parameter is incorrect. (0x80070057)" for a
    /// failure that does not say which call answered with it.
    /// </summary>
    public static string Describe(Exception exception)
    {
        var words = $"{exception.Message.Trim()} (0x{exception.HResult:X8})";
        return CallOf(exception) is { } call ? $"{call}: {words}" : words;
    }

    /// <summary>
    /// The name of the method a checked expression calls: GetState for <c>control.GetState(out var state)</c>,
    /// GetSessionEnumerator for <c>((IAudioSessionManager2Raw)activated).GetSessionEnumerator(out list)</c>; the outermost
    /// call when calls are nested. Null for an expression that calls nothing, such as a variable that holds a result, and
    /// for none.
    /// </summary>
    public static string? CallName(string? expression) =>
        expression is not null && CallPattern().Match(expression) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>A name followed by an opening parenthesis: a call. A cast's parenthesis follows no name, so it is passed over.</summary>
    [GeneratedRegex(@"(\w+)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallPattern();
}
