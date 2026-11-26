using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Simulation;

/// <summary>What an expectation is testing.</summary>
public enum ExpectationKind
{
    /// <summary>A real hit on this piece's mic: must trigger this piece.</summary>
    Hit,
    /// <summary>Bleed from another piece into this mic: must NOT trigger this piece.</summary>
    Bleed,
    /// <summary>Second stroke inside the minimum interval (lockout): must NOT trigger.</summary>
    DoubleInsideLockout,
}

/// <summary>A hit written into a mic track.</summary>
/// <param name="Piece">Piece index (DrumPiece order).</param>
/// <param name="StartSample">First sample of the hit (the expected onset).</param>
/// <param name="LevelDb">Peak level of the hit (dBFS).</param>
/// <param name="SustainMs">Decay time constant of the body (ms).</param>
/// <param name="Label">pt-BR description.</param>
public sealed record ScenarioHit(int Piece, long StartSample, float LevelDb, float SustainMs, string Label);

/// <summary>One expectation of the scenario.</summary>
/// <param name="Piece">Piece whose detector is checked (= mic track index).</param>
/// <param name="SampleIndex">Expected onset (for Bleed: the bleed arrival in this mic).</param>
/// <param name="ExpectTrigger">True = must trigger near <paramref name="SampleIndex"/>; false = must not.</param>
/// <param name="Kind">What the expectation tests.</param>
/// <param name="LevelDb">Level of the hit (or of the bleed) in this mic, dBFS.</param>
/// <param name="Label">pt-BR description.</param>
public sealed record ExpectedEvent(int Piece, long SampleIndex, bool ExpectTrigger, ExpectationKind Kind,
    float LevelDb, string Label);

/// <summary>Bleed path: a copy of <paramref name="FromPiece"/>'s hits leaks into mic <paramref name="ToMic"/>.</summary>
/// <param name="FromPiece">Source piece.</param>
/// <param name="ToMic">Receiving mic (piece index).</param>
/// <param name="GainDb">Bleed level relative to the source hit (dB).</param>
/// <param name="DelaySamples">Arrival delay in the receiving mic.</param>
public sealed record BleedPath(int FromPiece, int ToMic, float GainDb, int DelaySamples);

/// <summary>
/// Deterministic 4-mic synthetic kit scenario at 48 kHz (seeded): isolated hard/soft hits per piece, hits that cross
/// block boundaries (multiples of 3840 samples = common boundary of 32/64/128/256/480-sample blocks), simultaneous hits
/// (2, 3 and 4 pieces on the same sample), doubles per piece outside and inside the minimum interval, and controlled
/// bleed (kick -> floor tom mic, snare -> tom mic) that must not trigger. Cross-platform, no I/O.
/// </summary>
public sealed class KitScenario
{
    public const int DefaultSampleRate = 48000;

    /// <summary>Common block boundary of 32/64/128/256/480-sample blocks (LCM = 3840 samples = 80 ms @ 48 kHz).</summary>
    public const int CommonBoundary = 3840;

    public int SampleRate { get; }
    public int Seed { get; }
    /// <summary>Mic tracks, index = piece.</summary>
    public float[][] Mics { get; }
    /// <summary>Synthetic one-shot samples, index = piece.</summary>
    public float[][] Samples { get; }
    public IReadOnlyList<ScenarioHit> Hits { get; }
    public IReadOnlyList<ExpectedEvent> Expected { get; }
    public IReadOnlyList<BleedPath> Bleed { get; }

    /// <summary>
    /// Per-piece detection thresholds used by the harness (dBFS): the Core per-piece defaults
    /// (<see cref="KitDefaults"/>: Bumbo -24, Caixa -20, Tom -20) except the Surdo, raised from -22 to -16 dBFS:
    /// a hard kick (-3 dBFS) leaking at -18 dB reaches about -21 dBFS in the floor-tom mic, above the -22 default, and
    /// would trigger it. With -16 the worst bleed stays 5 dB below while the floor tom's soft hits (-6) stay 10 dB above.
    /// This is a calibration note for the rehearsal, not a rule. User mics (<see cref="FromMics"/>) use the plain defaults.
    /// </summary>
    public IReadOnlyList<float> ThresholdDb { get; }

