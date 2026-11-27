using System.Net;
using System.Security.Cryptography;

namespace SampleDrum.Remote;

public enum PairingOutcome { Paired, Invalid, Locked }

/// <summary>Result of one pairing attempt; <see cref="RetryAfter"/> is set when <see cref="Outcome"/> is Locked.</summary>
public readonly record struct PairingResult(PairingOutcome Outcome, TimeSpan RetryAfter = default);

/// <summary>
/// Temporary 6-digit pairing code shown on the PC. Valid for <see cref="Lifetime"/>, single use (a new code after
/// each successful pairing), renewed when it expires, on demand, and after <see cref="MaxFailuresPerCode"/> wrong
/// attempts. Attempt limits: <see cref="MaxFailuresPerIp"/> wrong codes per IP per <see cref="Window"/> lock that IP
/// for the rest of the window; <see cref="MaxFailuresGlobal"/> wrong codes in a window lock pairing for everyone.
/// Thread-safe. The code is compared in constant time.
/// </summary>
public sealed class PairingService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public const int MaxFailuresPerIp = 5;
    public const int MaxFailuresPerCode = 10;
    public const int MaxFailuresGlobal = 20;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failuresByIp = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _failures = new();
    private string _code = "";
    private DateTimeOffset _expiresAt;
    private int _failuresOnCode;

    public PairingService(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        lock (_gate) NewCodeLocked();
    }

    /// <summary>The code changed (renewed, used, expired or invalidated). Raised outside the lock.</summary>
    public event EventHandler? CodeChanged;

    /// <summary>Current code (renewed first if expired).</summary>
    public string Code { get { EnsureFresh(); lock (_gate) return _code; } }

    public DateTimeOffset ExpiresAt { get { EnsureFresh(); lock (_gate) return _expiresAt; } }

    public TimeSpan Remaining
    {
        get
        {
            var left = ExpiresAt - _time.GetUtcNow();
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>"Gerar novo código": invalidates the current code now.</summary>
    public string Regenerate()
    {
        string code;
        lock (_gate) code = NewCodeLocked();
        RaiseChanged();
        return code;
    }

    /// <summary>Checks a code from <paramref name="remote"/>; on success the code is consumed (a new one is shown).</summary>
    public PairingResult TryPair(string? code, IPAddress? remote)
    {
        EnsureFresh();
        bool changed = false;
        PairingResult result;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            string ip = remote?.ToString() ?? "local";
            Prune(_failures, now);
            var mine = _failuresByIp.TryGetValue(ip, out var q) ? q : null;
            if (mine is not null) Prune(mine, now);

            if (_failures.Count >= MaxFailuresGlobal)
                return new PairingResult(PairingOutcome.Locked, _failures.Peek() + Window - now);
            if (mine is { Count: >= MaxFailuresPerIp })
                return new PairingResult(PairingOutcome.Locked, mine.Peek() + Window - now);

            if (code is { Length: 6 } && code.All(char.IsAsciiDigit) &&
                CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(_code)))
            {
                NewCodeLocked();
                _failuresByIp.Remove(ip);
                result = new PairingResult(PairingOutcome.Paired);
                changed = true;
            }
            else
            {
                if (mine is null) _failuresByIp[ip] = mine = new Queue<DateTimeOffset>();
                mine.Enqueue(now);
                _failures.Enqueue(now);
                if (++_failuresOnCode >= MaxFailuresPerCode) { NewCodeLocked(); changed = true; }
                // Keep the dictionary bounded: forget IPs without recent failures.
                if (_failuresByIp.Count > 256)
                    foreach (var k in _failuresByIp.Where(kv => { Prune(kv.Value, now); return kv.Value.Count == 0; })
                                 .Select(kv => kv.Key).ToList())
                        _failuresByIp.Remove(k);
                result = new PairingResult(PairingOutcome.Invalid);
            }
        }
        if (changed) RaiseChanged();
        return result;
    }

    private void EnsureFresh()
    {
        bool renewed = false;
        lock (_gate)
        {
            if (_time.GetUtcNow() >= _expiresAt) { NewCodeLocked(); renewed = true; }
        }
        if (renewed) RaiseChanged();
    }

    private string NewCodeLocked()
    {
        string next;
        do next = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        while (next == _code);
        _code = next;
        _expiresAt = _time.GetUtcNow() + Lifetime;
        _failuresOnCode = 0;
        return next;
    }

    private static void Prune(Queue<DateTimeOffset> q, DateTimeOffset now)
    {
        while (q.Count > 0 && now - q.Peek() >= Window) q.Dequeue();
    }

    private void RaiseChanged()
    {
        try { CodeChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }
}
