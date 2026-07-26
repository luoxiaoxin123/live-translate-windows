using System.Text.RegularExpressions;
using System.Threading.Channels;
using NAudio.Wave;

namespace LiveTranslate.Core.Audio;

/// <summary>
/// Plays translated speech PCM returned by the Live API, in parallel with the source media.
/// A dedicated writer task owns the output device; chunks are queued drop-oldest so latency
/// stays bounded. Volume up to 200%: device volume covers 0–100%, above that a per-sample
/// digital gain is applied with clamping.
/// </summary>
public sealed partial class TranslatedAudioPlayer : IDisposable
{
    public const float MaxVolume = 2.0f;
    private const int DefaultRate = 24000;
    private const int MaxQueuedChunks = 32;

    private readonly Channel<(byte[] Pcm, int Rate)> _queue = Channel.CreateBounded<(byte[], int)>(
        new BoundedChannelOptions(MaxQueuedChunks)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly object _deviceLock = new();

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private int _currentRate;
    private float _volume = 0.8f;
    private volatile bool _enabled;
    private long _lastWriteTicks;

    public TranslatedAudioPlayer()
    {
        _writerTask = Task.Run(WriterLoopAsync);
    }

    /// <summary>True while our own translated audio is (or was in the last ~300 ms) audible.</summary>
    public bool IsActivelyPlaying
    {
        get
        {
            if (!_enabled) return false;
            if (_queue.Reader.Count > 0) return true; // queued but not yet written is still "about to be audible"
            lock (_deviceLock)
            {
                if ((_provider?.BufferedBytes ?? 0) > 0) return true;
            }
            var last = Interlocked.Read(ref _lastWriteTicks);
            return DateTime.UtcNow.Ticks - last < TimeSpan.FromMilliseconds(300).Ticks;
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            while (_queue.Reader.TryRead(out _)) { }
            lock (_deviceLock)
            {
                _provider?.ClearBuffer();
                try { _waveOut?.Stop(); } catch { }
            }
        }
    }

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, MaxVolume);
        lock (_deviceLock)
        {
            if (_waveOut != null) _waveOut.Volume = Math.Min(1f, _volume);
        }
    }

    public void PlayPcm(byte[] pcm, string? mimeType)
    {
        if (!_enabled || pcm.Length == 0) return;
        _queue.Writer.TryWrite((pcm, ParseRate(mimeType)));
    }

    /// <summary>Parses the sample rate from a mime like "audio/pcm;rate=24000"; clamps to 8k–48k.</summary>
    public static int ParseRate(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return DefaultRate;
        var match = RateRegex().Match(mimeType);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var rate)) return DefaultRate;
        return Math.Clamp(rate, 8000, 48000);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var (pcm, rate) in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (!_enabled) continue;
                try
                {
                    WriteChunk(pcm, rate);
                    Interlocked.Exchange(ref _lastWriteTicks, DateTime.UtcNow.Ticks);
                }
                catch
                {
                    // Device hiccups (unplugged output etc.) must not kill the loop; retry with a fresh device.
                    lock (_deviceLock)
                    {
                        DisposeDevice();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void WriteChunk(byte[] pcm, int rate)
    {
        lock (_deviceLock)
        {
            if (_provider == null || _waveOut == null || _currentRate != rate)
            {
                DisposeDevice();
                _provider = new BufferedWaveProvider(new WaveFormat(rate, 16, 1))
                {
                    BufferDuration = TimeSpan.FromSeconds(5),
                    DiscardOnBufferOverflow = true,
                };
                _waveOut = new WaveOutEvent { DesiredLatency = 200 };
                _waveOut.Init(_provider);
                _waveOut.Volume = Math.Min(1f, _volume);
                _waveOut.Play();
                _currentRate = rate;
            }

            var volume = _volume;
            if (volume > 1f)
            {
                var boosted = new byte[pcm.Length];
                Buffer.BlockCopy(pcm, 0, boosted, 0, pcm.Length);
                PcmConvert.ApplyGain(boosted, volume);
                pcm = boosted;
            }

            _provider.AddSamples(pcm, 0, pcm.Length);
            if (_waveOut.PlaybackState != PlaybackState.Playing) _waveOut.Play();
        }
    }

    private void DisposeDevice()
    {
        try { _waveOut?.Stop(); } catch { }
        _waveOut?.Dispose();
        _waveOut = null;
        _provider = null;
        _currentRate = 0;
    }

    public void Dispose()
    {
        _enabled = false;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _writerTask.Wait(2000); } catch { }
        lock (_deviceLock)
        {
            DisposeDevice();
        }
        _cts.Dispose();
    }

    [GeneratedRegex(@"rate=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RateRegex();
}
