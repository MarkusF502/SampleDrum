namespace SampleDrum.Core;

/// <summary>Result of one detected onset.</summary>
/// <param name="OnsetIndex">Absolute sample index of the threshold crossing.</param>
/// <param name="FireIndex">Absolute sample index where the trigger fires (= onset + max(detect, scan) window).</param>
/// <param name="Peak">Linear peak over the window of max(|velocity path|, |detector path|). The velocity path is the input
/// through a gentle fixed 20 Hz high-pass (the detector's 40 Hz high-pass shrinks the first half-cycle of a low kick
/// by several dB; 20 Hz still removes rumble). Taking the max keeps velocity at or above the level that triggered.</param>
/// <param name="Velocity">0..1, see <see cref="TriggerSettings.VelocityRangeDb"/>.</param>
/// <param name="IsRetrigger">True when the onset was accepted during the previous hit's sustain (rise re-trigger).</param>
public readonly record struct DetectedOnset(long OnsetIndex, long FireIndex, float Peak, float Velocity, bool IsRetrigger);

/// <summary>Detector state (see <see cref="OnsetDetector"/>).</summary>
public enum DetectorState
{
    /// <summary>Armed, waiting for a crossing.</summary>
    Off,
    /// <summary>Crossing seen; debounce / velocity window running.</summary>
    Detect,
    /// <summary>Triggered (or crossing suppressed); waiting for the envelope to fall below the release level.</summary>
    On,
    /// <summary>Envelope below the release level; re-arms after the release time.</summary>
    Release,
}

/// <summary>
/// Sample-by-sample onset detector. All state lives in fields, so feeding a signal in blocks of any size gives
/// exactly the same result as feeding it at once. No allocations.
/// <para>
/// Re-implemented from the behaviour described in docs/research/lsp-trigger.md (LSP Trigger, LGPL-3,
/// used only as a behavioural reference; no LSP code is copied or translated), with two additions LSP lacks:
/// an absolute minimum interval and a rise-based re-trigger during a sustain.
/// </para>
/// <para>
/// Signal path: input -> high-pass (optional) -> low-pass (optional) -> |y| -> peak-hold envelope
/// (instant attack, exponential release). The envelope, never raw |y|, drives the release logic, so the zero
/// crossings of a 50-60 Hz kick do not look like the end of the hit.
/// </para>
/// <para>
/// States:
/// <list type="bullet">
/// <item>OFF → DETECT when |y| reaches the threshold (the crossing = onset). Inside the lockout
/// (MinInterval since the last accepted onset) the crossing is suppressed and the detector goes straight to ON,
/// so that hit's sustain cannot fire later.</item>
/// <item>DETECT: collects max(DetectTime, Scan) samples starting at the crossing. After DetectTime samples the
/// mean square of |y| must reach the threshold (debounce); otherwise it was an isolated spike → back to OFF
/// (or ON if it started on a sustain). At the end of the window the trigger fires with the peak of the velocity path
/// → ON.</item>
/// <item>ON → RELEASE when the envelope falls to the release level; RELEASE → ON if it rises again;
/// RELEASE → OFF (re-armed) after ReleaseTime below it.</item>
/// <item>ON/RELEASE re-trigger: after the lockout, if the envelope rises RetriggerRiseDb above its minimum since
/// the lockout ended and is above the threshold → DETECT.</item>
/// </list>
/// </para>
/// </summary>
public sealed class OnsetDetector
{
    private EngineParams _p;

    // Filters / envelope.
    private double _hpZ1, _hpZ2, _lpZ1, _lpZ2;
    private float _env;

    // State machine.
    private DetectorState _state = DetectorState.Off;
    private long _onset;
    private int _windowCount;
    private double _sumSq;
    private float _peak;
    private float _velAbs;   // velocity-path level of the current sample (see Step)
    private double _vpZ1, _vpZ2;
    private bool _debounced;
    private bool _fromSustain;
    private int _releaseCount;
    private float _envMin;
    private long _lastOnset = long.MinValue / 4;
    // Window lengths frozen at the crossing, so a settings change in the middle of DETECT
    // can neither skip the debounce nor cut the window short.
    private int _winDetect, _winTotal;

