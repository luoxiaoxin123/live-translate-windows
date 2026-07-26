using System.Runtime.InteropServices;

namespace LiveTranslate.Core.Audio;

/// <summary>
/// Captures all system playback EXCEPT this process tree, using WASAPI process-loopback
/// activation (Windows 10 2004+). Our own translated-audio playback is never re-captured,
/// so media-mode translation cannot feed back on itself.
///
/// Follows the flow of the Microsoft ApplicationLoopback sample:
/// ActivateAudioInterfaceAsync(VAD\Process_Loopback) with AUDIOCLIENT_ACTIVATION_PARAMS
/// (EXCLUDE_TARGET_PROCESS_TREE), then an event-driven IAudioCaptureClient loop.
/// The virtual device has no mix format, so we request 48 kHz stereo 16-bit and
/// downmix/resample to 16 kHz mono in managed code.
/// </summary>
public sealed class ProcessExcludeLoopbackCapturer : IDisposable
{
    private const int CaptureRate = 48000;
    private const int CaptureChannels = 2;

    private Thread? _thread;
    private volatile bool _stopping;
    private Action<byte[]>? _onPcm16k;

    public bool IsRunning { get; private set; }

    /// <summary>Invoked once from the capture thread if the stream dies and cannot recover.</summary>
    public Action<string>? OnFatalError { get; set; }

    /// <summary>Starts capture; throws if process-loopback activation fails (caller falls back).</summary>
    public void Start(Action<byte[]> onPcm16k)
    {
        Stop();
        _onPcm16k = onPcm16k;
        _stopping = false;

        Exception? startError = null;
        using var started = new ManualResetEventSlim(false);

        var thread = new Thread(() => CaptureThread(err => { startError = err; started.Set(); }))
        {
            IsBackground = true,
            Name = "process-loopback-capture",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        if (!started.Wait(TimeSpan.FromSeconds(10)))
        {
            _stopping = true;
            throw new InvalidOperationException("Process-loopback activation timed out.");
        }
        if (startError != null)
        {
            _stopping = true;
            thread.Join(2000);
            throw startError;
        }
        _thread = thread;
        IsRunning = true;
    }

    public void Stop()
    {
        _stopping = true;
        var thread = _thread;
        _thread = null;
        thread?.Join(3000);
        IsRunning = false;
        _onPcm16k = null;
    }

    public void Dispose() => Stop();

    private void CaptureThread(Action<Exception?> reportStart)
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        AutoResetEvent? bufferEvent = null;
        try
        {
            audioClient = ActivateProcessLoopbackClient();

            var format = new WaveFormatEx
            {
                FormatTag = 1, // PCM
                Channels = CaptureChannels,
                SamplesPerSec = CaptureRate,
                BitsPerSample = 16,
                BlockAlign = (ushort)(CaptureChannels * 2),
                AvgBytesPerSec = CaptureRate * CaptureChannels * 2,
                Size = 0,
            };

            const int AUDCLNT_SHAREMODE_SHARED = 0;
            const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
            const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;

            var hr = audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                2_000_000, // 200 ms buffer
                0,
                ref format,
                IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);

            bufferEvent = new AutoResetEvent(false);
            hr = audioClient.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle());
            Marshal.ThrowExceptionForHR(hr);

            var captureIid = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            hr = audioClient.GetService(ref captureIid, out var captureObj);
            Marshal.ThrowExceptionForHR(hr);
            captureClient = (IAudioCaptureClient)captureObj;

            hr = audioClient.Start();
            Marshal.ThrowExceptionForHR(hr);

