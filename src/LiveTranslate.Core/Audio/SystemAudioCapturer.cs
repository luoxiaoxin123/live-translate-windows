using NAudio.Wave;

namespace LiveTranslate.Core.Audio;

/// <summary>
/// Captures system playback audio as 16 kHz mono PCM16 LE.
/// Prefers process-exclude loopback (our own translated-audio playback is never captured);
/// falls back to classic WASAPI loopback, where <see cref="ShouldMuteOutgoing"/> gates out
/// chunks while our own TTS is audible to avoid a feedback loop.
/// </summary>
public sealed class SystemAudioCapturer : IDisposable
{
    private ProcessExcludeLoopbackCapturer? _processExclude;
    private WasapiLoopbackCapture? _classic;
    private PcmResampler? _classicResampler;
    private short[] _monoBuffer = new short[9600];
    private Action<byte[]>? _onPcm16k;

    /// <summary>Classic-loopback anti-feedback gate: return true to drop the current chunk.</summary>
    public Func<bool>? ShouldMuteOutgoing { get; set; }

    public bool UsingProcessExclude { get; private set; }

    public bool IsRunning { get; private set; }

    public void Start(Action<byte[]> onPcm16k)
    {
        Stop();
        _onPcm16k = onPcm16k;

        try
        {
            var capturer = new ProcessExcludeLoopbackCapturer();
            capturer.Start(pcm => _onPcm16k?.Invoke(pcm));
            _processExclude = capturer;
            UsingProcessExclude = true;
        }
        catch
        {
            UsingProcessExclude = false;
            StartClassic();
        }
        IsRunning = true;
    }

    private void StartClassic()
    {
        var classic = new WasapiLoopbackCapture();
        var format = classic.WaveFormat;
        _classicResampler = format.SampleRate == 16000 ? null : new PcmResampler(format.SampleRate, 16000);
        classic.DataAvailable += OnClassicData;
        classic.RecordingStopped += (_, _) => { };
        classic.StartRecording();
        _classic = classic;
    }

    private void OnClassicData(object? sender, WaveInEventArgs e)
    {
        var onPcm = _onPcm16k;
        if (onPcm == null || e.BytesRecorded <= 0) return;
        if (ShouldMuteOutgoing?.Invoke() == true) return;

        try
        {
            var format = ((WasapiLoopbackCapture)sender!).WaveFormat;
            var samples = PcmConvert.ToMonoShorts(e.Buffer, e.BytesRecorded, format, ref _monoBuffer);
            if (samples <= 0) return;

            byte[] pcm;
            if (_classicResampler == null)
            {
                pcm = new byte[samples * 2];
                Buffer.BlockCopy(_monoBuffer, 0, pcm, 0, pcm.Length);
            }
            else
            {
                pcm = _classicResampler.Resample(_monoBuffer, samples);
            }
            if (pcm.Length > 0) onPcm(pcm);
        }
        catch
        {
            // Keep the capture callback alive no matter what.
        }
    }

    public void Stop()
    {
        IsRunning = false;
        _onPcm16k = null;

        _processExclude?.Dispose();
        _processExclude = null;

        var classic = _classic;
        _classic = null;
        if (classic != null)
        {
            classic.DataAvailable -= OnClassicData;
            try { classic.StopRecording(); } catch { }
            classic.Dispose();
        }
        _classicResampler = null;
    }

    public void Dispose() => Stop();
}
