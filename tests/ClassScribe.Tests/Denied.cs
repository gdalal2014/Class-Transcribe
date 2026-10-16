using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClassScribe.Tests;

/// <summary>
/// Keeps the current user from looking into a folder, or from reading a file, until disposed, as with one
/// copied from another account. The user owns it, so may always give the right back, and needs no
/// administrator rights for any of it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Denied : IDisposable
{
    private readonly FileSystemInfo _item;
    private readonly FileSystemAccessRule _rule;

    /// <summary>Denies the current user <paramref name="rights"/> on <paramref name="item"/>.</summary>
    private Denied(FileSystemInfo item, FileSystemRights rights)
    {
        using var user = WindowsIdentity.GetCurrent();
        _item = item;
        _rule = new FileSystemAccessRule(user.User!, rights, AccessControlType.Deny);
        Change(security => security.AddAccessRule(_rule));
    }

    /// <summary>Denies the current user the listing of the folder <paramref name="folder"/>.</summary>
    public static Denied ListingOf(string folder) => new(new DirectoryInfo(folder), FileSystemRights.ListDirectory);

    /// <summary>Denies the current user the reading of the file <paramref name="file"/>, which still shows in its folder.</summary>
    public static Denied ReadingOf(string file) => new(new FileInfo(file), FileSystemRights.ReadData);

    /// <summary>Gives the right back, so the folder or file can be read and deleted again.</summary>
    public void Dispose() => Change(security => security.RemoveAccessRule(_rule));

    /// <summary>Reads the permissions of the folder or file, changes them and writes them back.</summary>
    private void Change(Action<FileSystemSecurity> change)
    {
        switch (_item)
        {
            case DirectoryInfo folder:
                var folderSecurity = folder.GetAccessControl();
                change(folderSecurity);
                folder.SetAccessControl(folderSecurity);
                break;
            case FileInfo file:
                var fileSecurity = file.GetAccessControl();
                change(fileSecurity);
                file.SetAccessControl(fileSecurity);
                break;
        }
    }
}
