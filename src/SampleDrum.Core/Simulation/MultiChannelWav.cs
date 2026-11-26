using System.Buffers.Binary;
using System.Text;

namespace SampleDrum.Core.Simulation;

/// <summary>
/// Writes interleaved multichannel 32-bit float WAV files (e.g. the 4-channel return file and the stereo preview mix).
/// More than 2 channels use WAVE_FORMAT_EXTENSIBLE (KSDATAFORMAT_SUBTYPE_IEEE_FLOAT), which
/// <see cref="WavFile.Read(string, int)"/> also reads. Off the audio thread only.
/// </summary>
public static class MultiChannelWav
{
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Writes <paramref name="channels"/> (all the same length or shorter; missing samples = 0).</summary>
    public static void Write(string path, IReadOnlyList<float[]> channels, int sampleRate)
    {
        if (channels.Count == 0) throw new ArgumentException("Pelo menos um canal.", nameof(channels));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        Write(fs, channels, sampleRate);
    }

    public static void Write(Stream stream, IReadOnlyList<float[]> channels, int sampleRate)
    {
        int nCh = channels.Count;
        int frames = 0;
        foreach (var c in channels) frames = Math.Max(frames, c.Length);
        bool ext = nCh > 2;
        int blockAlign = nCh * 4;
        long dataBytes = (long)frames * blockAlign;
        int fmtSize = ext ? 40 : 16;
        if (dataBytes > uint.MaxValue - 100) throw new NotSupportedException("Arquivo grande demais para WAV.");

        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        bw.Write("RIFF"u8);
        bw.Write((uint)(4 + 8 + fmtSize + 8 + dataBytes));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write((uint)fmtSize);
        bw.Write(ext ? FormatExtensible : FormatFloat);
        bw.Write((ushort)nCh);
        bw.Write(sampleRate);
        bw.Write(sampleRate * blockAlign);
        bw.Write((ushort)blockAlign);
        bw.Write((ushort)32);
        if (ext)
        {
            bw.Write((ushort)22);          // cbSize
            bw.Write((ushort)32);          // valid bits
            bw.Write(0u);                  // channel mask: none (discrete channels)
            // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT {00000003-0000-0010-8000-00aa00389b71}
            bw.Write(FormatFloat); bw.Write((ushort)0);
            bw.Write((ushort)0x0000); bw.Write((ushort)0x0010);
            bw.Write(new byte[] { 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);

        var buf = new byte[blockAlign * 1024];
        int pos = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < nCh; c++)
            {
                var ch = channels[c];
                float v = f < ch.Length ? ch[f] : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos), float.IsFinite(v) ? v : 0f);
                pos += 4;
            }
            if (pos == buf.Length) { bw.Write(buf, 0, pos); pos = 0; }
        }
        if (pos > 0) bw.Write(buf, 0, pos);
    }
}
