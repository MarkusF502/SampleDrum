using System.Buffers.Binary;
using System.Text;

namespace SampleDrum.Core;

/// <summary>Sample encodings supported by <see cref="WavFile.WriteMono(string, ReadOnlySpan{float}, int, WavSampleFormat)"/>.</summary>
public enum WavSampleFormat { Pcm16, Pcm24, Float32 }

/// <summary>Decoded WAV contents, already downmixed to mono.</summary>
/// <param name="Mono">Mono samples (average of all channels), at <paramref name="SampleRate"/>.</param>
/// <param name="SampleRate">File sample rate (Hz).</param>
/// <param name="Channels">Channel count in the file (before downmix).</param>
/// <param name="BitsPerSample">Bits per sample in the file.</param>
/// <param name="IsFloat">True for IEEE float files.</param>
public sealed record WavData(float[] Mono, int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

/// <summary>
/// Dependency-free RIFF/WAVE reader/writer. Reads PCM 8/16/24/32-bit, IEEE float 32/64-bit and
/// WAVE_FORMAT_EXTENSIBLE; any channel count is downmixed to mono by averaging.
/// Not for the audio thread (allocates and does I/O).
/// </summary>
public static class WavFile
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static WavData Read(string path) => Read(path, -1);

    public static WavData Read(Stream stream) => Read(stream, -1);

    /// <summary>Reads one channel (0-based) of a multichannel file, or the mono downmix when <paramref name="channel"/> is -1.</summary>
    public static WavData Read(string path, int channel)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, channel);
    }

    /// <summary>Reads one channel (0-based) of a multichannel stream, or the mono downmix when <paramref name="channel"/> is -1.</summary>
    public static WavData Read(Stream stream, int channel)
    {
        using var br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadTag(br) != "RIFF") throw new InvalidDataException("Arquivo não é RIFF/WAV.");
        br.ReadUInt32();
        if (ReadTag(br) != "WAVE") throw new InvalidDataException("Arquivo RIFF não é WAVE.");

        ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
        int rate = 0;
        bool haveFmt = false;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            string id = ReadTag(br);
            uint size = br.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = br.ReadUInt16();
                channels = br.ReadUInt16();
                rate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                blockAlign = br.ReadUInt16();
                bits = br.ReadUInt16();
                if (format == FormatExtensible && size >= 40)
                {
                    br.ReadUInt16(); // cbSize
                    br.ReadUInt16(); // valid bits
                    br.ReadUInt32(); // channel mask
                    var guid = br.ReadBytes(16);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(guid); // first 2 bytes = format tag
                }
                haveFmt = true;
            }
            else if (id == "data")
            {
                long avail = stream.Length - stream.Position;
                int len = (int)Math.Min(size, avail); // tolerate truncated files / size 0xFFFFFFFF
                data = br.ReadBytes(len);
            }
            if (next > stream.Length) break;
            stream.Position = next;
        }

        if (!haveFmt) throw new InvalidDataException("WAV sem chunk 'fmt '.");
        if (data == null) throw new InvalidDataException("WAV sem chunk 'data'.");
        if (channels == 0 || rate <= 0) throw new InvalidDataException("WAV com cabeçalho inválido.");

        bool isFloat = format == FormatFloat;
        if (format != FormatPcm && !isFloat)
            throw new NotSupportedException($"Formato WAV não suportado (tag {format}). Use PCM ou float.");
        int bytesPerSample = bits / 8;
        if (isFloat ? bits is not (32 or 64) : bits is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"Profundidade não suportada: {bits} bits ({(isFloat ? "float" : "PCM")}).");
        int frameBytes = Math.Max(blockAlign, bytesPerSample * channels);
        int frames = data.Length / frameBytes;

        var mono = new float[frames];
        if (channel < -1 || channel >= channels)
            throw new ArgumentOutOfRangeException(nameof(channel), channel,
                $"O arquivo tem {channels} canal(is); canal pedido: {channel + 1}.");
        int firstCh = channel < 0 ? 0 : channel;
        int nCh = channel < 0 ? channels : 1;
        float inv = 1f / nCh;
        var span = data.AsSpan();
        for (int f = 0; f < frames; f++)
        {
            int off = f * frameBytes;
            float sum = 0f;
            off += firstCh * bytesPerSample;
            for (int c = 0; c < nCh; c++, off += bytesPerSample)
                sum += Decode(span.Slice(off, bytesPerSample), bits, isFloat);
            mono[f] = sum * inv;
        }
        return new WavData(mono, rate, channels, bits, isFloat);
    }

    private static float Decode(ReadOnlySpan<byte> b, int bits, bool isFloat)
    {
        if (isFloat)
            return bits == 32 ? BinaryPrimitives.ReadSingleLittleEndian(b) : (float)BinaryPrimitives.ReadDoubleLittleEndian(b);
        return bits switch
        {
            8 => (b[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
            24 => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
        };
    }

    /// <summary>
    /// Reads a file, downmixes to mono and resamples offline to <paramref name="targetSampleRate"/> when needed.
    /// <paramref name="note"/> is a pt-BR message describing any conversion, otherwise null.
    /// </summary>
    public static float[] ReadMonoAtRate(string path, int targetSampleRate, out string? note)
        => ReadMonoAtRate(path, targetSampleRate, -1, out note);

    /// <summary>
    /// Like <see cref="ReadMonoAtRate(string, int, out string?)"/>, but takes one channel (0-based) of a
    /// multichannel file instead of the downmix when <paramref name="channel"/> &gt;= 0.
    /// </summary>
    public static float[] ReadMonoAtRate(string path, int targetSampleRate, int channel, out string? note)
    {
        var wav = Read(path, channel);
        var notes = new List<string>();
        if (channel >= 0 && wav.Channels != 1) notes.Add($"usando só o canal {channel + 1} de {wav.Channels}");
        else if (wav.Channels != 1) notes.Add($"{wav.Channels} canais convertidos para mono (média)");
        float[] result = wav.Mono;
        if (wav.SampleRate != targetSampleRate)
        {
            result = Resample(wav.Mono, wav.SampleRate, targetSampleRate);
            notes.Add($"reamostrado de {wav.SampleRate} Hz para {targetSampleRate} Hz (offline, sinc com janela)");
        }
        note = notes.Count == 0 ? null : $"{Path.GetFileName(path)}: " + string.Join("; ", notes) + ".";
        return result;
    }

    /// <summary>
    /// Offline band-limited resampler: windowed sinc (Blackman, 32 zero crossings per side), with the cutoff
    /// lowered to the target Nyquist when downsampling. Quality is ample for one-shot drum samples.
    /// Not for the audio thread.
    /// </summary>
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate));
        if (fromRate == toRate || input.IsEmpty) return input.ToArray();

        const int halfTaps = 32;
        double ratio = (double)toRate / fromRate;
        double cutoff = Math.Min(1.0, ratio) * 0.97;        // fraction of the input Nyquist
        double support = halfTaps / cutoff;                  // half-width in input samples
        long outLen = (long)Math.Ceiling(input.Length * ratio);
        var output = new float[outLen];

        for (long o = 0; o < outLen; o++)
        {
            double t = o / ratio;                            // position in input samples
            int lo = (int)Math.Ceiling(t - support);
            int hi = (int)Math.Floor(t + support);
            if (lo < 0) lo = 0;
            if (hi > input.Length - 1) hi = input.Length - 1;
            double acc = 0;
            for (int k = lo; k <= hi; k++)
            {
                double x = k - t;
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / support) + 0.08 * Math.Cos(2 * Math.PI * x / support);
                double arg = Math.PI * cutoff * x;
                double sinc = Math.Abs(arg) < 1e-12 ? 1.0 : Math.Sin(arg) / arg;
                acc += input[k] * cutoff * sinc * w;
            }
            output[o] = (float)acc;
        }
        return output;
    }

    public static void WriteMono(string path, ReadOnlySpan<float> samples, int sampleRate, WavSampleFormat format = WavSampleFormat.Float32)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        WriteMono(fs, samples, sampleRate, format);
    }

    public static void WriteMono(Stream stream, ReadOnlySpan<float> samples, int sampleRate, WavSampleFormat format = WavSampleFormat.Float32)
    {
        int bits = format switch { WavSampleFormat.Pcm16 => 16, WavSampleFormat.Pcm24 => 24, _ => 32 };
        int bps = bits / 8;
        long dataBytes = (long)samples.Length * bps;
        if (dataBytes > uint.MaxValue - 44) throw new NotSupportedException("Arquivo grande demais para WAV.");

        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + dataBytes + (dataBytes & 1)));
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16u);
        bw.Write(format == WavSampleFormat.Float32 ? FormatFloat : FormatPcm);
        bw.Write((ushort)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * bps);
        bw.Write((ushort)bps);
        bw.Write((ushort)bits);
        bw.Write("data"u8);
        bw.Write((uint)dataBytes);

        var buf = new byte[Math.Min(samples.Length, 8192) * bps + 4];
        int pos = 0;
        foreach (float s in samples)
        {
            var dst = buf.AsSpan(pos);
            switch (format)
            {
                case WavSampleFormat.Float32:
                    BinaryPrimitives.WriteSingleLittleEndian(dst, s);
                    break;
                case WavSampleFormat.Pcm16:
                    BinaryPrimitives.WriteInt16LittleEndian(dst, (short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f));
                    break;
                default:
                    int v = (int)Math.Round(Math.Clamp(s, -1f, 1f) * 8388607f);
                    dst[0] = (byte)v; dst[1] = (byte)(v >> 8); dst[2] = (byte)(v >> 16);
                    break;
            }
            pos += bps;
            if (pos + bps > buf.Length - 4) { bw.Write(buf, 0, pos); pos = 0; }
        }
        if (pos > 0) bw.Write(buf, 0, pos);
        if ((dataBytes & 1) != 0) bw.Write((byte)0);
    }

    private static string ReadTag(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        if (b.Length < 4) throw new EndOfStreamException("WAV truncado.");
        return Encoding.ASCII.GetString(b);
    }
}
