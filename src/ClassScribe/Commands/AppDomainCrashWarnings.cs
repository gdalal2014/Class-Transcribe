namespace ClassScribe.Commands;

/// <summary>
/// The warning .NET gives as an exception nobody caught takes the process down (AppDomain.UnhandledException), to each
/// registered warning in the order they were registered, before the process ends.
/// </summary>
public sealed class AppDomainCrashWarnings : ICrashWarnings
{
    /// <inheritdoc />
    public IDisposable Register(Action<object> onCrash)
    {
        UnhandledExceptionEventHandler crashing = (_, e) => onCrash(e.ExceptionObject);
        AppDomain.CurrentDomain.UnhandledException += crashing;
        return new Registration(crashing);
    }

    /// <summary>One warning; disposing it, once or more, unhooks it.</summary>
    private sealed class Registration(UnhandledExceptionEventHandler crashing) : IDisposable
    {
        /// <summary>Unhooks the warning; unhooking it again does nothing.</summary>
        public void Dispose() => AppDomain.CurrentDomain.UnhandledException -= crashing;
    }
}