    /// <summary>
    /// A filter's two states are flushed to exactly 0 together once both are below this magnitude (avoids subnormal
    /// slowdowns in silence). Flushing them one at a time would inject tiny errors that the high internal gain of a
    /// low-cutoff high-pass near DC (1/(1+a1+a2), ~146000 at 20 Hz) keeps alive around 1e-23 instead of decaying.
    /// </summary>
    private const double DenormalFlush = 1e-25;

    public OnsetDetector(int sampleRate, TriggerSettings settings)
        : this(new EngineParams(sampleRate, settings)) { }

    internal OnsetDetector(EngineParams p) { _p = p; }

    /// <summary>Current envelope (linear).</summary>
    public float Envelope => _env;

    /// <summary>Current state.</summary>
    public DetectorState State => _state;

    /// <summary>True when all filter states are exactly zero (diagnostics/tests).</summary>
    internal bool FilterStateIsZero => _hpZ1 == 0 && _hpZ2 == 0 && _lpZ1 == 0 && _lpZ2 == 0 && _vpZ1 == 0 && _vpZ2 == 0;

    /// <summary>True when the detector is OFF, i.e. armed for a normal crossing.</summary>
    public bool IsArmed => _state == DetectorState.Off;

    /// <summary>
    /// Applies new parameters, keeping the state. If the envelope is already above the new threshold
    /// (e.g. the threshold was lowered during a sustain) an armed detector goes to ON, so a settings change
    /// never fires a spurious trigger by itself; it re-arms through the normal release.
    /// </summary>
    internal void SetParams(EngineParams p)
    {
        _p = p;
        if (_state == DetectorState.Off && _env >= p.ThresholdLin) EnterOn();
    }

    /// <summary>Clears all state (filters, envelope, state machine, lockout).</summary>
    public void Reset()
    {
        _hpZ1 = _hpZ2 = _lpZ1 = _lpZ2 = _vpZ1 = _vpZ2 = 0;
        _env = 0f;
        _state = DetectorState.Off;
        _windowCount = 0; _sumSq = 0; _peak = 0f; _velAbs = 0f; _debounced = false; _fromSustain = false;
        _winDetect = _winTotal = 0;
        _releaseCount = 0; _envMin = 0f;
        _lastOnset = long.MinValue / 4;
    }

    /// <summary>
    /// Processes one input sample with absolute index <paramref name="index"/>.
    /// Returns true when a trigger fires on this sample (the voice should start at this sample).
    /// </summary>
    public bool Step(float x, long index, out DetectedOnset onset)
    {
        var p = _p;
        onset = default;

        double y = x;
        if (!double.IsFinite(y)) y = 0;

        // Velocity path: input -> fixed 20 Hz high-pass (rumble/DC out, the kick's attack nearly untouched).
        double v = y;
        if (p.VelocityHighPass.Enabled)
        {
            ref readonly var c = ref p.VelocityHighPass;
            double o = c.B0 * v + _vpZ1;
            _vpZ1 = c.B1 * v - c.A1 * o + _vpZ2;
            _vpZ2 = c.B2 * v - c.A2 * o;
            if (Math.Abs(_vpZ1) < DenormalFlush && Math.Abs(_vpZ2) < DenormalFlush) _vpZ1 = _vpZ2 = 0;
            v = o;
        }

        if (p.HighPass.Enabled)
        {
            ref readonly var c = ref p.HighPass;
            double o = c.B0 * y + _hpZ1;
            _hpZ1 = c.B1 * y - c.A1 * o + _hpZ2;
            _hpZ2 = c.B2 * y - c.A2 * o;
            if (Math.Abs(_hpZ1) < DenormalFlush && Math.Abs(_hpZ2) < DenormalFlush) _hpZ1 = _hpZ2 = 0;
            y = o;
        }
        if (p.LowPass.Enabled)
        {
            ref readonly var c = ref p.LowPass;
            double o = c.B0 * y + _lpZ1;
            _lpZ1 = c.B1 * y - c.A1 * o + _lpZ2;
            _lpZ2 = c.B2 * y - c.A2 * o;
            if (Math.Abs(_lpZ1) < DenormalFlush && Math.Abs(_lpZ2) < DenormalFlush) _lpZ1 = _lpZ2 = 0;
            y = o;
        }

        float a = (float)Math.Abs(y);
        if (!float.IsFinite(a)) { a = 0f; _hpZ1 = _hpZ2 = _lpZ1 = _lpZ2 = 0; }
        // Velocity level = max(velocity path, detector path): never below the level that triggered.
        float va = (float)Math.Abs(v);
        if (!float.IsFinite(va)) { va = 0f; _vpZ1 = _vpZ2 = 0; }
        _velAbs = va > a ? va : a;
        float env = _env * p.EnvelopeReleaseCoef;
        if (a > env) env = a;
        if (env < 1e-30f) env = 0f; // flush denormals
        _env = env;

        switch (_state)
        {
            case DetectorState.Off:
                if (a >= p.ThresholdLin) return Cross(index, a, fromSustain: false, out onset);
                return false;

            case DetectorState.Detect:
                return StepDetect(index, a, out onset);

            case DetectorState.On:
                if (env <= p.RearmLin)
                {
                    _state = DetectorState.Release;
                    _releaseCount = 0;
                    goto case DetectorState.Release;
                }
                return TryRetrigger(index, a, env, out onset);

            case DetectorState.Release:
                if (env > p.RearmLin)
                {
                    _state = DetectorState.On;
                    return TryRetrigger(index, a, env, out onset);
                }
                if (++_releaseCount >= p.RearmSamples)
                {
                    _state = DetectorState.Off;
                    return false;
                }
                return TryRetrigger(index, a, env, out onset);
        }
        return false;
    }

