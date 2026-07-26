namespace LiveTranslate.Core.Audio;

/// <summary>
/// Mixes two 16 kHz mono PCM16 LE streams (system audio + microphone) by sample averaging.
/// WASAPI loopback stops delivering packets while nothing is playing, so when one side has
/// been silent for a while the other side is passed through directly instead of being
/// starved. Each side is buffered in a bounded queue (drop-oldest) to keep latency bounded.
/// The callback is invoked while holding the internal lock so chunks stay in order.
/// </summary>
public sealed class PcmMixer
{
    private const int MaxQueueSamples = 24_000;   // 1.5 s at 16 kHz mono
    private const long StaleAfterMs = 500;        // one side silent this long → pass the other through

    private readonly Queue<short> _media = new();
    private readonly Queue<short> _mic = new();
    private readonly object _lock = new();
    private readonly Action<byte[]> _onMixed;
    private long _lastMediaAt;
    private long _lastMicAt;

    public PcmMixer(Action<byte[]> onMixed)
    {
        _onMixed = onMixed;
        _lastMediaAt = _lastMicAt = Environment.TickCount64; // both count as "just alive" at start
    }

    public void OfferMedia(byte[] pcm16k) => Offer(_media, _mic, pcm16k, isMedia: true);

    public void OfferMic(byte[] pcm16k) => Offer(_mic, _media, pcm16k, isMedia: false);

    public void Reset()
    {
        lock (_lock)
        {
            _media.Clear();
            _mic.Clear();
            _lastMediaAt = _lastMicAt = Environment.TickCount64;
        }
    }

    private void Offer(Queue<short> own, Queue<short> other, byte[] pcm, bool isMedia)
    {
        if (pcm.Length < 2) return;

        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (isMedia) _lastMediaAt = now; else _lastMicAt = now;

            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                own.Enqueue((short)(pcm[i] | (pcm[i + 1] << 8)));
            }
            while (own.Count > MaxQueueSamples) own.Dequeue();

            var available = Math.Min(_media.Count, _mic.Count);
            if (available > 0)
            {
                var mixed = new byte[available * 2];
                for (var i = 0; i < available; i++)
                {
                    var value = (_media.Dequeue() + _mic.Dequeue()) / 2;
                    var sample = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                    mixed[i * 2] = (byte)(sample & 0xFF);
                    mixed[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }
                _onMixed(mixed);
                return;
            }

            // Nothing to pair: if the other side has been silent for a while, don't hold
            // this side's audio hostage — flush it straight through.
            var otherLastAt = isMedia ? _lastMicAt : _lastMediaAt;
            if (now - otherLastAt >= StaleAfterMs && own.Count > 0)
            {
                var direct = new byte[own.Count * 2];
                var index = 0;
                while (own.Count > 0)
                {
                    var sample = own.Dequeue();
                    direct[index++] = (byte)(sample & 0xFF);
                    direct[index++] = (byte)((sample >> 8) & 0xFF);
                }
                _onMixed(direct);
            }
        }
    }
}
