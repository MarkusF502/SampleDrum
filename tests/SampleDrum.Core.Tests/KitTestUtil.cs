using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

internal static class KitTestUtil
{
    /// <summary>Four silent mic tracks of <paramref name="seconds"/>.</summary>
    public static float[][] Mics(double seconds) =>
        DrumPieces.All.Select(_ => new float[(int)(seconds * Fs)]).ToArray();

    /// <summary>A kit whose piece p plays a constant sample of value <paramref name="values"/>[p].</summary>
    public static PreparedKit DcKit(int length, params float[] values) => new("dc", Fs,
        DrumPieces.All.Select(p => new PreparedPiece(p,
            Enumerable.Repeat(values.Length > (int)p ? values[(int)p] : 1f, length).ToArray(), null, $"dc{(int)p}")));

    public static KitEngine Engine(KitSettings? settings = null, PreparedKit? kit = null, bool enabled = true)
    {
        var e = new KitEngine(Fs, settings ?? KitSettings.Default) { KitEnabled = enabled };
        e.ApplyKit(kit ?? DcKit(Fs / 10));
        return e;
    }

    /// <summary>Same settings for all pieces.</summary>
    public static KitSettings AllPieces(TriggerSettings t) =>
        new(DrumPieces.All.Select(_ => new PieceSettings { Trigger = t }).ToArray());

    /// <summary>Runs the engine over the mic tracks in blocks, exactly like a host (outputs pre-cleared per block).</summary>
    public static (float[][] Outputs, List<KitEvent> Events) Run(KitEngine engine, float[][] mics, int block)
    {
        int len = mics[0].Length;
        var outputs = mics.Select(_ => new float[len]).ToArray();
        var events = new List<KitEvent>();
        var inBlocks = mics.Select(_ => new float[block]).ToArray();
        var outBlocks = mics.Select(_ => new float[block]).ToArray();
        for (int start = 0; start < len; start += block)
        {
            int n = Math.Min(block, len - start);
            for (int p = 0; p < mics.Length; p++)
            {
                Array.Copy(mics[p], start, inBlocks[p], 0, n);
                Array.Clear(outBlocks[p]);
            }
            engine.Process(inBlocks, outBlocks, n);
            for (int p = 0; p < mics.Length; p++) Array.Copy(outBlocks[p], 0, outputs[p], start, n);
            while (engine.TryDequeueEvent(out var e)) events.Add(e);
        }
        return (outputs, events);
    }

}
