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
}
