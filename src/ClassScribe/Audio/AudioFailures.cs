using System.Runtime.InteropServices;

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
public static class AudioFailures
{
    /// <summary>True for an exception a failed result of one of Windows' audio calls can reach a caller as (see <see cref="AudioFailures"/>).</summary>
    public static bool IsFailedResult(Exception exception) =>
        exception is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or NotImplementedException;

    /// <summary>A failure in words for a listing or a line: its message and its result, such as "The parameter is incorrect. (0x80070057)".</summary>
    public static string Describe(Exception exception) => $"{exception.Message.Trim()} (0x{exception.HResult:X8})";
}
