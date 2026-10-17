using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Opens Windows' process loopback: an audio client that delivers the sound of one process and its
/// child processes, whatever output device they play through. Windows 10 build 20348 and later, so
/// Windows 11 and Windows Server 2022, not Windows 10 version 2004 as the SDK's headers first said.
/// </summary>
/// <remarks>
/// NAudio 2.2.1 declares the activation parameters but has no public way to pass them, so the call to
/// <c>ActivateAudioInterfaceAsync</c> is made here. The parameters travel as a blob in a PROPVARIANT,
/// and the answer arrives on a Windows thread through a completion handler.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ProcessLoopback
{
    /// <summary>The virtual device path Windows uses for process loopback (VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK).</summary>
    private const string VirtualDevicePath = @"VAD\Process_Loopback";

    private const ushort VarTypeBlob = 65;
    private const int ActivationTypeProcessLoopback = 1;
    private const int IncludeTargetProcessTree = 0;

    private static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Opens an audio client for the sound of <paramref name="processId"/> and its child processes.</summary>
    public static AudioClient Activate(int processId)
    {
        var parameters = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = IncludeTargetProcessTree
        };

        var parametersPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(parameters, parametersPointer, fDeleteOld: false);
            Marshal.StructureToPtr(
                new PropVariantBlob { VarType = VarTypeBlob, BlobSize = (uint)Marshal.SizeOf<ActivationParams>(), BlobData = parametersPointer },
                variantPointer,
                fDeleteOld: false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevicePath, AudioClientInterfaceId, variantPointer, handler, out var operation);
            var activated = handler.WaitForResult(ActivationTimeout);
            GC.KeepAlive(operation);
            return new AudioClient((IAudioClient)activated);
        }
        finally
        {
            Marshal.FreeHGlobal(variantPointer);
            Marshal.FreeHGlobal(parametersPointer);
        }
    }

    /// <summary>
    /// Asks Windows to activate an audio interface, here an audio client, for the device at
    /// <paramref name="deviceInterfacePath"/> with the activation parameters given. It returns at once, and
    /// Windows reports the result to <paramref name="completionHandler"/> on a thread of its own. A request
    /// Windows refuses outright is thrown as an exception.
    /// </summary>
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS with its one union member, AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS, laid out as Windows expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>A PROPVARIANT holding a BLOB: the variant type, three reserved words, then the blob's size and data pointer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    /// <summary>Windows calls this when the activation has finished.</summary>
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        /// <summary>Reports that the activation has finished; its result is read from the operation.</summary>
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>The activation in progress, which yields the activated interface once finished.</summary>
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        /// <summary>Reads the activation's result code and, on success, the activated interface.</summary>
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marks the handler as callable from any COM apartment, which Windows requires of it.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    /// <summary>Hands the activation's result from the Windows thread that reports it to the thread that waits for it.</summary>
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<object> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var result, out var activated);
                if (result < 0)
                    _result.TrySetException(Marshal.GetExceptionForHR(result) ?? new COMException("Windows could not open the app's sound for capture.", result));
                else
                    _result.TrySetResult(activated);
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: this runs on a Windows thread, where an exception would be lost
                // or end the process. It is handed to the thread that waits for the result instead.
                _result.TrySetException(ex);
            }
        }

        /// <summary>Waits for the activated interface, or throws when the activation failed or did not finish in time.</summary>
        public object WaitForResult(TimeSpan timeout)
        {
            var finished = Task.WhenAny(_result.Task, Task.Delay(timeout)).GetAwaiter().GetResult();
            if (finished != _result.Task)
                throw new TimeoutException($"Windows did not open the app's sound for capture within {timeout.TotalSeconds} seconds.");

            return _result.Task.GetAwaiter().GetResult();
        }
    }
}
