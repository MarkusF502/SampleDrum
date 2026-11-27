using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SampleDrum.Remote;

/// <summary>
/// Host allowlist (anti DNS rebinding), Origin check for state-changing requests and client network filter. Built once
/// per server start from <see cref="PanelOptions"/>.
/// </summary>
internal sealed class AccessGuard
{
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly PanelOptions _options;
    private readonly uint _network, _mask;

    public AccessGuard(PanelOptions options)
    {
        _options = options;
        int port = options.Port;
        _hosts.Add($"localhost:{port}");
        _hosts.Add($"127.0.0.1:{port}");
        _hosts.Add($"[::1]:{port}");
        if (!string.IsNullOrWhiteSpace(options.MachineName)) _hosts.Add($"{options.MachineName.Trim()}.local:{port}");
        if (options.Address is { } a) _hosts.Add($"{PanelServer.FormatHost(a.ToString())}:{port}");
        if (options.AnyAddress)
            foreach (var ip in LocalIPv4Addresses()) _hosts.Add($"{ip}:{port}");
        if (options.Address is { AddressFamily: AddressFamily.InterNetwork } addr)
        {
            int prefix = Math.Clamp(options.PrefixLength, 0, 32);
            _mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            _network = ToUInt(addr) & _mask;
        }
    }

    public IReadOnlyCollection<string> AllowedHosts => _hosts;

    /// <summary>Host header must be exactly one of the allowed host:port values.</summary>
    public bool IsHostAllowed(string? hostHeader) => hostHeader is { Length: > 0 } h && _hosts.Contains(h);

    /// <summary>Origin must be http:// + an allowed host (no path).</summary>
    public bool IsOriginAllowed(string? origin)
    {
        const string scheme = "http://";
        if (origin is null || !origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        return _hosts.Contains(origin[scheme.Length..]);
    }

    /// <summary>Loopback always; otherwise the selected interface's subnet (or any private range with "todas as redes").</summary>
    public bool IsRemoteAllowed(IPAddress? remote)
    {
        if (remote is null) return true;                      // in-memory test server (no socket)
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        if (_options.AnyAddress) return IsPrivate(remote);
        return _options.Address is not null && (ToUInt(remote) & _mask) == _network;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IEnumerable<string> LocalIPv4Addresses()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            foreach (var u in props.UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork) yield return u.Address.ToString();
        }
    }
}
