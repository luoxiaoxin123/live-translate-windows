namespace LiveTranslate.Core.Audio;

/// <summary>
/// Mixes two 16 kHz mono PCM16 LE streams (system audio + microphone) by sample averaging.
/// Emits mixed audio only for the overlapping portion; each side is buffered in a bounded
/// queue (drop-oldest) so a stalled source cannot grow latency without limit.
/// </summary>
public sealed class PcmMixer
{
    private const int MaxQueueSamples = 24_000; // 1.5 s at 16 kHz mono

    private readonly Queue<short> _media = new();
    private readonly Queue<short> _mic = new();
    private readonly object _lock = new();
    private readonly Action<byte[]> _onMixed;

    public PcmMixer(Action<byte[]> onMixed) => _onMixed = onMixed;

    public void OfferMedia(byte[] pcm16k) => Offer(_media, pcm16k);

    public void OfferMic(byte[] pcm16k) => Offer(_mic, pcm16k);

    public void Reset()
    {
        lock (_lock)
        {
            _media.Clear();
            _mic.Clear();
        }
    }

    private void Offer(Queue<short> target, byte[] pcm)
    {
        if (pcm.Length < 2) return;

        byte[]? mixed = null;
        lock (_lock)
        {
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                target.Enqueue((short)(pcm[i] | (pcm[i + 1] << 8)));
            }
            while (target.Count > MaxQueueSamples) target.Dequeue();

            var available = Math.Min(_media.Count, _mic.Count);
            if (available > 0)
            {
                mixed = new byte[available * 2];
                for (var i = 0; i < available; i++)
                {
                    var value = (_media.Dequeue() + _mic.Dequeue()) / 2;
                    var sample = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                    mixed[i * 2] = (byte)(sample & 0xFF);
                    mixed[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }
            }
        }

        if (mixed != null) _onMixed(mixed);
    }
}