    /// <summary>A threshold crossing at <paramref name="index"/> (from OFF, or a re-trigger rise).</summary>
    private bool Cross(long index, float a, bool fromSustain, out DetectedOnset onset)
    {
        var p = _p;
        onset = default;
        if (index - _lastOnset < p.MinIntervalSamples)
        {
            if (!fromSustain) EnterOn(); // suppressed: wait for release so the sustain cannot fire later
            else _envMin = _env;
            return false;
        }

        _onset = index;
        _fromSustain = fromSustain;
        _peak = _velAbs;
        _windowCount = 1;
        _sumSq = (double)a * a;
        _winDetect = p.DetectSamples;
        _winTotal = p.WindowSamples;
        _debounced = _winDetect <= 0;
        if (_winTotal <= 0)
        {
            Fire(index, out onset);
            return true;
        }
        _state = DetectorState.Detect;
        return false;
    }

    private bool StepDetect(long index, float a, out DetectedOnset onset)
    {
        var p = _p;
        onset = default;

        // _windowCount samples [onset, index) are already in the window.
        if (!_debounced && _windowCount >= _winDetect)
        {
            if (_sumSq / _winDetect < p.ThresholdSq)
            {
                // Isolated spike: abort without touching the lockout.
                if (_fromSustain) { EnterOn(); return false; }
                _state = DetectorState.Off;
                return a >= p.ThresholdLin && Cross(index, a, fromSustain: false, out onset);
            }
            _debounced = true;
        }

        if (_windowCount >= _winTotal)
        {
            Fire(index, out onset);
            return true;
        }

        if (_windowCount < _winDetect) _sumSq += (double)a * a;
        if (_velAbs > _peak) _peak = _velAbs;
        _windowCount++;
        return false;
    }

    private void Fire(long index, out DetectedOnset onset)
    {
        onset = new DetectedOnset(_onset, index, _peak, _p.VelocityFromPeak(_peak), _fromSustain);
        _lastOnset = _onset;
        EnterOn();
    }

    private void EnterOn()
    {
        _state = DetectorState.On;
        _releaseCount = 0;
        _envMin = _env;
    }

    private bool TryRetrigger(long index, float a, float env, out DetectedOnset onset)
    {
        onset = default;
        var p = _p;
        if (p.RetriggerRiseLin <= 0f) return false;
        if (index - _lastOnset < p.MinIntervalSamples)
        {
            _envMin = env; // reference follows the envelope during the lockout (covers the hit's own attack)
            return false;
        }
        if (env < _envMin) { _envMin = env; return false; }
        if (env >= p.ThresholdLin && env >= _envMin * p.RetriggerRiseLin)
            return Cross(index, a, fromSustain: true, out onset);
        return false;
    }
}
