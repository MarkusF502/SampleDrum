using System.Runtime.InteropServices;
using NAudio.Wave.Asio;

namespace SampleDrum.App.Audio;

/// <summary>
/// Allocation-free conversion between the driver's native ASIO sample format and normalized float.
/// Implemented here (not via NAudio's converters) so the audio callback path is fully under our control.
/// Supported: Int32LSB, Float32LSB (the expected X-USB formats), plus Int16LSB and Int24LSB.
/// </summary>
public static class NativeSampleConverter
{
    public static bool IsSupported(AsioSampleType type) => type is
        AsioSampleType.Int32LSB or AsioSampleType.Float32LSB or AsioSampleType.Int16LSB or AsioSampleType.Int24LSB;

    public static int BytesPerSample(AsioSampleType type) => type switch
    {
        AsioSampleType.Int32LSB or AsioSampleType.Float32LSB => 4,
        AsioSampleType.Int24LSB => 3,
        AsioSampleType.Int16LSB => 2,
        _ => throw new NotSupportedException(type.ToString()),
    };

    /// <summary>Native bytes -> float (-1..1). <paramref name="dest"/>.Length frames are converted.</summary>
    public static void ToFloat(ReadOnlySpan<byte> src, AsioSampleType type, Span<float> dest)
    {
        int n = dest.Length;
        switch (type)
        {
            case AsioSampleType.Int32LSB:
            {
                var s = MemoryMarshal.Cast<byte, int>(src);
                const float scale = 1f / 2147483648f;
                for (int i = 0; i < n; i++) dest[i] = s[i] * scale;
                break;
            }
            case AsioSampleType.Float32LSB:
                MemoryMarshal.Cast<byte, float>(src).Slice(0, n).CopyTo(dest);
                break;
            case AsioSampleType.Int16LSB:
            {
                var s = MemoryMarshal.Cast<byte, short>(src);
                const float scale = 1f / 32768f;
                for (int i = 0; i < n; i++) dest[i] = s[i] * scale;
                break;
            }
            case AsioSampleType.Int24LSB:
            {
                const float scale = 1f / 8388608f;
                for (int i = 0, b = 0; i < n; i++, b += 3)
                {
                    int v = src[b] | (src[b + 1] << 8) | ((sbyte)src[b + 2] << 16);
                    dest[i] = v * scale;
                }
                break;
            }
            default:
                // Validated at start; never reached from the audio callback.
                dest.Clear();
                break;
        }
    }

    /// <summary>Float -> native bytes, clamping to full scale. <paramref name="src"/>.Length frames are written.</summary>
    public static void FromFloat(ReadOnlySpan<float> src, AsioSampleType type, Span<byte> dest)
    {
        int n = src.Length;
        switch (type)
        {
            case AsioSampleType.Int32LSB:
            {
                var d = MemoryMarshal.Cast<byte, int>(dest);
                for (int i = 0; i < n; i++)
                {
                    double v = src[i] * 2147483648.0;
                    d[i] = v >= 2147483647.0 ? int.MaxValue : v <= -2147483648.0 ? int.MinValue : (int)v;
                }
                break;
            }
            case AsioSampleType.Float32LSB:
            {
                var d = MemoryMarshal.Cast<byte, float>(dest);
                for (int i = 0; i < n; i++)
                {
                    float v = src[i];
                    d[i] = v > 1f ? 1f : v < -1f ? -1f : v;
                }
                break;
            }
            case AsioSampleType.Int16LSB:
            {
                var d = MemoryMarshal.Cast<byte, short>(dest);
                for (int i = 0; i < n; i++)
                {
                    float v = src[i] * 32768f;
                    d[i] = v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)v;
                }
                break;
            }
            case AsioSampleType.Int24LSB:
            {
                for (int i = 0, b = 0; i < n; i++, b += 3)
                {
                    float f = src[i] * 8388608f;
                    int v = f >= 8388607f ? 8388607 : f <= -8388608f ? -8388608 : (int)f;
                    dest[b] = (byte)v;
                    dest[b + 1] = (byte)(v >> 8);
                    dest[b + 2] = (byte)(v >> 16);
                }
                break;
            }
            default:
                dest.Clear();
                break;
        }
    }
}
