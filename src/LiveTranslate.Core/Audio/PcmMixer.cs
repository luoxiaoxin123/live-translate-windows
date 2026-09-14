using System.Runtime.InteropServices;

namespace LiveTranslate.Core.Audio;

/// <summary>
/// Mixes two 16 kHz mono PCM16 LE streams (system audio + microphone) by sample averaging.
/// Whole capture chunks are queued (no per-sample queueing) and mixed in span blocks, so
/// MediaAndMic stays cheap on the capture threads. WASAPI loopback stops delivering packets
/// while nothing is playing, so when one side has been silent for a while the other side is
/// passed through directly instead of being starved. Each side is bounded (drop-oldest) to
/// keep latency bounded. The callback is invoked while holding the internal lock so chunks
/// stay in order. Capturers hand over fresh arrays, so chunks are queued without copying.
/// </summary>
public sealed class PcmMixer
{
    private const int MaxQueueBytes = 48_000;   // 1.5 s at 16 kHz mono PCM16
    private const long StaleAfterMs = 500;      // one side silent this long → pass the other through

    private readonly ChunkQueue _media = new();
    private readonly ChunkQueue _mic = new();
    private readonly object _lock = new();
    private readonly Action<byte[]> _onMixed;
    private long _lastMediaAt;
    private long _lastMicAt;

    public PcmMixer(Action<byte[]> onMixed)
    {
        _onMixed = onMixed;
        _lastMediaAt = _lastMicAt = Environment.TickCount64; // both count as "just alive" at start
    }

    public void OfferMedia(byte[] pcm16k) => Offer(pcm16k, isMedia: true);

    public void OfferMic(byte[] pcm16k) => Offer(pcm16k, isMedia: false);

    public void Reset()
    {
        lock (_lock)
        {
            _media.Clear();
            _mic.Clear();
            _lastMediaAt = _lastMicAt = Environment.TickCount64;
        }
    }

    private void Offer(byte[] pcm, bool isMedia)
    {
        // A trailing odd byte is half a sample; it can never be mixed, so it is dropped here.
        var usable = pcm.Length & ~1;
        if (usable == 0) return;

        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (isMedia) _lastMediaAt = now; else _lastMicAt = now;

            var own = isMedia ? _media : _mic;
            own.Enqueue(new ArraySegment<byte>(pcm, 0, usable));
            own.TrimTo(MaxQueueBytes);

            if (MixAvailable()) return;

            // Nothing to pair: if the other side has been silent for a while, don't hold
            // this side's audio hostage — flush it straight through.
            var otherLastAt = isMedia ? _lastMicAt : _lastMediaAt;
            if (now - otherLastAt >= StaleAfterMs && own.Bytes > 0)
            {
                _onMixed(own.DequeueAll());
            }
        }
    }

    private bool MixAvailable()
    {
        var total = Math.Min(_media.Bytes, _mic.Bytes);
        if (total == 0) return false;

        var mixed = new byte[total];
        var written = 0;
        while (written < total)
        {
            var media = _media.Head;
            var mic = _mic.Head;
            // Every queued segment has an even length, so block sizes stay sample-aligned.
            var block = Math.Min(total - written, Math.Min(media.Length, mic.Length));
            MixAverage(media[..block], mic[..block], mixed.AsSpan(written, block));
            _media.Advance(block);
            _mic.Advance(block);
            written += block;
        }

        _onMixed(mixed);
        return true;
    }

    /// <summary>Averages two equal-length PCM16 LE blocks into <paramref name="output"/>.</summary>
    public static void MixAverage(ReadOnlySpan<byte> media, ReadOnlySpan<byte> mic, Span<byte> output)
    {
        var a = MemoryMarshal.Cast<byte, short>(media);
        var b = MemoryMarshal.Cast<byte, short>(mic);
        var o = MemoryMarshal.Cast<byte, short>(output);
        for (var i = 0; i < o.Length; i++)
        {
            o[i] = (short)((a[i] + b[i]) / 2); // the mean of two shorts always fits a short
        }
    }

    /// <summary>FIFO of PCM chunks with a read offset into the head chunk.</summary>
    private sealed class ChunkQueue
    {
        private readonly Queue<ArraySegment<byte>> _chunks = new();
        private int _headOffset;

        public int Bytes { get; private set; }

        public ReadOnlySpan<byte> Head => _chunks.Peek().AsSpan(_headOffset);

        public void Enqueue(ArraySegment<byte> chunk)
        {
            _chunks.Enqueue(chunk);
            Bytes += chunk.Count;
        }

        public void Advance(int bytes)
        {
            _headOffset += bytes;
            Bytes -= bytes;
            if (_headOffset == _chunks.Peek().Count)
            {
                _chunks.Dequeue();
                _headOffset = 0;
            }
        }

        /// <summary>Drops the oldest audio (in whole samples) until at most <paramref name="maxBytes"/> remain.</summary>
        public void TrimTo(int maxBytes)
        {
            while (Bytes > maxBytes)
            {
                var headRemaining = _chunks.Peek().Count - _headOffset;
                var excess = (Bytes - maxBytes + 1) & ~1;
                Advance(Math.Min(excess, headRemaining));
            }
        }

        public byte[] DequeueAll()
        {
            if (_chunks.Count == 1 && _headOffset == 0)
            {
                var only = _chunks.Dequeue();
                Bytes = 0;
                if (only.Offset == 0 && only.Count == only.Array!.Length) return only.Array;
                return only.AsSpan().ToArray();
            }

            var result = new byte[Bytes];
            var written = 0;
            while (Bytes > 0)
            {
                var head = Head;
                head.CopyTo(result.AsSpan(written));
                written += head.Length;
                Advance(head.Length);
            }
            return result;
        }

        public void Clear()
        {
            _chunks.Clear();
            _headOffset = 0;
            Bytes = 0;
        }
    }
}
