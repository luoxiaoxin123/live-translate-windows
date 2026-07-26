using NAudio.Wave;

namespace LiveTranslate.Core.Audio;

/// <summary>Converts captured WASAPI buffers of any common format to mono 16-bit samples.</summary>
public static class PcmConvert
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    /// <summary>
    /// Converts raw bytes in the given format (IEEE float or PCM 16/32, any channel count)
    /// to mono short samples by averaging channels. Returns the number of mono samples written.
    /// </summary>
    public static int ToMonoShorts(byte[] buffer, int bytes, WaveFormat format, ref short[] output)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0) return 0;

        var frames = bytes / (bytesPerSample * channels);
        if (frames <= 0) return 0;
        if (output.Length < frames) output = new short[frames];

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

        for (var frame = 0; frame < frames; frame++)
        {
            var acc = 0.0;
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = (frame * channels + ch) * bytesPerSample;
                double sample;
                if (isFloat && bytesPerSample == 4)
                {
                    sample = BitConverter.ToSingle(buffer, offset) * short.MaxValue;
                }
                else if (bytesPerSample == 2)
                {
                    sample = BitConverter.ToInt16(buffer, offset);
                }
                else if (bytesPerSample == 4)
                {
                    sample = BitConverter.ToInt32(buffer, offset) / 65536.0;
                }
                else if (bytesPerSample == 3)
                {
                    var v = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    sample = v / 256.0;
                }
                else
                {
                    sample = 0;
                }
                acc += sample;
            }
            output[frame] = (short)Math.Clamp(acc / channels, short.MinValue, short.MaxValue);
        }

        return frames;
    }

    /// <summary>Applies a digital gain to 16-bit LE PCM in place, clamping to the short range.</summary>
    public static void ApplyGain(byte[] pcm16le, float gain)
    {
        if (Math.Abs(gain - 1f) < 0.001f) return;
        for (var i = 0; i + 1 < pcm16le.Length; i += 2)
        {
            var sample = (short)(pcm16le[i] | (pcm16le[i + 1] << 8));
            var boosted = (int)Math.Round(sample * gain);
            var clamped = (short)Math.Clamp(boosted, short.MinValue, short.MaxValue);
            pcm16le[i] = (byte)(clamped & 0xFF);
            pcm16le[i + 1] = (byte)((clamped >> 8) & 0xFF);
        }
    }
}
