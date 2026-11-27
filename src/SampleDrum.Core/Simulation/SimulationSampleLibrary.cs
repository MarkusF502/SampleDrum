using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Simulation;

/// <summary>A generated demo sample of the simulation library.</summary>
/// <param name="Id">Library id (path relative to the samples folder, with '/').</param>
/// <param name="Piece">Piece it is meant for.</param>
/// <param name="Description">pt-BR description (format, character).</param>
public sealed record DemoSample(string Id, DrumPiece Piece, string Description);

/// <summary>
/// Demo sample library for <c>--simulation</c> and the harness: generated WAVs (3 recognizable variants per piece, in
/// several formats so loading/resampling/down-mix are exercised) and demo presets (KitPreset JSON), written under a
/// root folder the caller owns (a temp folder, never the operator's samples/presets folders).
/// Deterministic: same content on every run.
/// </summary>
public sealed class SimulationSampleLibrary
{
    /// <summary>Preset id of the demo preset that references a missing WAV (to show a failed preset).</summary>
    public const string BrokenPresetId = "demo-com-erro";
    /// <summary>Sample id referenced by <see cref="BrokenPresetId"/> that does not exist.</summary>
    public const string MissingSampleId = "caixas/caixa-que-nao-existe.wav";

    public string Root { get; }
    public string SamplesFolder { get; }
    public string PresetsFolder { get; }
    public IReadOnlyList<DemoSample> Samples { get; }
    /// <summary>Demo presets, with <see cref="KitPreset.Id"/> = file name without .json.</summary>
    public IReadOnlyList<KitPreset> Presets { get; }

    private SimulationSampleLibrary(string root, IReadOnlyList<DemoSample> samples, IReadOnlyList<KitPreset> presets)
    {
        Root = root;
        SamplesFolder = Path.Combine(root, "samples");
        PresetsFolder = Path.Combine(root, "presets");
        Samples = samples;
        Presets = presets;
    }

    public KitPreset Preset(string id) => Presets.First(p => p.Id == id);

