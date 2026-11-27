using System.Security.Cryptography;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote;

/// <summary>A paired phone as shown on the PC. <see cref="Key"/> is the SHA-256 hex of its token (never the token).</summary>
public sealed record PairedDevice(string Key, string Name, DateTimeOffset PairedAtUtc, DateTimeOffset? LastSeenUtc);

/// <summary>
/// Paired phones. Tokens are 256-bit random values handed out once (cookie); only their SHA-256 is kept and
/// persisted (<see cref="RemotePanelConfig.PairedDevices"/>). Thread-safe.
/// </summary>
public sealed class DeviceRegistry
{
    public const int MaxDevices = 16;
    public const int MaxNameLength = 40;

    /// <summary>How often a last-seen update alone raises <see cref="Changed"/> (persistence throttle).</summary>
    public static readonly TimeSpan LastSeenPersistInterval = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _devices = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public DeviceRegistry(IEnumerable<PairedDeviceConfig>? initial = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        foreach (var d in initial ?? [])
        {
            if (d is null || !IsHashHex(d.TokenSha256Hex)) continue;
            string key = d.TokenSha256Hex.ToLowerInvariant();
            _devices[key] = new Entry(key, CleanName(d.Name), d.PairedAtUtc, d.LastSeenUtc, d.LastSeenUtc);
        }
    }

    /// <summary>List changed (pairing, removal, throttled last-seen). Raised outside the lock, on the caller's thread.</summary>
    public event EventHandler? Changed;

    /// <summary>A device was removed (revocation/logout): its open SSE connections must close now.</summary>
    public event Action<string>? Revoked;

    public int Count { get { lock (_gate) return _devices.Count; } }

    public IReadOnlyList<PairedDevice> List()
    {
        lock (_gate)
            return _devices.Values.OrderBy(e => e.PairedAt).Select(e => e.ToPublic()).ToArray();
    }

    /// <summary>Persistable copy (hashes only).</summary>
    public List<PairedDeviceConfig> Export()
    {
        lock (_gate)
            return _devices.Values.OrderBy(e => e.PairedAt).Select(e => new PairedDeviceConfig
            {
                Name = e.Name, TokenSha256Hex = e.Key, PairedAtUtc = e.PairedAt, LastSeenUtc = e.LastSeen,
            }).ToList();
    }

    /// <summary>Creates a device and returns its raw token (only time it exists outside the phone). Null when full.</summary>
    public (string Token, PairedDevice Device)? Add(string? name)
    {
        string token = NewToken();
        string key = Hash(token);
        PairedDevice device;
        lock (_gate)
        {
            if (_devices.Count >= MaxDevices) return null;
            var now = _time.GetUtcNow();
            var e = new Entry(key, CleanName(name), now, now, now);
            _devices[key] = e;
            device = e.ToPublic();
        }
        RaiseChanged();
        return (token, device);
    }

    /// <summary>Device of a cookie token, or null (missing, malformed, unknown or revoked). Updates last seen.</summary>
    public PairedDevice? Validate(string? token)
    {
        if (!IsTokenShape(token)) return null;
        string key = Hash(token!);
        bool persist = false;
        PairedDevice? result;
        lock (_gate)
        {
            if (!_devices.TryGetValue(key, out var e)) return null;
            var now = _time.GetUtcNow();
            e.LastSeen = now;
            if (e.LastPersisted is null || now - e.LastPersisted.Value >= LastSeenPersistInterval)
            {
                e.LastPersisted = now;
                persist = true;
            }
            result = e.ToPublic();
        }
        if (persist) RaiseChanged();
        return result;
    }

    /// <summary>True when the key is still paired (cheap check for open SSE connections).</summary>
    public bool Contains(string key) { lock (_gate) return _devices.ContainsKey(key); }

    public bool Remove(string key)
    {
        bool removed;
        lock (_gate) removed = key is not null && _devices.Remove(key);
        if (!removed) return false;
        try { Revoked?.Invoke(key!); } catch { }
        RaiseChanged();
        return true;
    }

    public int RemoveAll()
    {
        string[] keys;
        lock (_gate)
        {
            keys = _devices.Keys.ToArray();
            _devices.Clear();
        }
        foreach (var k in keys) try { Revoked?.Invoke(k); } catch { }
        if (keys.Length > 0) RaiseChanged();
        return keys.Length;
    }

    /// <summary>256-bit random token, base64url without padding (43 characters).</summary>
    internal static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lower-case SHA-256 hex of the token.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));

    internal static bool IsTokenShape(string? token) =>
        token is { Length: 43 } && token.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static bool IsHashHex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    /// <summary>Trimmed, control characters removed, at most <see cref="MaxNameLength"/>; empty → "Celular".</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? "Celular" : clean;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private sealed class Entry(string key, string name, DateTimeOffset pairedAt, DateTimeOffset? lastSeen, DateTimeOffset? lastPersisted)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public DateTimeOffset PairedAt { get; } = pairedAt;
        public DateTimeOffset? LastSeen { get; set; } = lastSeen;
        public DateTimeOffset? LastPersisted { get; set; } = lastPersisted;
        public PairedDevice ToPublic() => new(Key, Name, PairedAt, LastSeen);
    }
}
