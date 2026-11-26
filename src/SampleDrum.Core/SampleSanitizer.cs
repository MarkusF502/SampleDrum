namespace SampleDrum.Core;

/// <summary>Replaces NaN/±Inf samples (e.g. from a corrupt float WAV) by 0 so they never reach the driver.</summary>
public static class SampleSanitizer
{
    /// <summary>Replaces non-finite values in place by 0 and returns how many were replaced.</summary>
    public static int ReplaceNonFinite(Span<float> samples)
    {
        int n = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i])) { samples[i] = 0f; n++; }
        }
        return n;
    }
}
