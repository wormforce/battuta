using System.Buffers.Binary;
using Battuta.Windows.Audio.Linux;

namespace Battuta.Engine.Tests.Audio.Linux;

/// <summary>
/// Cross-platform validation for the BTP1 product container read by the
/// Linux sample loader (plan §6.2). The writer side is implemented by the
/// Rust <c>battuta-audio-prep</c> crate.
/// </summary>
public sealed class PreparedAudioProductLoaderTests
{
    private static byte[] ProductBytes(
        uint sampleRate,
        uint channels,
        ReadOnlySpan<float> frames,
        uint? declaredFrames = null)
    {
        using var stream = new MemoryStream();
        stream.Write("BTP1"u8);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], declaredFrames ?? (uint)frames.Length);
        stream.Write(header);
        Span<byte> payload = stackalloc byte[frames.Length * sizeof(float)];
        for (var index = 0; index < frames.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                payload[(index * sizeof(float))..], frames[index]);
        }

        stream.Write(payload);
        return stream.ToArray();
    }

    [Fact]
    public void DecodesAValidProduct()
    {
        var bytes = ProductBytes(48_000, 1, stackalloc float[] { 0f, 0.25f, -0.5f, 1f });
        using var stream = new MemoryStream(bytes);

        var sample = PreparedAudioProductLoader.DecodeCore(stream, "test.p48m", default);

        Assert.Equal(4, sample.FrameCount);
        Assert.Equal(0f, sample.Samples.Span[0]);
        Assert.Equal(0.25f, sample.Samples.Span[1]);
        Assert.Equal(-0.5f, sample.Samples.Span[2]);
        Assert.Equal(1f, sample.Samples.Span[3]);
    }

    [Fact]
    public void RejectsWrongMagic()
    {
        var bytes = ProductBytes(48_000, 1, stackalloc float[] { 0f });
        bytes[0] = (byte)'X';
        using var stream = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(
            () => PreparedAudioProductLoader.DecodeCore(stream, "test.p48m", default));
    }

    [Fact]
    public void RejectsWrongSampleRate()
    {
        var bytes = ProductBytes(44_100, 1, stackalloc float[] { 0f });
        using var stream = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(
            () => PreparedAudioProductLoader.DecodeCore(stream, "test.p48m", default));
    }

    [Fact]
    public void RejectsStereoProducts()
    {
        var bytes = ProductBytes(48_000, 2, stackalloc float[] { 0f, 0f });
        using var stream = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(
            () => PreparedAudioProductLoader.DecodeCore(stream, "test.p48m", default));
    }

    [Fact]
    public void RejectsTruncatedPayload()
    {
        var bytes = ProductBytes(
            48_000, 1, stackalloc float[] { 0f, 0.25f }, declaredFrames: 8);
        using var stream = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(
            () => PreparedAudioProductLoader.DecodeCore(stream, "test.p48m", default));
    }

    [Fact]
    public void DecodeResolvesProductFilesNextToSources()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"battuta-products-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "mxblack", "press"));
        try
        {
            var productPath = Path.Combine(directory, "mxblack", "press", "GENERIC_R0.p48m");
            File.WriteAllBytes(
                productPath,
                ProductBytes(48_000, 1, stackalloc float[] { 0f, 0.5f }));

            var loader = new PreparedAudioProductLoader();
            var pcm = loader.Decode(
                Path.Combine(directory, "mxblack", "press", "GENERIC_R0.mp3"),
                trimKeyboardLeadingSilence: true);

            Assert.Equal(2, pcm.FrameCount);
            Assert.Equal(0.5f, pcm.Samples.Span[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
