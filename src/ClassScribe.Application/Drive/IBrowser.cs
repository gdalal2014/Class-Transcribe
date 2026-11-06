namespace ClassScribe.Drive;

/// <summary>Opens a web address in the person's browser, for Google's sign-in.</summary>
public interface IBrowser
{
    /// <summary>Opens <paramref name="address"/> in the default browser; false when it could not be opened, so the person opens it by hand.</summary>
    bool Open(Uri address);
}