    /// <summary>Length in samples.</summary>
    public int Length => Mics[0].Length;

    private KitScenario(int sampleRate, int seed, float[][] mics, float[][] samples, List<ScenarioHit> hits,
        List<ExpectedEvent> expected, List<BleedPath> bleed, float[] thresholds)
    {
        SampleRate = sampleRate; Seed = seed; Mics = mics; Samples = samples; Hits = hits; Expected = expected;
        Bleed = bleed; ThresholdDb = thresholds;
    }

    /// <summary>Detection settings for <paramref name="piece"/>: the Core per-piece defaults
    /// (<see cref="KitDefaults.TriggerFor"/>) with this scenario's threshold (re-arm moves with it, keeping the hysteresis).</summary>
    public TriggerSettings RecommendedSettings(int piece)
        => KitDefaults.TriggerFor((DrumPiece)piece).WithThreshold(ThresholdDb[piece]).Normalized();

    /// <summary>Surdo threshold used with the synthetic scenario (see <see cref="ThresholdDb"/>).</summary>
    public const float SyntheticFloorTomThresholdDb = -16f;

    private static float[] DefaultThresholds() =>
        DrumPieces.All.Select(p => KitDefaults.TriggerFor(p).ThresholdDb).ToArray();

    /// <summary>Synthetic samples only (e.g. to load into the engine when the mics come from user WAVs).</summary>
    public static float[][] SyntheticSamples(int sampleRate = DefaultSampleRate)
        => Enumerable.Range(0, SyntheticKit.PieceCount).Select(p => SyntheticKit.Sample(p, sampleRate)).ToArray();

    public static KitScenario Generate(int seed = 1, int sampleRate = DefaultSampleRate)
    {
        int fs = sampleRate;
        long S(double sec) => (long)Math.Round(sec * fs);
        long Boundary(double sec) => (long)Math.Round(sec * fs / CommonBoundary) * CommonBoundary;

        var hits = new List<ScenarioHit>();
        var expected = new List<ExpectedEvent>();
        float[] hard = [-3f, -3f, -1f, -2f];
        float[] soft = [-12f, -12f, -5f, -6f];
        float[] sustain = [120f, 60f, 150f, 200f];
        float[] shortSustain = [15f, 15f, 20f, 25f];
        float[] mid = [-6f, -6f, -4f, -5f];

        void Hit(int p, long at, float db, float sus, string label, bool expectTrigger = true,
            ExpectationKind kind = ExpectationKind.Hit)
        {
            hits.Add(new ScenarioHit(p, at, db, sus, label));
            expected.Add(new ExpectedEvent(p, at, expectTrigger, kind, db, label));
        }

        for (int p = 0; p < 4; p++)
        {
            Hit(p, S(0.5 + 0.5 * p), hard[p], sustain[p], "isolada forte");
            Hit(p, S(2.5 + 0.5 * p), soft[p], sustain[p] * 0.7f, "isolada suave");
        }
        int[] crossOffset = [3, 1, 7, 2];
        for (int p = 0; p < 4; p++)
            Hit(p, Boundary(4.8 + 0.4 * p) - crossOffset[p], mid[p], sustain[p] * 0.6f,
                $"atravessa fronteira de bloco (-{crossOffset[p]} amostras)");

        for (int n = 2; n <= 4; n++)
        {
            long at = S(6.6 + 0.6 * (n - 2));
            for (int p = 0; p < n; p++) Hit(p, at, mid[p], sustain[p] * 0.5f, $"simultânea ({n} peças)");
        }
        for (int p = 0; p < 4; p++)
        {
            long at = S(8.5 + 0.7 * p);
            Hit(p, at, mid[p], shortSustain[p], "dupla fora do intervalo (1ª)");
            Hit(p, at + S(0.120), mid[p], shortSustain[p], "dupla fora do intervalo (2ª, +120 ms)");
        }
        for (int p = 0; p < 4; p++)
        {
            long at = S(11.3 + 0.7 * p);
            Hit(p, at, mid[p], shortSustain[p], "dupla dentro do intervalo (1ª)");
            Hit(p, at + S(0.020), mid[p], shortSustain[p], "dupla dentro do intervalo (2ª, +20 ms: não deve disparar)",
                expectTrigger: false, kind: ExpectationKind.DoubleInsideLockout);
        }

        int length = (int)S(14.5);
        var mics = new float[4][];
        for (int p = 0; p < 4; p++) mics[p] = SyntheticSignals.Noise(length, -60f, seed * 10 + p);

        // Direct sound, keeping each hit's waveform so bleed is an exact delayed/attenuated copy.
        var waves = new float[hits.Count][];
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            waves[i] = SyntheticKit.MicHit(h.Piece, fs, EngineParams.DbToLin(h.LevelDb), h.SustainMs, seed * 1000 + i);
            SyntheticSignals.Add(mics[h.Piece], waves[i], h.StartSample);
        }

