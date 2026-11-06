using System.Runtime.InteropServices;
using ClassScribe.Audio;
using ClassScribe.Audio.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// Which exceptions the readers of Windows' audio lists take for a failed result of one of Windows' calls, so that one bad
/// session or device costs that look alone (see <see cref="AudioFailures.IsFailedResult"/>): ClassScribe's own
/// COMExceptions, the InvalidCastException of an interface an object does not offer, and the three exceptions .NET's own
/// mapping makes of NAudio's failed results, each by its exact type and the result it stands for, as the Zoom fix meant. A
/// programming error inside a visit, such as a null or out-of-range argument, an argument named by its parameter or a type
/// of ClassScribe's own, is not one, and fails loudly as before the fix, rather than being noted at Debug as one unreadable
/// session.
/// </summary>
[TestFixture]
public sealed class AudioFailuresTests
{
    /// <summary>E_INVALIDARG, which .NET maps to an ArgumentException.</summary>
    private const int InvalidArgument = unchecked((int)0x80070057);

    /// <summary>E_ACCESSDENIED, which .NET maps to an UnauthorizedAccessException.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>E_NOTIMPL, which .NET maps to a NotImplementedException.</summary>
    private const int NotImplemented = unchecked((int)0x80004001);

    /// <summary>
    /// A failed result .NET maps to an exception of its own, as NAudio's calls throw it, is taken: the mapping makes the
    /// exact type with the result as its HResult and names no parameter.
    /// </summary>
    [TestCase(InvalidArgument, typeof(ArgumentException))]
    [TestCase(AccessDenied, typeof(UnauthorizedAccessException))]
    [TestCase(NotImplemented, typeof(NotImplementedException))]
    public void A_result_NET_maps_to_an_exception_of_its_own_is_taken(int result, Type mapped)
    {
        var failure = Marshal.GetExceptionForHR(result)!;

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.TypeOf(mapped));
            Assert.That(failure.HResult, Is.EqualTo(result));
            Assert.That(AudioFailures.IsFailedResult(failure), Is.True);
        });
    }

    /// <summary>
    /// The ArgumentException of E_INVALIDARG as the readers' tests play it, with .NET's own words and nothing else set, is
    /// taken: it is what NAudio threw with the Zoom app in a meeting on 18 October.
    /// </summary>
    [Test]
    public void The_argument_exception_of_E_INVALIDARG_as_the_readers_tests_play_it_is_taken() =>
        Assert.That(AudioFailures.IsFailedResult(new ArgumentException("Value does not fall within the expected range.")), Is.True);

    /// <summary>ClassScribe's own failures, a COMException of any result, and an interface not offered are taken as before.</summary>
    [Test]
    public void ClassScribes_own_failures_and_an_interface_not_offered_are_taken()
    {
        var own = Assert.Throws<COMException>(() => CoreAudio.Check(unchecked((int)0x88890004)))!;

        Assert.Multiple(() =>
        {
            Assert.That(AudioFailures.IsFailedResult(own), Is.True);
            Assert.That(AudioFailures.IsFailedResult(new COMException("It failed.", unchecked((int)0x80004005))), Is.True);
            Assert.That(AudioFailures.IsFailedResult(new InvalidCastException("No such interface supported")), Is.True);
        });
    }

    /// <summary>The exceptions a programming error inside a reader's visit throws, which Windows' mapping never makes.</summary>
    private static IEnumerable<TestCaseData> ProgrammingErrors()
    {
        yield return new TestCaseData(new ArgumentNullException("session")).SetName("A_null_argument_is_not_taken");
        yield return new TestCaseData(new ArgumentOutOfRangeException("index")).SetName("An_argument_out_of_range_is_not_taken");
        yield return new TestCaseData(new ArgumentException("not a session", "session")).SetName("An_argument_named_by_its_parameter_is_not_taken");
        yield return new TestCaseData(new ArgumentException("another result") { HResult = unchecked((int)0x80131501) }).SetName("An_argument_exception_of_another_result_is_not_taken");
        yield return new TestCaseData(new OwnArgumentException()).SetName("An_argument_exception_of_ClassScribes_own_type_is_not_taken");
        yield return new TestCaseData(new OwnNotImplementedException()).SetName("A_not_implemented_exception_of_ClassScribes_own_type_is_not_taken");
        yield return new TestCaseData(new NotImplementedException("later") { HResult = unchecked((int)0x80131500) }).SetName("A_not_implemented_exception_of_another_result_is_not_taken");
        yield return new TestCaseData(new UnauthorizedAccessException("a file") { HResult = unchecked((int)0x80131500) }).SetName("An_unauthorized_access_of_another_result_is_not_taken");
        yield return new TestCaseData(new NullReferenceException()).SetName("A_null_reference_is_not_taken");
        yield return new TestCaseData(new IndexOutOfRangeException()).SetName("An_index_out_of_range_is_not_taken");
        yield return new TestCaseData(new InvalidOperationException("wrong state")).SetName("An_invalid_operation_is_not_taken");
        yield return new TestCaseData(new NotSupportedException()).SetName("A_not_supported_exception_is_not_taken");
    }

    /// <summary>
    /// A programming error is not taken for a failed result, so it fails loudly: a null or out-of-range argument, an argument
    /// named by its parameter (Windows' mapping names none), a mapped type with another result, a type derived from one of
    /// them, and the exceptions that were never taken.
    /// </summary>
    [TestCaseSource(nameof(ProgrammingErrors))]
    public void A_programming_error_is_not_taken(Exception error) =>
        Assert.That(AudioFailures.IsFailedResult(error), Is.False);

    /// <summary>An ArgumentException of ClassScribe's own, as a visit's own check might throw: its result is E_INVALIDARG, as every ArgumentException's is.</summary>
    private sealed class OwnArgumentException() : ArgumentException("a visit's own check");

    /// <summary>A NotImplementedException of ClassScribe's own, as a stub might throw: its result is E_NOTIMPL, as every NotImplementedException's is.</summary>
    private sealed class OwnNotImplementedException() : NotImplementedException("a stub");
}
