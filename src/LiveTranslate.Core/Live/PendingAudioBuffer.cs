namespace LiveTranslate.Core.Live;

/// <summary>
/// Holds PCM captured while no connection is ready (the planned ~10-minute connection rotation,
/// reconnect backoff) so it can be replayed after the next setupComplete instead of being dropped —
/// otherwise speech in the gap never gets captioned.
///
/// Keeps at most <c>maxBytes</c>, dropping the oldest whole chunks beyond that. <see cref="Drain"/>
/// coalesces the backlog into frames of up to <c>maxFrameBytes</c> so the replay is a handful of
/// messages rather than hundreds. Not thread-safe; callers synchronize.
/// </summary>
public sealed class PendingAudioBuffer(int maxBytes, int maxFrameBytes)
{
    private readonly LinkedList<byte[]> _chunks = new();

    public int SizeBytes { get; private set; }

    /// <summary>Bytes discarded since the last <see cref="Clear"/>/<see cref="Drain"/> because the buffer was full.</summary>
    public int DroppedBytes { get; private set; }

    public void Add(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        _chunks.AddLast(chunk);
        SizeBytes += chunk.Length;
        TrimToCapacity();
    }

    /// <summary>Puts chunks (oldest first) ahead of everything already buffered.</summary>
    public void Prepend(IReadOnlyList<byte[]> chunks)
    {
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            if (chunks[i].Length == 0) continue;
            _chunks.AddFirst(chunks[i]);
            SizeBytes += chunks[i].Length;
        }
        TrimToCapacity();
    }

    /// <summary>Returns the raw chunks oldest-first and empties the buffer.</summary>
    public List<byte[]> TakeChunks()
    {
        var chunks = _chunks.ToList();
        Clear();
        return chunks;
    }

    /// <summary>Returns the backlog oldest-first as coalesced frames and empties the buffer.</summary>
    public List<byte[]> Drain()
    {
        var frames = new List<byte[]>();
        using var frame = new MemoryStream(Math.Min(SizeBytes, maxFrameBytes));
        foreach (var chunk in _chunks)
        {
            if (frame.Length > 0 && frame.Length + chunk.Length > maxFrameBytes)
            {
                frames.Add(frame.ToArray());
                frame.SetLength(0);
            }
            frame.Write(chunk);
        }
        if (frame.Length > 0) frames.Add(frame.ToArray());
        Clear();
        return frames;
    }

    public void Clear()
    {
        _chunks.Clear();
        SizeBytes = 0;
        DroppedBytes = 0;
    }

    private void TrimToCapacity()
    {
        while (SizeBytes > maxBytes && _chunks.First is { } oldest)
        {
            _chunks.RemoveFirst();
            SizeBytes -= oldest.Value.Length;
            DroppedBytes += oldest.Value.Length;
        }
    }
}
