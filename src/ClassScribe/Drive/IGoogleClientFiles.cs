namespace ClassScribe.Drive;

/// <summary>
/// Reads the client file Google offers when a client is made (Google Auth platform > Clients > Download JSON): a Desktop app
/// client's id and secret (architecture 4.3). Any other file is refused with what to do instead, and nothing the file holds
/// is repeated in the message, since it may be the secret.
/// </summary>
public interface IGoogleClientFiles
{
    /// <summary>
    /// The client in the file at <paramref name="path"/>. Throws a <see cref="GoogleDriveException"/> of
    /// <see cref="DriveProblem.ClientFile"/> when there is no file there, or one that is not a Desktop app client's file
    /// from Google, saying what to do instead.
    /// </summary>
    GoogleClient Read(string path);
}
