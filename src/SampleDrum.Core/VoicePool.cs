namespace SampleDrum.Core;

/// <summary>
/// Fixed pool of one-shot sample voices. Allocated once; no allocation while playing.
/// At most <c>maxVoices</c> voices play normally; when a new voice exceeds that, the oldest one is
/// faded out over a few ms (it keeps a slot while fading, which is why the pool has 2x slots).
/// </summary>
internal sealed class VoicePool
{
    private struct Voice
    {
        public float[]? Data;
        public int Pos;
        public float Gain;
        public bool Fading;
        public int FadeRemaining;
        public int FadeLength;
        public long Order;
        public bool IsTest;
    }

    public const int MaxSupportedVoices = 16;

    private readonly Voice[] _voices = new Voice[MaxSupportedVoices * 2];
    private long _order;

    public int ActiveCount
    {
        get
        {
            int c = 0;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].Data != null) c++;
            return c;
        }
    }

    public void Start(float[] data, float gain, bool isTest, int maxVoices, int fadeSamples)
    {
        if (data.Length == 0) return;

        // Enforce polyphony among non-fading voices: fade (steal) the oldest ones.
        int playing = 0;
        for (int i = 0; i < _voices.Length; i++)
            if (_voices[i].Data != null && !_voices[i].Fading) playing++;
        while (playing >= maxVoices)
        {
            int oldest = -1;
            for (int i = 0; i < _voices.Length; i++)
            {
                ref var v = ref _voices[i];
                if (v.Data != null && !v.Fading && (oldest < 0 || v.Order < _voices[oldest].Order)) oldest = i;
            }
            if (oldest < 0) break;
            BeginFade(ref _voices[oldest], fadeSamples);
            playing--;
        }

        int slot = -1;
        for (int i = 0; i < _voices.Length; i++) if (_voices[i].Data == null) { slot = i; break; }
        if (slot < 0)
        {
            // Every slot is busy fading: hard-cut the one closest to the end of its fade.
            for (int i = 0; i < _voices.Length; i++)
                if (slot < 0 || _voices[i].FadeRemaining < _voices[slot].FadeRemaining) slot = i;
        }

        _voices[slot] = new Voice { Data = data, Pos = 0, Gain = gain, Order = ++_order, IsTest = isTest };
    }

    /// <summary>Fades out every automatic (non-test) voice, e.g. when the engine is disabled.</summary>
    public void FadeOutAutomatic(int fadeSamples)
    {
        for (int i = 0; i < _voices.Length; i++)
        {
            ref var v = ref _voices[i];
            if (v.Data != null && !v.IsTest && !v.Fading) BeginFade(ref v, fadeSamples);
        }
    }

    public void Clear() => Array.Clear(_voices);

    private static void BeginFade(ref Voice v, int fadeSamples)
    {
        if (fadeSamples <= 0) { v.Data = null; return; }
        v.Fading = true;
        v.FadeLength = fadeSamples;
        v.FadeRemaining = fadeSamples;
    }

    /// <summary>Adds all active voices into <paramref name="output"/>.</summary>
    public void Render(Span<float> output)
    {
        if (output.IsEmpty) return;
        for (int vi = 0; vi < _voices.Length; vi++)
        {
            ref var v = ref _voices[vi];
            var data = v.Data;
            if (data == null) continue;

            int n = Math.Min(output.Length, data.Length - v.Pos);
            if (!v.Fading)
            {
                float g = v.Gain;
                var src = data.AsSpan(v.Pos, n);
                for (int i = 0; i < n; i++) output[i] += src[i] * g;
                v.Pos += n;
            }
            else
            {
                n = Math.Min(n, v.FadeRemaining);
                float step = v.Gain / v.FadeLength;
                float g = v.Gain * v.FadeRemaining / v.FadeLength;
                var src = data.AsSpan(v.Pos, n);
                for (int i = 0; i < n; i++) { g -= step; output[i] += src[i] * g; }
                v.Pos += n;
                v.FadeRemaining -= n;
                if (v.FadeRemaining <= 0) { v.Data = null; continue; }
            }
            if (v.Pos >= data.Length) v.Data = null;
        }
    }
}