    /// <summary>Writes the library under <paramref name="root"/> (created if needed; existing files overwritten).</summary>
    public static SimulationSampleLibrary Create(string root, int sampleRate = KitScenario.DefaultSampleRate)
    {
        root = Path.GetFullPath(root);
        string samplesDir = Path.Combine(root, "samples");
        string presetsDir = Path.Combine(root, "presets");
        Directory.CreateDirectory(samplesDir);
        Directory.CreateDirectory(presetsDir);
        float peak = EngineParams.DbToLin(-1f);
        var samples = new List<DemoSample>();

        void Mono(string id, DrumPiece p, float[] data, int rate, WavSampleFormat fmt, string desc)
        {
            string path = Path.Combine(samplesDir, id.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WavFile.WriteMono(path, Finish(data, rate), rate, fmt);
            samples.Add(new DemoSample(id, p, desc));
        }

        // Bumbo (kick)
        Mono("bumbos/bumbo-seco.wav", DrumPiece.Kick,
            SyntheticSignals.Kick(sampleRate, peak, 60f, 21, 140f, 50f, 0.5f), sampleRate, WavSampleFormat.Pcm24, "seco e curto (PCM 24 bits)");
        Mono("bumbos/bumbo-grave.wav", DrumPiece.Kick,
            SyntheticSignals.Kick(sampleRate, peak, 160f, 22, 90f, 42f, 0.2f), sampleRate, WavSampleFormat.Pcm24, "grave e longo (PCM 24 bits)");
        Mono("bumbos/bumbo-ataque.wav", DrumPiece.Kick,
            SyntheticSignals.Kick(sampleRate, peak, 90f, 23, 200f, 55f, 0.9f), sampleRate, WavSampleFormat.Float32, "muito ataque (float 32)");

        // Caixa (snare): one stereo file (down-mixed to mono on load)
        Mono("caixas/caixa-seca.wav", DrumPiece.Snare,
            SyntheticKit.Snare(sampleRate, peak, 45f, 31, toneHz: 240f, noiseMix: 0.55f), sampleRate, WavSampleFormat.Pcm24, "seca (PCM 24 bits)");
        Mono("caixas/caixa-aro.wav", DrumPiece.Snare,
            SyntheticKit.Snare(sampleRate, peak, 30f, 32, toneHz: 420f, noiseMix: 0.3f), sampleRate, WavSampleFormat.Pcm16, "aro, agudo (PCM 16 bits)");
        {
            string id = "caixas/caixa-aberta.wav";
            var l = Finish(SyntheticKit.Snare(sampleRate, peak, 140f, 33, toneHz: 200f, noiseMix: 0.75f), sampleRate);
            var r = Finish(SyntheticKit.Snare(sampleRate, peak, 140f, 34, toneHz: 200f, noiseMix: 0.75f), sampleRate);
            MultiChannelWav.Write(Path.Combine(samplesDir, "caixas", "caixa-aberta.wav"), [l, r], sampleRate);
            samples.Add(new DemoSample(id, DrumPiece.Snare, "aberta, com esteira longa (estéreo float 32, vira mono)"));
        }

        // Tom: one file at 44.1 kHz (resampled on load)
        Mono("tons/tom-agudo.wav", DrumPiece.Tom,
            SyntheticKit.Tonal(44100, peak, 120f, 41, 240f, 190f, 0.3f), 44100, WavSampleFormat.Pcm16, "agudo (44,1 kHz, reamostrado)");
        Mono("tons/tom-medio.wav", DrumPiece.Tom,
            SyntheticKit.Tonal(sampleRate, peak, 150f, 42, 190f, 140f, 0.35f), sampleRate, WavSampleFormat.Pcm24, "médio (PCM 24 bits)");
        Mono("tons/tom-curto.wav", DrumPiece.Tom,
            SyntheticKit.Tonal(sampleRate, peak, 60f, 43, 210f, 160f, 0.6f), sampleRate, WavSampleFormat.Pcm24, "curto (PCM 24 bits)");

        // Surdo (floor tom)
        Mono("surdos/surdo-grave.wav", DrumPiece.FloorTom,
            SyntheticKit.Tonal(sampleRate, peak, 230f, 51, 110f, 75f, 0.2f), sampleRate, WavSampleFormat.Pcm24, "grave (PCM 24 bits)");
        Mono("surdos/surdo-longo.wav", DrumPiece.FloorTom,
            SyntheticKit.Tonal(sampleRate, peak, 350f, 52, 130f, 90f, 0.25f), sampleRate, WavSampleFormat.Pcm24, "longo (PCM 24 bits)");
        Mono("surdos/surdo-seco.wav", DrumPiece.FloorTom,
            SyntheticKit.Tonal(sampleRate, peak, 110f, 53, 125f, 95f, 0.5f), sampleRate, WavSampleFormat.Float32, "seco (float 32)");

        static PiecePreset P(string id, float? gainDb = null) => new(id, gainDb);
        var presets = new List<KitPreset>
        {
            new()
            {
                Name = "Demo Rock", Id = "demo-rock",
                Pieces = new Dictionary<DrumPiece, PiecePreset>
                {
                    [DrumPiece.Kick] = P("bumbos/bumbo-ataque.wav"), [DrumPiece.Snare] = P("caixas/caixa-seca.wav"),
                    [DrumPiece.Tom] = P("tons/tom-medio.wav"), [DrumPiece.FloorTom] = P("surdos/surdo-seco.wav"),
                },
            },
            new()
            {
                Name = "Demo Suave", Id = "demo-suave",
                Pieces = new Dictionary<DrumPiece, PiecePreset>
                {
                    [DrumPiece.Kick] = P("bumbos/bumbo-grave.wav", -3f), [DrumPiece.Snare] = P("caixas/caixa-aberta.wav"),
                    [DrumPiece.Tom] = P("tons/tom-agudo.wav"), [DrumPiece.FloorTom] = P("surdos/surdo-longo.wav"),
                },
            },
            new()
            {
                Name = "Demo com erro (arquivo faltando)", Id = BrokenPresetId,
                Pieces = new Dictionary<DrumPiece, PiecePreset>
                {
                    [DrumPiece.Kick] = P("bumbos/bumbo-seco.wav"), [DrumPiece.Snare] = P(MissingSampleId),
                    [DrumPiece.Tom] = P("tons/tom-curto.wav"), [DrumPiece.FloorTom] = P("surdos/surdo-grave.wav"),
                },
            },
        };
        foreach (var preset in presets) preset.Save(Path.Combine(presetsDir, preset.Id + ".json"));
        return new SimulationSampleLibrary(root, samples, presets);
    }

    /// <summary>Trim to 0.8 s and fade the last 5 ms.</summary>
    private static float[] Finish(float[] data, int rate)
    {
        int max = (int)(0.8 * rate);
        var s = data.Length > max ? data[..max] : data;
        int fade = Math.Min(s.Length, (int)(0.005 * rate));
        for (int i = 0; i < fade; i++) s[s.Length - 1 - i] *= (float)i / fade;
        return s;
    }
}
