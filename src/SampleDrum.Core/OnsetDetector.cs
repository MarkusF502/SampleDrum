namespace SampleDrum.Core;

/// <summary>Result of one detected onset.</summary>
/// <param name="OnsetIndex">Absolute sample index of the threshold crossing.</param>
/// <param name="FireIndex">Absolute sample index where the trigger fires (= onset + scan window).</param>
/// <param name="Peak">Linear peak of |detector signal| over the scan window.</param>
/// <param name="Velocity">0..1, see <see cref="TriggerSettings.VelocityRangeDb"/>.</param>
public readonly record struct DetectedOnset(long OnsetIndex, long FireIndex, float Peak, float Velocity);

/// <summary>
/// Sample-by-sample onset detector. All state lives in fields, so feeding a signal in blocks of any size
/// gives exactly the same result as feeding it at once. No allocations.
/// <para>
/// Signal path: input -> optional high-pass (detector path only) -> |x| -> peak follower
/// (instant attack, exponential release) = envelope.
/// </para>
/// <para>
/// State machine:
/// <list type="bullet">
/// <item>ARMED: envelope &gt;= threshold → onset at this sample. The detector disarms. If the onset is closer
/// than MinInterval to the previous accepted onset it is suppressed (no trigger), otherwise it is accepted.</item>
/// <item>SCAN (only when ScanMs &gt; 0): the peak of |x| is tracked for ScanSamples; the trigger fires at
/// onset + ScanSamples with the velocity derived from that peak.</item>
/// <item>DISARMED: re-arms once the envelope stays below RearmDb for RearmTimeMs (hysteresis + release time),
/// and never while a scan is running.</item>
/// </list>
/// </para>
/// </summary>
public sealed class OnsetDetector
{
    private EngineParams _p;

    // Envelope / filter state.
    private float _env;
    private double _hpZ1, _hpZ2;

    // State machine.
    private bool _armed = true;
    private int _belowCount;
    private bool _scanning;
    private int _scanRemaining;
    private float _scanPeak;
    private long _scanOnset;
    private long _lastOnset = long.MinValue / 4;

    public OnsetDetector(int sampleRate, TriggerSettings settings)
        : this(new EngineParams(sampleRate, settings)) { }

    internal OnsetDetector(EngineParams p) { _p = p; }

    /// <summary>Current envelope (linear).</summary>
    public float Envelope => _env;

    /// <summary>True when the detector would accept a new onset (apart from the lockout).</summary>
    public bool IsArmed => _armed;

    /// <summary>
    /// Applies new parameters, keeping the state. If the envelope is already above the new threshold
    /// (e.g. the threshold was lowered during a sustain) the detector disarms, so a settings change never
    /// fires a spurious trigger by itself; it re-arms through the normal hysteresis.
    /// </summary>
    internal void SetParams(EngineParams p)
    {
        _p = p;
        if (_armed && _env >= p.ThresholdLin) { _armed = false; _belowCount = 0; }
    }

    /// <summary>Clears all state (envelope, filter, hysteresis, lockout).</summary>
    public void Reset()
    {
        _env = 0f; _hpZ1 = _hpZ2 = 0;
        _armed = true; _belowCount = 0; _scanning = false; _scanRemaining = 0; _scanPeak = 0f;
        _lastOnset = long.MinValue / 4;
    }

    /// <summary>
    /// Processes one input sample with absolute index <paramref name="index"/>.
    /// Returns true when a trigger fires on this sample.
    /// </summary>
    public bool Step(float x, long index, out DetectedOnset onset)
    {
        var p = _p;
        onset = default;

        float y = x;
        if (p.HighPassEnabled)
        {
            double xd = x;
            double yd = p.HpB0 * xd + _hpZ1;
            _hpZ1 = p.HpB1 * xd - p.HpA1 * yd + _hpZ2;
            _hpZ2 = p.HpB2 * xd - p.HpA2 * yd;
            y = (float)yd;
        }

        float a = y < 0f ? -y : y;
        float env = _env * p.EnvelopeReleaseCoef;
        if (a > env) env = a;
        if (env < 1e-30f) env = 0f; // flush denormals
        _env = env;

        bool fired = false;

        if (_scanning)
        {
            if (--_scanRemaining <= 0)
            {
                _scanning = false;
                float vel = p.VelocityFromPeak(_scanPeak);
                onset = new DetectedOnset(_scanOnset, index, _scanPeak, vel);
                fired = true;
            }
            else if (a > _scanPeak)
            {
                _scanPeak = a;
            }
        }

        if (_armed)
        {
            if (env >= p.ThresholdLin)
            {
                _armed = false;
                _belowCount = 0;
                if (index - _lastOnset >= p.MinIntervalSamples)
                {
                    _lastOnset = index;
                    if (p.ScanSamples <= 0)
                    {
                        onset = new DetectedOnset(index, index, a, p.VelocityFromPeak(a));
                        fired = true;
                    }
                    else
                    {
                        _scanning = true;
                        _scanRemaining = p.ScanSamples;
                        _scanPeak = a;
                        _scanOnset = index;
                    }
                }
                // else: suppressed by the lockout; stays disarmed so its sustain cannot retrigger later.
            }
        }
        else if (!_scanning)
        {
            if (env < p.RearmLin)
            {
                _belowCount++;
                if (_belowCount >= p.RearmSamples) { _armed = true; _belowCount = 0; }
            }
            else
            {
                _belowCount = 0;
            }
        }

        return fired;
    }
}
