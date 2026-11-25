using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Battuta.Windows.Audio.Linux;

/// <summary>
/// Linux <see cref="IAudioSampleLoader"/> reading the deterministic PCM
/// products written by <c>battuta-audio-prep</c> at build time (plan §6.2).
/// The realtime hot path never calls this loader — it runs during profile
/// bank preparation only. Runtime MP3 decoding is not available by design;
/// the caller surfaces a missing-product error instead of falling back.
/// </summary>
public sealed class PreparedAudioProductLoader : IAudioSampleLoader
{
    private const uint ProductMagic = 0x31505442; // "BTP1" little endian
    private const int HeaderBytes = 16;
    private const int SampleRate = 48_000;
    private const int ChannelCount = 1;

    /// <summary>Optional root directory holding the product tree.</summary>
    private readonly string? productRoot;

    public PreparedAudioProductLoader(string? productRoot = null)
    {
        this.productRoot = productRoot;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Keyboard products are onset-trimmed at build time, so the
    /// <paramref name="trimKeyboardLeadingSilence"/> flag only documents the
    /// caller's expectation; no runtime trimming happens on Linux.
    /// </remarks>
    public PreparedPcmSample Decode(
        string path,
        bool trimKeyboardLeadingSilence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var productPath = ResolveProductPath(path);
        using var stream = File.OpenRead(productPath);
        return DecodeCore(stream, productPath, cancellationToken);
    }

    /// <summary>Validates a product stream and converts it to a prepared sample.</summary>
    internal static PreparedPcmSample DecodeCore(
        Stream stream,
        string productName,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        stream.ReadExactly(header);
        var magic = BitConverter.ToUInt32(header);
        if (magic != ProductMagic)
        {
            throw new InvalidDataException($"{productName} is not a BTP1 audio product.");
        }

        var sampleRate = BitConverter.ToUInt32(header[4..]);
        if (sampleRate != SampleRate)
        {
            throw new InvalidDataException(
                $"{productName} must be 48 kHz (found {sampleRate}).");
        }

        var channels = BitConverter.ToUInt32(header[8..]);
        if (channels != ChannelCount)
        {
            throw new InvalidDataException(
                $"{productName} must be mono (found {channels} channels).");
        }

        var frameCount = BitConverter.ToUInt32(header[12..]);
        var expectedBytes = checked(frameCount * sizeof(float));
        var payloadBytes = new byte[expectedBytes];
        if (stream.Length - HeaderBytes != expectedBytes)
        {
            throw new InvalidDataException(
                $"{productName} payload is {(stream.Length - HeaderBytes)} bytes; expected {expectedBytes}.");
        }

        stream.ReadExactly(payloadBytes);
        var payload = new float[frameCount];
        payloadBytes.CopyTo(MemoryMarshal.AsBytes<float>(new Span<float>(payload)));
        return new PreparedPcmSample(payload);
    }

    /// <summary>
    /// Verifies a product file against its SHA-256 sidecar (written by the
    /// prep tool as <c>&lt;product&gt;.sha256</c>). Returns true when no
    /// sidecar exists.
    /// </summary>
    internal static bool VerifyChecksum(string productPath)
    {
        var sidecarPath = productPath + ".sha256";
        if (!File.Exists(sidecarPath))
        {
            return true;
        }

        var expected = File.ReadAllText(sidecarPath).Trim();
        using var stream = File.OpenRead(productPath);
        var hash = SHA256.HashData(stream);
        var actual = Convert.ToHexString(hash);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a canonical source asset path (MP3/WAV under the build output's
    /// Assets/Sounds tree) to the prep product file with the same relative
    /// location and a <c>.p48m</c> extension.
    /// </summary>
    private string ResolveProductPath(string path)
    {
        if (productRoot is null)
        {
            return Path.ChangeExtension(path, ".p48m");
        }

        var marker = Path.Combine("Assets", "Sounds");
        var sourceRoot = Path.GetFullPath(path);
        var root = Path.GetFullPath(productRoot);
        var index = sourceRoot.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return Path.Combine(root, Path.GetFileName(Path.ChangeExtension(path, ".p48m")));
        }

        var relative = sourceRoot[(index + marker.Length)..].TrimStart(Path.DirectorySeparatorChar);
        return Path.Combine(
            root,
            Path.ChangeExtension(relative, ".p48m"));
    }
}
