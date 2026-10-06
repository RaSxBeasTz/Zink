using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Tasks;
using Zink.Interop;

namespace Zink.Services.Recording
{
    public sealed class ProcessExcludingLoopbackCaptureService : WasapiCaptureBase
    {
        private readonly uint _targetProcessId;

        private ProcessExcludingLoopbackCaptureService(uint targetProcessId)
        {
            _targetProcessId = targetProcessId;
        }

        public static IAudioCaptureService CreateForApplicationOrSystemLoopback(string processName)
        {
            var application = Process.GetProcesses()
                .Where(process => process.ProcessName.StartsWith(processName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(process =>
                {
                    try { return process.StartTime; }
                    catch { return DateTime.MaxValue; }
                })
                .FirstOrDefault();

            if (application is null)
                return new SystemLoopbackCaptureService();

            return new ProcessExcludingLoopbackCaptureService((uint)application.Id);
        }

        protected override bool UseLoopback => true;
        protected override EDataFlow DataFlow => EDataFlow.eRender;
        private protected override bool UsesFixedLoopbackFormat => true;

        private protected override Task<IAudioClient?> ActivateCustomAudioClientAsync()
        {
            return ProcessLoopbackActivator.ActivateAsync(_targetProcessId);
        }
    }

    internal static class ProcessLoopbackActivator
    {
        private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
        private const ushort VtBlob = 65;
        private static readonly StrategyBasedComWrappers ComWrappers = new();
        private static readonly Guid CompletionHandlerId = new("41D949AB-9862-444A-80F6-C261334DA5EB");

        public static Task<IAudioClient?> ActivateAsync(uint processId)
        {
            var activation = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = processId,
                    ProcessLoopbackMode = ProcessLoopbackMode.ExcludeTargetProcessTree
                }
            };

            var activationPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<AudioClientActivationParams>());
            Marshal.StructureToPtr(activation, activationPointer, false);
            var variant = new PropVariant
            {
                VariantType = VtBlob,
                Blob = new Blob
                {
                    Size = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                    Data = activationPointer
                }
            };

