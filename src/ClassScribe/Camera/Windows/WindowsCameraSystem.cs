using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop.Windows;
using Microsoft.Extensions.Logging;
using CodecCategory = global::Windows.Media.Core.CodecCategory;
using CodecKind = global::Windows.Media.Core.CodecKind;
using CodecQuery = global::Windows.Media.Core.CodecQuery;
using CodecSubtypes = global::Windows.Media.Core.CodecSubtypes;

namespace ClassScribe.Camera.Windows;

/// <summary>
/// What Windows says the camera needs, read without opening a camera: its version, whether an H.264 encoder is installed
/// (WinRT's CodecQuery, which lists the encoders and opens none), and the antivirus programs Windows' Security Center knows
/// of and says are on (its documented product list, WSCProductList, through ClassScribe's own declarations in
/// <see cref="IWscProductListRaw"/>, since Windows registers no type library for late binding).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCameraSystem(ILogger<WindowsCameraSystem> logger) : ICameraSystem
{
    /// <summary>CLSID_WSCProductList, Windows' Security Center's list of security products.</summary>
    private static readonly Guid ProductListClass = new("17072F7B-9ABE-4A74-A261-1EB76B55107A");

    /// <summary>WSC_SECURITY_PROVIDER_ANTIVIRUS: the antivirus programs, which is where Bitdefender's Webcam Protection belongs.</summary>
    private const int Antivirus = 4;

    /// <summary>WSC_SECURITY_PRODUCT_STATE_ON: a product that is on.</summary>
    private const int ProductOn = 0;

    /// <inheritdoc />
    public Version WindowsVersion => Environment.OSVersion.Version;

    /// <inheritdoc />
    public async Task<bool?> HasH264EncoderAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) return null;

        try
        {
            var encoders = await new CodecQuery().FindAllAsync(CodecKind.Video, CodecCategory.Encoder, CodecSubtypes.VideoFormatH264).AsTask(cancellationToken);
            return encoders.Count > 0;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or TypeLoadException)
        {
            // Deliberately not rethrown: whether an encoder is there only adds a line to the check, which then says that
            // Windows did not say; nothing was opened.
            logger.LogDebug(ex, "Could not list the H.264 encoders (0x{Code:X8})", ex.HResult);
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>Only the products Security Center says are on: Microsoft Defender stands by (off) beside another antivirus.</remarks>
    public IReadOnlyList<string>? SecurityPrograms()
    {
        IWscProductListRaw? list = null;
        try
        {
            if (Type.GetTypeFromCLSID(ProductListClass, throwOnError: false) is not { } type) return null;

            list = (IWscProductListRaw)Activator.CreateInstance(type)!;
            Check(list.Initialize(Antivirus));
            Check(list.GetCount(out var count));
            List<string> names = [];
            for (var index = 0u; index < count; index++)
            {
                Check(list.GetItem(index, out var product));
                try
                {
                    Check(product.GetProductName(out var name));
                    Check(product.GetProductState(out var state));
                    if (state == ProductOn && name.Trim().Length > 0) names.Add(name.Trim());
                }
                finally
                {
                    Marshal.ReleaseComObject(product);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or NotSupportedException)
        {
            // Deliberately not rethrown: which security program runs only adds advice to the check, which then gives the
            // advice for any program; Security Center is not on every edition of Windows.
            logger.LogDebug(ex, "Could not read Windows' Security Center's list of security programs (0x{Code:X8})", ex.HResult);
            return null;
        }
        finally
        {
            if (list is not null) Marshal.ReleaseComObject(list);
        }
    }

    /// <summary>Throws the exception that matches a failed result; does nothing for a success.</summary>
    private static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }
}

/// <summary>Windows' record of which apps used a camera and when (ConsentStore\webcam), read as the microphone's is (see <see cref="ConsentStoreRecord"/>).</summary>
[SupportedOSPlatform("windows")]
public sealed class CameraUsageRecord(ILogger<CameraUsageRecord> logger) : ICameraUsageRecord
{
    /// <summary>The record's key under HKEY_CURRENT_USER.</summary>
    public const string WebcamKey = $@"{ConsentStoreRecord.ConsentStore}\webcam";

    /// <inheritdoc />
    public UsageRecordListing Read() => ConsentStoreRecord.Read(WebcamKey, "camera", logger);
}
