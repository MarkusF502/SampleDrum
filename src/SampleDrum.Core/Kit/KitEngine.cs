namespace SampleDrum.Core.Kit;

/// <summary>
/// Four independent trigger pipelines (detector + voice pool + return) behind one <see cref="IMultiChannelProcessor"/>.
/// No mutable state is shared between pieces, including when several pieces trigger in the same block.
/// <para>
/// Thread model: <see cref="Process"/> runs on the audio thread (no allocation/lock/log/I/O). Settings, kit, enable
/// flags and test requests are lock-free swaps/flags usable from any thread, applied at the next block start.
/// <see cref="TryDequeueEvent"/> has a single consumer (UI/log). <see cref="Reset"/> only while no host runs.
/// </para>
/// </summary>
public sealed class KitEngine : IMultiChannelProcessor
{
    public KitEngine(int sampleRate, KitSettings settings)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(settings);
        SampleRate = sampleRate;
        Settings = settings;
    }

    public int SampleRate { get; }
    public int PieceCount => DrumPieces.Count;

    /// <summary>Most recently submitted settings.</summary>
    public KitSettings Settings { get; private set; }

    /// <summary>Master "Ativar/Desativar kit". Starts false. Disabled = silence except manual tests.</summary>
    public bool KitEnabled { get; set; }

    public void SetPieceEnabled(DrumPiece p, bool on) { }
    public bool IsPieceEnabled(DrumPiece p) => true;

    /// <summary>Manual test of one piece at the next block start; works even when disabled.</summary>
    public void RequestTest(DrumPiece p, float gain = 1f) { }

    /// <summary>Off the audio thread; atomic swap applied at the next block start.</summary>
    public void UpdatePieceSettings(DrumPiece p, PieceSettings s) { }

    /// <summary>
    /// Swaps all piece samples (and optional per-piece gain/detection) with one reference swap, effective at the
    /// next block start. Voices already playing finish with their old sample.
    /// </summary>
    public void ApplyKit(PreparedKit kit) { }

    public PreparedKit? ActiveKit => null;

    public bool TryDequeueEvent(out KitEvent e) { e = default; return false; }

    public float ReadAndResetInputPeak(DrumPiece p) => 0f;

    /// <summary>Clears voices, detectors, pending tests and meters. Only while no host runs.</summary>
    public void Reset() { }

    public void Process(float[][] inputs, float[][] outputs, int frames)
    {
        // STEP 0 stub.
    }
}
