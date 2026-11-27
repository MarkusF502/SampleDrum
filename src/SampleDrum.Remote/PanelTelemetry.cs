using SampleDrum.Core.Kit;

namespace SampleDrum.Remote;

/// <summary>
/// Fan-out point between the app's single engine sampler (UI timer: ReadAndResetInputPeak / TryDequeueEvent) and the
/// web hub. The app publishes; the hub takes the peak since its last read (~5 Hz) and receives hits as events.
/// Never called from the audio thread.
/// </summary>
public sealed class PanelTelemetry
{
    private readonly object _gate = new();
    private readonly float[] _peaks = new float[DrumPieces.Count];

    /// <summary>Raised on the publisher's thread for every trigger/test hit. Handlers must not block.</summary>
    public event Action<DrumPiece, float, bool>? Hit;

    /// <summary>Linear input peak of one piece since the app's previous read (keeps the max until the hub reads).</summary>
    public void PublishPeak(DrumPiece piece, float linearPeak)
    {
        if (!piece.IsValid() || !float.IsFinite(linearPeak)) return;
        lock (_gate) _peaks[(int)piece] = MathF.Max(_peaks[(int)piece], MathF.Abs(linearPeak));
    }

    public void PublishHit(DrumPiece piece, float velocity, bool isTest)
    {
        if (!piece.IsValid()) return;
        try { Hit?.Invoke(piece, float.IsFinite(velocity) ? Math.Clamp(velocity, 0f, 1f) : 0f, isTest); } catch { }
    }

    /// <summary>Peak (linear) per piece since the previous call, then reset. Hub only.</summary>
    public float[] TakePeaks()
    {
        var r = new float[DrumPieces.Count];
        lock (_gate)
        {
            Array.Copy(_peaks, r, r.Length);
            Array.Clear(_peaks);
        }
        return r;
    }

    /// <summary>dBFS with a floor of -90 (the phone meter's bottom).</summary>
    public static float ToDbfs(float linear) =>
        linear > 3.1623e-5f ? MathF.Round(20f * MathF.Log10(linear), 1) : -90f;
}
