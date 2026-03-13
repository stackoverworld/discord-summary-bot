namespace DiscordSummaryBot;

public static class WavUtility
{
    public static byte[] PcmToWav(byte[] pcm, int sampleRate = 48000, short channels = 2, short bitsPerSample = 16)
    {
        using var stream = new MemoryStream(capacity: pcm.Length + 44);
        using var writer = new BinaryWriter(stream);

        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();

        return stream.ToArray();
    }
}