            var handler = new AudioActivationCompletionHandler();
            var unknownPointer = ComWrappers.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
            IntPtr handlerPointer = IntPtr.Zero;
            IntPtr operationPointer = IntPtr.Zero;
            try
            {
                HResult.Check(Marshal.QueryInterface(unknownPointer, in CompletionHandlerId, out handlerPointer),
                    "QueryInterface(IActivateAudioInterfaceCompletionHandler)");
                var audioClientId = CoreAudioGuids.IID_IAudioClient;
                HResult.Check(
                    ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref audioClientId, ref variant, handlerPointer, out operationPointer),
                    "ActivateAudioInterfaceAsync(process loopback)");
                return WaitForCompletionAsync(handler);
            }
            finally
            {
                if (operationPointer != IntPtr.Zero)
                    Marshal.Release(operationPointer);
                if (handlerPointer != IntPtr.Zero)
                    Marshal.Release(handlerPointer);
                Marshal.Release(unknownPointer);
                Marshal.FreeCoTaskMem(activationPointer);
                GC.KeepAlive(handler);
            }
        }

        private static async Task<IAudioClient?> WaitForCompletionAsync(AudioActivationCompletionHandler handler)
        {
            return await handler.Completion.Task.ConfigureAwait(false);
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid interfaceId,
            ref PropVariant activationParams,
            IntPtr completionHandler,
            out IntPtr activationOperation);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientActivationParams
    {
        public AudioClientActivationType ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    internal enum AudioClientActivationType : uint
    {
        Default,
        ProcessLoopback
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    internal enum ProcessLoopbackMode : uint
    {
        IncludeTargetProcessTree,
        ExcludeTargetProcessTree
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Blob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public Blob Blob;
    }

    [GeneratedComInterface]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    internal partial interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IntPtr operation);
    }

    [GeneratedComInterface]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    internal partial interface IAgileObject
    {
    }

    [GeneratedComClass]
    internal sealed partial class AudioActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public TaskCompletionSource<IAudioClient?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public unsafe int ActivateCompleted(IntPtr operation)
        {
            try
            {
                var vtable = *(IntPtr**)operation;
                var getActivateResult = (delegate* unmanaged[Stdcall]<IntPtr, int*, IntPtr*, int>)vtable[3];
                var activationResult = 0;
                var activatedInterfacePointer = IntPtr.Zero;
                var hr = getActivateResult(operation, &activationResult, &activatedInterfacePointer);
                HResult.Check(hr, "IActivateAudioInterfaceAsyncOperation.GetActivateResult");
                HResult.Check(activationResult, "Process-loopback IAudioClient activation");
                try
                {
                    Completion.TrySetResult(new RawProcessLoopbackAudioClient(activatedInterfacePointer));
                    activatedInterfacePointer = IntPtr.Zero;
                }
                finally
                {
                    if (activatedInterfacePointer != IntPtr.Zero)
                        Marshal.Release(activatedInterfacePointer);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
                return Marshal.GetHRForException(ex);
            }
        }
    }

    internal sealed class RawProcessLoopbackAudioClient : IAudioClient, IDisposable
    {
        private IntPtr _instance;
        private readonly IntPtr[] _vtable;

        public RawProcessLoopbackAudioClient(IntPtr instance)
        {
            if (instance == IntPtr.Zero)
                throw new ArgumentNullException(nameof(instance));

            _instance = instance;
            var vtablePointer = Marshal.ReadIntPtr(instance);
            _vtable = new IntPtr[15];
            for (var index = 0; index < _vtable.Length; index++)
                _vtable[index] = Marshal.ReadIntPtr(vtablePointer, index * IntPtr.Size);
        }

        public int Initialize(AUDCLNT_SHAREMODE shareMode, AUDCLNT_STREAMFLAGS streamFlags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr sessionGuid) =>
            Get<InitializeDelegate>(3)(_instance, shareMode, streamFlags, bufferDuration, periodicity, format, sessionGuid);

        public int GetBufferSize(out uint bufferSize) => Get<GetUIntDelegate>(4)(_instance, out bufferSize);
        public int GetStreamLatency(out long latency) => Get<GetLongDelegate>(5)(_instance, out latency);
        public int GetCurrentPadding(out uint padding) => Get<GetUIntDelegate>(6)(_instance, out padding);
        public int IsFormatSupported(AUDCLNT_SHAREMODE shareMode, IntPtr format, out IntPtr closestMatch) =>
            Get<IsFormatSupportedDelegate>(7)(_instance, shareMode, format, out closestMatch);
        public int GetMixFormat(out IntPtr format) => Get<GetPointerDelegate>(8)(_instance, out format);
        public int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod) =>
            Get<GetDevicePeriodDelegate>(9)(_instance, out defaultPeriod, out minimumPeriod);
        public int Start() => Get<SimpleDelegate>(10)(_instance);
        public int Stop() => Get<SimpleDelegate>(11)(_instance);
        public int Reset() => Get<SimpleDelegate>(12)(_instance);
        public int SetEventHandle(IntPtr eventHandle) => Get<SetEventHandleDelegate>(13)(_instance, eventHandle);

        public int GetService(ref Guid interfaceId, out object service)
        {
            var hr = Get<GetServiceDelegate>(14)(_instance, ref interfaceId, out var servicePointer);
            if (hr < 0 || servicePointer == IntPtr.Zero)
            {
                service = null!;
                return hr;
            }

            try
            {
                service = Marshal.GetObjectForIUnknown(servicePointer);
                return hr;
            }
            finally
            {
                Marshal.Release(servicePointer);
            }
        }

        private T Get<T>(int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(_vtable[slot]);

        public void Dispose()
        {
            var instance = System.Threading.Interlocked.Exchange(ref _instance, IntPtr.Zero);
            if (instance != IntPtr.Zero)
                Marshal.Release(instance);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeDelegate(IntPtr self, AUDCLNT_SHAREMODE shareMode, AUDCLNT_STREAMFLAGS flags,
            long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUIntDelegate(IntPtr self, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetLongDelegate(IntPtr self, out long value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointerDelegate(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IsFormatSupportedDelegate(IntPtr self, AUDCLNT_SHAREMODE mode, IntPtr format, out IntPtr closestMatch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDevicePeriodDelegate(IntPtr self, out long defaultPeriod, out long minimumPeriod);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleDelegate(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetEventHandleDelegate(IntPtr self, IntPtr eventHandle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetServiceDelegate(IntPtr self, ref Guid interfaceId, out IntPtr service);
    }
}
