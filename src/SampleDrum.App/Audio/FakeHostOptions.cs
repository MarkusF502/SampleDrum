using SampleDrum.Core.Simulation;

namespace SampleDrum.App.Audio;

/// <summary>Where the simulation host gets its four mic signals from.</summary>
public enum SimulationSource
{
    /// <summary>Generated synthetic kit scenario (deterministic, with expected events).</summary>
    SyntheticKit,
    /// <summary>Four mono (or stereo → mono) WAV files, one per piece (Bumbo, Caixa, Tom, Surdo).</summary>
    MicWavs,
    /// <summary>One multichannel WAV; <see cref="FakeHostOptions.MultichannelChannels"/> picks the channel of each piece.</summary>
    MultichannelWav,
}

/// <summary>
/// Options of <see cref="FakeAudioHost"/> ("Simulação (sem ASIO)"). Source, block size, loop and capture apply at
/// <see cref="FakeAudioHost.Start"/>; preview on/off and <see cref="Preview"/> can also be changed while running via
/// <see cref="FakeAudioHost.PreviewEnabled"/> / <see cref="FakeAudioHost.Preview"/>.
/// </summary>
public sealed record FakeHostOptions
{
    /// <summary>Block sizes offered in the UI (same choices as typical ASIO buffers).</summary>
    public static IReadOnlyList<int> BlockSizes { get; } = [64, 128, 256, 480];

    public SimulationSource Source { get; init; } = SimulationSource.SyntheticKit;

    /// <summary>Seed of the synthetic scenario.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Per piece (DrumPiece order) mic WAV path for <see cref="SimulationSource.MicWavs"/>; null = silence.</summary>
    public IReadOnlyList<string?> MicPaths { get; init; } = [null, null, null, null];

    /// <summary>Multichannel WAV for <see cref="SimulationSource.MultichannelWav"/>.</summary>
    public string? MultichannelPath { get; init; }

    /// <summary>1-based channel of the multichannel WAV for each piece (DrumPiece order); null = 1, 2, 3, 4.
    /// 0 = piece gets silence.</summary>
    public IReadOnlyList<int>? MultichannelChannels { get; init; }

    /// <summary>Frames per block.</summary>
    public int BlockSize { get; init; } = 256;

    /// <summary>Restart at the end of the source (true) or stop and raise <see cref="FakeAudioHost.Finished"/>.</summary>
    public bool Loop { get; init; } = true;

    /// <summary>Play the stereo preview on the PC's default output (headphones/speakers) from the start.</summary>
    public bool PreviewEnabled { get; init; }

    /// <summary>Preview volume, pieces in the mix and mic level.</summary>
    public PreviewMixSettings Preview { get; init; } = PreviewMixSettings.Default;

    /// <summary>Seconds of routed returns to capture from the start (0 = no capture). Used by the harness.</summary>
    public double CaptureSeconds { get; init; }

    /// <summary>Pacing factor: 1 = real time (default, UI and --play). &gt;1 runs faster (automatic checks only;
    /// the block-by-block processing is identical, only the wall-clock pacing changes).</summary>
    public double Speed { get; init; } = 1.0;
}
