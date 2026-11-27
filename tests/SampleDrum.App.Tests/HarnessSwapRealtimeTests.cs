using SampleDrum.App.Harness;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Tests;

/// <summary>Swap verification through the running simulation host, and the --simulation demo library lifecycle.</summary>
public class HarnessSwapRealtimeTests
{
    [Fact]
    public async Task SwapsWhileFakeHostRuns_AllChecksPass()
    {
        string root = Path.Combine(Path.GetTempPath(), "sd-swap-rt-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var lib = SimulationSampleLibrary.Create(root);
            var o = new SwapOptions { BlockSize = 128 };
            var rec = await SwapCommand.RunRealtimeAsync(lib, o, speed: 8.0);
            var rep = SwapHarness.Analyze(rec, o);
            Assert.True(rep.Passed, string.Join("\n", rep.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));
            Assert.False(rec.HostInterrupted);
            Assert.Equal(rec.Callbacks, rec.BlockKits.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SimulationLibrary_LivesInTempFolderOfThisRun_AndIsRemoved()
    {
        string saved = StartupOptions.SimulationLibraryRoot;
        string root = Path.Combine(Path.GetTempPath(), "SampleDrum-simulacao-teste-" + Guid.NewGuid().ToString("N")[..8]);
        StartupOptions.SimulationLibraryRoot = root;
        try
        {
            var lib = StartupOptions.EnsureSimulationLibrary();
            Assert.Same(lib, StartupOptions.EnsureSimulationLibrary());          // once per run
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), lib.Root, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(lib.SamplesFolder, "bumbos", "bumbo-seco.wav")));
            Assert.True(File.Exists(Path.Combine(lib.PresetsFolder, "demo-rock.json")));
            // never the operator's folders
            var cfg = new SampleDrum.Core.Kit.AppConfig();
            Assert.NotEqual(Path.GetFullPath(cfg.EffectiveSamplesFolder), lib.SamplesFolder, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(Path.GetFullPath(cfg.EffectivePresetsFolder), lib.PresetsFolder, StringComparer.OrdinalIgnoreCase);

            StartupOptions.CleanupSimulationLibrary();
            Assert.False(Directory.Exists(root));
            Assert.Null(StartupOptions.SimulationLibrary);
            StartupOptions.CleanupSimulationLibrary();                          // idempotent
        }
        finally
        {
            StartupOptions.CleanupSimulationLibrary();
            StartupOptions.SimulationLibraryRoot = saved;
        }
    }

    [Fact]
    public void StaleLibrariesOfDeadProcessesAreRemoved()
    {
        // A pid that cannot be running (int.MaxValue) stands for a crashed earlier run.
        string stale = Path.Combine(Path.GetTempPath(), "SampleDrum-simulacao-" + int.MaxValue);
        Directory.CreateDirectory(Path.Combine(stale, "samples"));
        try
        {
            Assert.True(StartupOptions.CleanupStaleLibraries() >= 1);
            Assert.False(Directory.Exists(stale));
        }
        finally { if (Directory.Exists(stale)) Directory.Delete(stale, true); }
    }
}
