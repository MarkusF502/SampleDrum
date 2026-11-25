using System.Buffers.Binary;
using SampleDrum.Core;

namespace SampleDrum.Core.Tests;

public class WavFileTests
{
    private static float[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (float)Math.Sin(i * 0.05) * 0.8f).ToArray();

    [Theory]
    [InlineData(WavSampleFormat.Float32, 1e-7)]
    [InlineData(WavSampleFormat.Pcm24, 2e-7)]
    [InlineData(WavSampleFormat.Pcm16, 6e-5)]
    public void RoundTrip(WavSampleFormat format, double tol)
    {
        var x = Ramp(1001); // odd length exercises the pad byte for PCM24
        using var ms = new MemoryStream();
        WavFile.WriteMono(ms, x, 48000, format);
        ms.Position = 0;
        var w = WavFile.Read(ms);
        Assert.Equal(48000, w.SampleRate);
        Assert.Equal(1, w.Channels);
        Assert.Equal(x.Length, w.Mono.Length);
        for (int i = 0; i < x.Length; i++) Assert.True(Math.Abs(x[i] - w.Mono[i]) <= tol, $"i={i}");
    }

    [Fact]
    public void Stereo16_DownmixedToMono_WithExtraChunk()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            short[] frames = { 16384, -16384, 8192, 8192 }; // L,R,L,R
            bw.Write("RIFF"u8); bw.Write(0u); bw.Write("WAVE"u8);
            bw.Write("LIST"u8); bw.Write(3u); bw.Write(new byte[] { 1, 2, 3, 0 }); // odd chunk + pad
            bw.Write("fmt "u8); bw.Write(16u); bw.Write((ushort)1); bw.Write((ushort)2); bw.Write(44100); bw.Write(44100 * 4); bw.Write((ushort)4); bw.Write((ushort)16);
            bw.Write("data"u8); bw.Write((uint)(frames.Length * 2));
            foreach (var f in frames) bw.Write(f);
        }
        ms.Position = 0;
        var w = WavFile.Read(ms);
        Assert.Equal(2, w.Channels);
        Assert.Equal(44100, w.SampleRate);
        Assert.Equal(new[] { 0f, 0.25f }, w.Mono);
    }

    [Fact]
    public void Pcm24_NegativeValuesSignExtended()
    {
        using var ms = new MemoryStream();
        WavFile.WriteMono(ms, new[] { -1f, -0.5f, 0.5f }, 48000, WavSampleFormat.Pcm24);
        ms.Position = 0;
        var w = WavFile.Read(ms);
        Assert.Equal(-1f, w.Mono[0], 5);
        Assert.Equal(-0.5f, w.Mono[1], 5);
        Assert.Equal(0.5f, w.Mono[2], 5);
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(96000, 48000)]
    public void Resample_PreservesLengthAndFrequency(int from, int to)
    {
        double f = 100;
        var x = Enumerable.Range(0, from).Select(i => (float)Math.Sin(2 * Math.PI * f * i / from) * 0.5f).ToArray();
        var y = WavFile.Resample(x, from, to);
        Assert.InRange(y.Length, to - 1, to + 1);
        // Compare against the ideal sine in the middle (away from edges).
        for (int i = to / 4; i < 3 * to / 4; i += 37)
            Assert.True(Math.Abs(y[i] - 0.5 * Math.Sin(2 * Math.PI * f * i / to)) < 2e-3, $"i={i}");
    }

    [Fact]
    public void ReadMonoAtRate_ReportsConversionNote()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sd-test-{Guid.NewGuid():N}.wav");
        try
        {
            WavFile.WriteMono(path, Ramp(4410), 44100, WavSampleFormat.Pcm16);
            var y = WavFile.ReadMonoAtRate(path, 48000, out var note);
            Assert.Equal(4800, y.Length);
            Assert.NotNull(note);
            Assert.Contains("44100", note);

            WavFile.WriteMono(path, Ramp(480), 48000, WavSampleFormat.Float32);
            WavFile.ReadMonoAtRate(path, 48000, out note);
            Assert.Null(note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Header_IsStandard44Bytes()
    {
        using var ms = new MemoryStream();
        WavFile.WriteMono(ms, new float[10], 48000, WavSampleFormat.Float32);
        var b = ms.ToArray();
        Assert.Equal(44 + 40, b.Length);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20))); // IEEE float
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24)));
    }
}
