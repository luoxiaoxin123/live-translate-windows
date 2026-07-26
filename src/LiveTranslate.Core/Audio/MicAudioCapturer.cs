using NAudio.Wave;

namespace LiveTranslate.Core.Audio;

/// <summary>
/// Captures the default microphone and emits 16 kHz mono PCM16 LE chunks.
/// Tries to open the device directly at 16 kHz; falls back to 44.1 kHz + resampling.
/// </summary>
public sealed class MicAudioCapturer : IDisposable
{
    private WaveInEvent? _waveIn;
    private PcmResampler? _resampler;
    private short[] _monoBuffer = new short[3200];
    private Action<byte[]>? _onPcm16k;

    public bool IsRunning { get; private set; }

    public void Start(Action<byte[]> onPcm16k)
    {
        Stop();
        _onPcm16k = onPcm16k;

        var waveIn = CreateWaveIn(16000) ?? CreateWaveIn(44100)
            ?? throw new InvalidOperationException("No usable microphone device found.");
        _waveIn = waveIn;
        _resampler = waveIn.WaveFormat.SampleRate == 16000 ? null : new PcmResampler(waveIn.WaveFormat.SampleRate, 16000);
        IsRunning = true;
    }

    private WaveInEvent? CreateWaveIn(int sampleRate)
    {
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(sampleRate, 16, 1),
            BufferMilliseconds = 100,
            NumberOfBuffers = 4,
        };
        waveIn.DataAvailable += OnDataAvailable;
        try
        {
            waveIn.StartRecording();
            return waveIn;
        }
        catch
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.Dispose();
            return null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var onPcm = _onPcm16k;
        if (onPcm == null || e.BytesRecorded < 2) return;

        try
        {
            if (_resampler == null)
            {
                var direct = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, direct, 0, e.BytesRecorded);
                onPcm(direct);
                return;
            }

            var format = ((WaveInEvent)sender!).WaveFormat;
            var samples = PcmConvert.ToMonoShorts(e.Buffer, e.BytesRecorded, format, ref _monoBuffer);
            if (samples <= 0) return;
            var resampled = _resampler.Resample(_monoBuffer, samples);
            if (resampled.Length > 0) onPcm(resampled);
        }
        catch
        {
            // Never let an audio callback exception tear down the capture thread.
        }
    }

    public void Stop()
    {
        IsRunning = false;
        _onPcm16k = null;
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn != null)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            try { waveIn.StopRecording(); } catch { }
            waveIn.Dispose();
        }
        _resampler = null;
    }

    public void Dispose() => Stop();
}
