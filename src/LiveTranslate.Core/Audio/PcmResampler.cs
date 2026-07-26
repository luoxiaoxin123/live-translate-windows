namespace LiveTranslate.Core.Audio;

/// <summary>
/// Stateful linear-interpolation resampler for mono PCM16. Carries the fractional
/// read position and the last sample across calls so chunk boundaries stay phase-continuous.
/// </summary>
public sealed class PcmResampler
{
    private readonly double _step;
    private double _position; // fractional index into the current chunk; -1 refers to _previous
    private short _previous;

    public PcmResampler(int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(fromRate), "Sample rates must be positive.");
        FromRate = fromRate;
        ToRate = toRate;
        _step = (double)fromRate / toRate;
    }

    public int FromRate { get; }
    public int ToRate { get; }

    /// <summary>Resamples mono 16-bit samples; returns little-endian PCM16 bytes at the target rate.</summary>
    public byte[] Resample(short[] input, int count)
    {
        if (count <= 0) return Array.Empty<byte>();

        if (FromRate == ToRate)
        {
            var direct = new byte[count * 2];
            Buffer.BlockCopy(input, 0, direct, 0, direct.Length);
            return direct;
        }

        var estimated = (int)(count / _step) + 2;
        var output = new byte[estimated * 2];
        var written = 0;

        var pos = _position;
        while (true)
        {
            var i0 = (int)Math.Floor(pos);
            var i1 = i0 + 1;
            if (i1 >= count) break; // need the next chunk to interpolate further

            var s0 = i0 < 0 ? _previous : input[i0];
            var s1 = input[i1];
            var frac = pos - i0;
            var value = (int)Math.Round(s0 + (s1 - s0) * frac);
            var sample = (short)Math.Clamp(value, short.MinValue, short.MaxValue);

            output[written++] = (byte)(sample & 0xFF);
            output[written++] = (byte)((sample >> 8) & 0xFF);
            pos += _step;
        }

        _position = pos - count;
        _previous = input[count - 1];

        if (written == output.Length) return output;
        var trimmed = new byte[written];
        Buffer.BlockCopy(output, 0, trimmed, 0, written);
        return trimmed;
    }

    public void Reset()
    {
        _position = 0;
        _previous = 0;
    }
}
