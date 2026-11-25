namespace SampleDrum.Core;

/// <summary>One expected onset paired (or not) with a detected one.</summary>
/// <param name="ExpectedIndex">Index in the expected list, or -1 for a false positive.</param>
/// <param name="ExpectedSample">Expected onset sample, or -1.</param>
/// <param name="DetectedIndex">Index in the detected list, or -1 for a miss.</param>
/// <param name="DetectedSample">Detected onset sample, or -1.</param>
public readonly record struct OnsetMatch(int ExpectedIndex, long ExpectedSample, int DetectedIndex, long DetectedSample)
{
    public bool IsMatch => ExpectedIndex >= 0 && DetectedIndex >= 0;
    public bool IsMiss => DetectedIndex < 0;
    public bool IsFalsePositive => ExpectedIndex < 0;
    /// <summary>Detected - expected, in samples (0 when not a match).</summary>
    public long ErrorSamples => IsMatch ? DetectedSample - ExpectedSample : 0;
}

/// <summary>Greedy in-order matching of expected vs detected onset times within a tolerance.</summary>
public static class OnsetMatcher
{
    /// <summary>
    /// Both lists must be sorted ascending. Each expected onset is paired with the earliest unused detected
    /// onset within ±<paramref name="toleranceSamples"/>; leftovers are misses / false positives.
    /// </summary>
    public static List<OnsetMatch> Match(IReadOnlyList<long> expected, IReadOnlyList<long> detected, long toleranceSamples)
    {
        var result = new List<OnsetMatch>();
        var used = new bool[detected.Count];
        int searchFrom = 0;
        for (int e = 0; e < expected.Count; e++)
        {
            int found = -1;
            for (int d = searchFrom; d < detected.Count; d++)
            {
                if (used[d]) continue;
                long diff = detected[d] - expected[e];
                if (diff < -toleranceSamples) continue;
                if (diff > toleranceSamples) break;
                found = d;
                break;
            }
            if (found >= 0)
            {
                used[found] = true;
                result.Add(new OnsetMatch(e, expected[e], found, detected[found]));
                searchFrom = found + 1;
            }
            else
            {
                result.Add(new OnsetMatch(e, expected[e], -1, -1));
            }
        }
        for (int d = 0; d < detected.Count; d++)
            if (!used[d]) result.Add(new OnsetMatch(-1, -1, d, detected[d]));
        return result;
    }
}