        var bleed = new List<BleedPath>
        {
            new(FromPiece: 0, ToMic: 3, GainDb: -18f, DelaySamples: (int)Math.Round(0.0010 * fs)), // bumbo -> mic do surdo
            new(FromPiece: 1, ToMic: 2, GainDb: -15f, DelaySamples: (int)Math.Round(0.0005 * fs)), // caixa -> mic do tom
        };
        foreach (var b in bleed)
        {
            float g = EngineParams.DbToLin(b.GainDb);
            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                if (h.Piece != b.FromPiece) continue;
                var w = waves[i];
                var scaled = new float[w.Length];
                for (int k = 0; k < w.Length; k++) scaled[k] = w[k] * g;
                long arrival = h.StartSample + b.DelaySamples;
                SyntheticSignals.Add(mics[b.ToMic], scaled, arrival);
                // Expect no trigger, unless the receiving piece is itself hit around that time.
                bool ownHitNear = hits.Any(o => o.Piece == b.ToMic && Math.Abs(o.StartSample - arrival) < S(0.060));
                if (!ownHitNear)
                    expected.Add(new ExpectedEvent(b.ToMic, arrival, false, ExpectationKind.Bleed, h.LevelDb + b.GainDb,
                        $"vazamento de {SyntheticKit.PieceNames[b.FromPiece]} ({b.GainDb:0} dB): não deve disparar"));
            }
        }

        expected.Sort((a, b) => a.SampleIndex != b.SampleIndex ? a.SampleIndex.CompareTo(b.SampleIndex) : a.Piece.CompareTo(b.Piece));
        return new KitScenario(fs, seed, mics, SyntheticSamples(fs), hits, expected, bleed, SyntheticThresholds());
    }

    private static float[] SyntheticThresholds()
    {
        var t = DefaultThresholds();
        t[(int)DrumPiece.FloorTom] = SyntheticFloorTomThresholdDb;
        return t;
    }

    /// <summary>Wraps user-provided mic tracks (no expectations; the report then only lists detections).</summary>
    public static KitScenario FromMics(float[][] mics, int sampleRate = DefaultSampleRate)
    {
        if (mics.Length != SyntheticKit.PieceCount) throw new ArgumentException("São necessários 4 microfones.", nameof(mics));
        int len = mics.Max(m => m.Length);
        var padded = mics.Select(m => m.Length == len ? m : m.Concat(new float[len - m.Length]).ToArray()).ToArray();
        return new KitScenario(sampleRate, 0, padded, SyntheticSamples(sampleRate), [], [], [], DefaultThresholds());
    }
}
