using System.IO;

namespace ReadSelectedTextTts.Tts;

internal static class PcmWave
{
    public static byte[] Create(float[] samples, int sampleRate)
    {
        using var stream = new MemoryStream(44 + samples.Length * sizeof(short));
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * sizeof(short));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * sizeof(short));
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        return stream.ToArray();
    }
}
