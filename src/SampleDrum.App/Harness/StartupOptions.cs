using SampleDrum.App.Audio;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Harness;

/// <summary>
/// Command-line startup options for the UI. <c>SampleDrum.App.exe --simulation</c> sets <see cref="SimulationMode"/>:
/// the main window should then open in the "Simulação" routing profile with the "Simulação (sem ASIO)" host and the
/// synthetic kit loaded (samples + per-piece detection), without touching any ASIO driver.
/// The helpers below give MainForm everything it needs for that.
/// </summary>
public static class StartupOptions
{
    /// <summary>True when started with <c>--simulation</c>.</summary>
    public static bool SimulationMode { get; set; }

    /// <summary>Seed of the synthetic kit used by <c>--simulation</c>.</summary>
    public static int SimulationSeed { get; set; } = 1;

    /// <summary>Routing profile to select in simulation mode.</summary>
    public static RoutingProfile SimulationProfile => RoutingProfiles.Simulation;

    /// <summary>Host options for simulation mode: synthetic kit, looping, 256-sample blocks, preview off.</summary>
    public static FakeHostOptions SimulationHostOptions => new()
    {
        Source = SimulationSource.SyntheticKit, Seed = SimulationSeed, Loop = true, BlockSize = 256,
    };

    /// <summary>The synthetic scenario (mics + samples + recommended thresholds).</summary>
    public static KitScenario SimulationScenario() => KitScenario.Generate(SimulationSeed);

    /// <summary>Synthetic samples as a prepared kit (apply with <see cref="KitEngine.ApplyKit"/>).</summary>
    public static PreparedKit SimulationKit() => KitHarness.KitFor(SimulationScenario());

    /// <summary>Per-piece detection matching the synthetic scenario (toms with higher thresholds because of bleed).</summary>
    public static KitSettings SimulationKitSettings() => KitHarness.SettingsFor(SimulationScenario());

    // ---------- simulation sample library (temp folder, never the operator's folders) ----------

    private const string TempPrefix = "SampleDrum-simulacao-";
    private static readonly object LibraryGate = new();
    private static SimulationSampleLibrary? _library;
    private static bool _exitHooked;

    /// <summary>Folder for this process's simulation library: %TEMP%\SampleDrum-simulacao-&lt;pid&gt;.</summary>
    public static string SimulationLibraryRoot { get; set; } =
        Path.Combine(Path.GetTempPath(), TempPrefix + Environment.ProcessId);

    /// <summary>The simulation library if it was created in this run, else null.</summary>
    public static SimulationSampleLibrary? SimulationLibrary { get { lock (LibraryGate) return _library; } }

    /// <summary>
    /// Creates (once per run) the demo samples + presets under <see cref="SimulationLibraryRoot"/> and returns them.
    /// In <c>--simulation</c> MainForm must use <see cref="SimulationSampleLibrary.SamplesFolder"/> for the
    /// KitController's SampleLibrary and <see cref="SimulationSampleLibrary.PresetsFolder"/> for presets, and must not
    /// store either folder (or kits/presets chosen from them) in the operator's AppConfig. Also removes leftovers of
    /// earlier simulation runs whose process is gone.
    /// </summary>
    public static SimulationSampleLibrary EnsureSimulationLibrary()
    {
        lock (LibraryGate)
        {
            if (_library is not null) return _library;
            CleanupStaleLibraries();
            _library = SimulationSampleLibrary.Create(SimulationLibraryRoot);
            if (!_exitHooked)
            {
                // Removed however the run ends normally (window closed, Application.Exit, Environment.Exit).
                AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupSimulationLibrary();
                _exitHooked = true;
            }
            return _library;
        }
    }

    /// <summary>Deletes this run's simulation library (call on exit). Safe to call more than once.</summary>
    public static void CleanupSimulationLibrary()
    {
        lock (LibraryGate)
        {
            if (_library is null && !Directory.Exists(SimulationLibraryRoot)) return;
            TryDelete(SimulationLibraryRoot);
            _library = null;
        }
    }

    /// <summary>Removes %TEMP%\SampleDrum-simulacao-&lt;pid&gt; folders of processes that no longer run (crash leftovers).</summary>
    public static int CleanupStaleLibraries()
    {
        int removed = 0;
        string temp = Path.GetTempPath();
        if (!Directory.Exists(temp)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(temp, TempPrefix + "*"))
        {
            if (!int.TryParse(Path.GetFileName(dir)[TempPrefix.Length..], out int pid) || pid == Environment.ProcessId) continue;
            bool alive;
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); alive = !p.HasExited; }
            catch (ArgumentException) { alive = false; }
            catch (InvalidOperationException) { alive = false; }
            if (!alive && TryDelete(dir)) removed++;
        }
        return removed;
    }

    private static bool TryDelete(string dir)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
        return false;
    }
}
