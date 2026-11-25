using System.Security.Cryptography;
using System.Text;

namespace Battuta.TestSupport;

/// <summary>
/// Deterministic PCM WAV fixtures shared by DIY audio/pack tests on every
/// platform. Writers are pure managed so tests run without NAudio.
/// </summary>
public static class WaveFixture
{
    public static string WriteStereoSine(
        string path,
        int sampleRate = 44_100,
        double durationSeconds = 0.12,
        double frequency = 930)
    {
        var frames = (int)Math.Round(sampleRate * durationSeconds, MidpointRounding.AwayFromZero);
        var samples = new short[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var envelope = Math.Exp(-frame / (sampleRate * 0.045));
            var value = Math.Sin(2 * Math.PI * frequency * frame / sampleRate) * envelope * 0.55;
            samples[frame * 2] = Quantize(value);
            samples[(frame * 2) + 1] = Quantize(value * 0.82);
        }

        WritePcm16(path, sampleRate, channels: 2, samples);
        return path;
    }

    public static string WriteCompleteKeystroke(
        string path,
        int sampleRate = 44_100,
        double durationSeconds = 0.18)
    {
        var frames = (int)Math.Round(sampleRate * durationSeconds, MidpointRounding.AwayFromZero);
        var samples = new short[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var time = (double)frame / sampleRate;
            var press = Burst(time, center: 0.022, width: 0.010, frequency: 880, amplitude: 0.72);
            var release = Burst(time, center: 0.116, width: 0.008, frequency: 1_540, amplitude: 0.58);
            var value = press + release;
            samples[frame * 2] = Quantize(value);
            samples[(frame * 2) + 1] = Quantize(value * 0.90);
        }

        WritePcm16(path, sampleRate, channels: 2, samples);
        return path;
    }

    public static void WritePcm16(
        string path,
        int sampleRate,
        short channels,
        ReadOnlySpan<short> interleavedSamples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        var dataBytes = checked(interleavedSamples.Length * sizeof(short));
        writer.Write("RIFF"u8);
        writer.Write(checked(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * channels * sizeof(short)));
        writer.Write(checked((short)(channels * sizeof(short))));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in interleavedSamples)
        {
            writer.Write(sample);
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static double Burst(
        double time,
        double center,
        double width,
        double frequency,
        double amplitude)
    {
        var offset = time - center;
        var envelope = Math.Exp(-(offset * offset) / (2 * width * width));
        return Math.Sin(2 * Math.PI * frequency * time) * envelope * amplitude;
    }

    private static short Quantize(double value) =>
        (short)Math.Round(
            Math.Clamp(value, -1, 0.999_969_5) * short.MaxValue,
            MidpointRounding.AwayFromZero);
}
