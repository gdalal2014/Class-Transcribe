using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BitmapBufferAccessMode = global::Windows.Graphics.Imaging.BitmapBufferAccessMode;
using BitmapPixelFormat = global::Windows.Graphics.Imaging.BitmapPixelFormat;
using IMemoryBufferReference = global::Windows.Foundation.IMemoryBufferReference;
using SoftwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap;

namespace ClassScribe.Camera.Windows;

/// <summary>
/// A frame of a record job as Windows hands it over (teach-video 6.6, probe step 5): its SystemRelativeTime, and its
/// SoftwareBitmap in CPU memory, NV12 at the reader's size, whose two planes are copied row by row, at their own strides,
/// into a pooled buffer through IMemoryBufferByteAccess on the CsWinRT object, while Windows still holds the frame.
/// </summary>
/// <param name="stamp">The frame's SystemRelativeTime, in units of 100 nanoseconds; null when Windows gave none.</param>
/// <param name="bitmap">The frame's pixels; null when Windows handed over none in CPU memory.</param>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsCameraFrame(long? stamp, SoftwareBitmap? bitmap) : ICameraFrame
{
    /// <inheritdoc />
    public long? Stamp100ns => stamp;

    /// <inheritdoc />
    public int Width => bitmap?.PixelWidth ?? 0;

    /// <inheritdoc />
    public int Height => bitmap?.PixelHeight ?? 0;

    /// <inheritdoc />
    public bool CopyNv12To(byte[] target)
    {
        if (bitmap is null || bitmap.BitmapPixelFormat != BitmapPixelFormat.Nv12) return false;

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (target.Length < width * height * 3 / 2) return false;

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        if (buffer.GetPlaneCount() < 2) return false;

        var brightness = buffer.GetPlaneDescription(0);
        var colour = buffer.GetPlaneDescription(1);
        using var reference = buffer.CreateReference();
        var data = MemoryBufferBytes.Of(reference, out var capacity);
        if ((long)brightness.StartIndex + (long)brightness.Stride * (height - 1) + width > capacity
            || (long)colour.StartIndex + (long)colour.Stride * (height / 2 - 1) + width > capacity)
            return false;

        for (var row = 0; row < height; row++) Marshal.Copy(data + brightness.StartIndex + row * brightness.Stride, target, row * width, width);
        for (var row = 0; row < height / 2; row++) Marshal.Copy(data + colour.StartIndex + row * colour.Stride, target, width * height + row * width, width);
        return true;
    }
}

/// <summary>The bytes behind a WinRT memory buffer reference, through IMemoryBufferByteAccess: valid while the reference is open.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class MemoryBufferBytes
{
    /// <summary>The start of the bytes behind <paramref name="reference"/>, and how many there are.</summary>
    public static IntPtr Of(IMemoryBufferReference reference, out uint capacity)
    {
        var native = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        var iid = typeof(IMemoryBufferByteAccess).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out var pointer));
        try
        {
            var access = (IMemoryBufferByteAccess)Marshal.GetObjectForIUnknown(pointer);
            try
            {
                access.GetBuffer(out var bytes, out capacity);
                return bytes;
            }
            finally
            {
                Marshal.ReleaseComObject(access);
            }
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>IMemoryBufferByteAccess (MemoryBuffer.idl): the one method after IUnknown's.</summary>
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMemoryBufferByteAccess
    {
        /// <summary>The bytes and their number.</summary>
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
