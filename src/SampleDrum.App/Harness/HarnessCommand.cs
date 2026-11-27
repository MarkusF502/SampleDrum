using SampleDrum.App.Audio;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Harness;

/// <summary>
/// <c>SampleDrum.App.exe --harness &lt;pasta&gt; [--mics a b c d | --mics4 f.wav --channels 1,2,3,4] [--block N]
/// [--seed S] [--realtime] [--play]</c>: the shared scenario runner (<see cref="HarnessCli"/>) plus the real-time run
/// through <see cref="FakeAudioHost"/> and, with <c>--play</c>, the audible stereo preview on the PC's default output.
/// A published build alone is enough (no SDK, no ASIO, no X32).
/// </summary>
public static class HarnessCommand
{
    /// <summary><c>--harness --swap ...</c> runs the swap verification (<see cref="SwapCommand"/>).</summary>
    public static int Run(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == "--swap"
            ? SwapCommand.Run(args.Skip(1).ToList())
            : HarnessCli.Run(args, "SampleDrum.App.exe --harness", Realtime);

    private static HarnessResult Realtime(KitScenario sc, HarnessCliOptions o, Action<string> log)
    {
        var source = o.Mics is not null
            ? new FakeHostOptions { Source = SimulationSource.MicWavs, MicPaths = o.MicPaths! }
            : o.MultichannelMics is not null
                ? new FakeHostOptions { Source = SimulationSource.MultichannelWav, MultichannelPath = o.MultichannelMics, MultichannelChannels = o.Channels }
                : new FakeHostOptions { Source = SimulationSource.SyntheticKit, Seed = o.Seed };
        return RealtimeChecks.RunRealtime(sc, source, o.BlockSize, speed: 1.0, preview: o.Play, log);
    }
}
