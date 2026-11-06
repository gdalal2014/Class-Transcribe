using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>
/// Opens an address in the default browser the Windows way, as a link is opened from any program. The address of a sign-in
/// holds its state, so it is never logged: Windows' own message about a failure names the address, so only its error number is.
/// </summary>
public sealed class ShellBrowser(ILogger<ShellBrowser> logger) : IBrowser
{
    /// <inheritdoc />
    public bool Open(Uri address)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception ex)
        {
            // Deliberately not rethrown: the window shows the address to open by hand, so a browser that does not open costs
            // nothing but a copy and paste. The exception is not logged: its message holds the address.
            logger.LogWarning("Could not open the browser (Windows error {Error})", ex.NativeErrorCode);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            // Deliberately not rethrown: as above.
            logger.LogWarning("Could not open the browser ({Kind})", ex.GetType().Name);
            return false;
        }
    }
}
