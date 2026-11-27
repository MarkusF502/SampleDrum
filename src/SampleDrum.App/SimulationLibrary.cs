using SampleDrum.Core;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App;

/// <summary>
/// Demo sample library for <c>--simulation</c>: the synthetic kit's samples plus a few variants written as WAVs, and
/// two demo presets, in a per-process temp folder. The phone panel and the PC operate on it during a simulation run, so
/// the operator's samples folder, presets and configuration are never touched. Deleted when the app closes.
/// </summary>
public sealed class SimulationLibrary : IDisposable
{
    private SimulationLibrary(string root)
    {
        Root = root;
        SamplesFolder = Path.Combine(root, "Samples");
        PresetsFolder = Path.Combine(root, "Presets");
    }

    public string Root { get; }
    public string SamplesFolder { get; }
    public string PresetsFolder { get; }

    /// <summary>Synthetic kit pieces with SourcePath pointing at the written WAVs (so the panel shows ids and names).</summary>
    public PreparedKit Kit { get; private set; } = PreparedKit.Empty(48000);

    /// <summary>Writes the library for <paramref name="kit"/> (synthetic kit at the engine rate).</summary>
    public static SimulationLibrary Create(PreparedKit kit)
    {
        string root = Path.Combine(Path.GetTempPath(), $"SampleDrum-simulacao-{Environment.ProcessId}");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var lib = new SimulationLibrary(root);
        Directory.CreateDirectory(lib.SamplesFolder);
        Directory.CreateDirectory(lib.PresetsFolder);
        int rate = kit.SampleRate;
        var pieces = new List<PreparedPiece>();
        var demo = new Dictionary<DrumPiece, string>();
        var shortOnes = new Dictionary<DrumPiece, string>();
        foreach (var p in DrumPieces.All)
        {
            string slug = SyntheticKit.PieceSlugs[(int)p];
            var samples = kit[p].Samples ?? new float[rate / 10];
            string dir = Path.Combine(lib.SamplesFolder, slug);
            Directory.CreateDirectory(dir);
            string main = $"{slug}/{slug}-sintetico.wav";
            string shortId = $"{slug}/{slug}-curto.wav";
            string soft = $"{slug}/{slug}-suave.wav";
            WavFile.WriteMono(Path.Combine(lib.SamplesFolder, main), samples, rate);
            WavFile.WriteMono(Path.Combine(lib.SamplesFolder, shortId), Shorten(samples, 0.4), rate);
            WavFile.WriteMono(Path.Combine(lib.SamplesFolder, soft), samples.Select(x => x * 0.5f).ToArray(), rate);
            demo[p] = main;
            shortOnes[p] = shortId;
            pieces.Add(new PreparedPiece(p, samples, Path.Combine(lib.SamplesFolder, main.Replace('/', Path.DirectorySeparatorChar)),
                $"{slug}-sintetico"));
        }
        Save(lib, "simulacao-padrao", "Simulação padrão", demo);
        Save(lib, "simulacao-curta", "Simulação curta", shortOnes);
        lib.Kit = new PreparedKit("Simulação padrão", rate, pieces, "simulacao-padrao");
        return lib;
    }

    private static void Save(SimulationLibrary lib, string id, string name, Dictionary<DrumPiece, string> samples) =>
        new KitPreset { Name = name, Pieces = samples.ToDictionary(kv => kv.Key, kv => new PiecePreset(kv.Value)) }
            .Save(Path.Combine(lib.PresetsFolder, id + ".json"));

    /// <summary>First <paramref name="fraction"/> of the sample with a short linear fade at the end.</summary>
    private static float[] Shorten(float[] s, double fraction)
    {
        int n = Math.Max(1, (int)(s.Length * fraction));
        var r = s.AsSpan(0, n).ToArray();
        int fade = Math.Min(n, 240);
        for (int i = 0; i < fade; i++) r[n - 1 - i] *= i / (float)fade;
        return r;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