            reportStart(null);
            RunCaptureLoop(captureClient, bufferEvent);
        }
        catch (Exception ex)
        {
            reportStart(ex);
        }
        finally
        {
            try { audioClient?.Stop(); } catch { }
            if (captureClient != null) Marshal.ReleaseComObject(captureClient);
            if (audioClient != null) Marshal.ReleaseComObject(audioClient);
            bufferEvent?.Dispose();
        }
    }

    private void RunCaptureLoop(IAudioCaptureClient captureClient, AutoResetEvent bufferEvent)
    {
        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        var resampler = new PcmResampler(CaptureRate, 16000);
        var mono = new short[CaptureRate / 5];
        var raw = Array.Empty<byte>();

        var consecutiveFailures = 0;
        while (!_stopping)
        {
            bufferEvent.WaitOne(200);
            while (!_stopping)
            {
                var hr = captureClient.GetNextPacketSize(out var packetFrames);
                if (hr < 0)
                {
                    // ~25 failed wakeups ≈ 5 s of a dead stream (audio service restart etc.) — give up loudly.
                    if (++consecutiveFailures > 25)
                    {
                        try { OnFatalError?.Invoke($"system audio stream failed (0x{hr:X8})"); } catch { }
                        return;
                    }
                    break;
                }
                consecutiveFailures = 0;
                if (packetFrames == 0) break;

                hr = captureClient.GetBuffer(out var dataPtr, out var frames, out var flags, out _, out _);
                if (hr < 0)
                {
                    if (++consecutiveFailures > 25)
                    {
                        try { OnFatalError?.Invoke($"system audio stream failed (0x{hr:X8})"); } catch { }
                        return;
                    }
                    break;
                }

                try
                {
                    if (frames > 0)
                    {
                        var bytes = (int)frames * CaptureChannels * 2;
                        if (raw.Length < bytes) raw = new byte[bytes];

                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        {
                            Array.Clear(raw, 0, bytes);
                        }
                        else
                        {
                            Marshal.Copy(dataPtr, raw, 0, bytes);
                        }

                        var samples = DownmixStereo16(raw, bytes, ref mono);
                        var pcm = resampler.Resample(mono, samples);
                        if (pcm.Length > 0)
                        {
                            try { _onPcm16k?.Invoke(pcm); } catch { }
                        }
                    }
                }
                finally
                {
                    captureClient.ReleaseBuffer(frames);
                }
            }
        }
    }

    private static int DownmixStereo16(byte[] raw, int bytes, ref short[] mono)
    {
        var frames = bytes / 4;
        if (mono.Length < frames) mono = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            var left = (short)(raw[i * 4] | (raw[i * 4 + 1] << 8));
            var right = (short)(raw[i * 4 + 2] | (raw[i * 4 + 3] << 8));
            mono[i] = (short)((left + right) / 2);
        }
        return frames;
    }

    private static IAudioClient ActivateProcessLoopbackClient()
    {
        const int activationParamsSize = 12; // int ActivationType + { uint TargetProcessId, int Mode }
        var paramsPtr = Marshal.AllocHGlobal(activationParamsSize);
        try
        {
            Marshal.WriteInt32(paramsPtr, 0, 1);  // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(paramsPtr, 4, Environment.ProcessId);
            Marshal.WriteInt32(paramsPtr, 8, 1);  // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE

            var propVariant = new PropVariantBlob
            {
                Vt = 65, // VT_BLOB
                BlobSize = activationParamsSize,
                BlobData = paramsPtr,
            };

            var handler = new ActivationHandler();
            var audioClientIid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            var hr = ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback",
                ref audioClientIid,
                ref propVariant,
                handler,
                out var operation);
            Marshal.ThrowExceptionForHR(hr);

            if (!handler.Wait(TimeSpan.FromSeconds(8)))
                throw new InvalidOperationException("ActivateAudioInterfaceAsync did not complete.");

            hr = operation.GetActivateResult(out var activateHr, out var activated);
            Marshal.ThrowExceptionForHR(hr);
            Marshal.ThrowExceptionForHR(activateHr);
            Marshal.ReleaseComObject(operation);

            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    // ---- interop ----

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariantBlob activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData; // aligned to offset 16 on x64, matching PROPVARIANT.blob
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    /// <summary>Vtable order must match audioclient.h exactly — do not reorder.</summary>
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, ref WaveFormatEx format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint framesRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint framesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint framesInNextPacket);
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _completed = new(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => _completed.Set();

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);
    }
}
