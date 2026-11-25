namespace SampleDrum.Core;

/// <summary>dB / linear conversions (amplitude).</summary>
public static class Db
{
    /// <summary>dB to linear amplitude.</summary>
    public static float ToLinear(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Linear amplitude to dB; -infinity for 0.</summary>
    public static float FromLinear(float lin) => lin <= 0f ? float.NegativeInfinity : 20f * MathF.Log10(lin);
}
