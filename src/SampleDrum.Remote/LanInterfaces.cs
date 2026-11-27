using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SampleDrum.Remote;

/// <summary>A network the panel can listen on (one IPv4 of one adapter).</summary>
public sealed record LanInterface(string Name, string Description, IPAddress Address, int PrefixLength)
{
    public override string ToString() => $"{Name} – {Address}";
}

/// <summary>
/// Candidate LAN interfaces: adapters that are up, with a private IPv4 (10/8, 172.16/12, 192.168/16), excluding
/// loopback, link-local (169.254), virtual switches (vEthernet/WSL/Hyper-V/VirtualBox/VMware/Docker) and VPN/tunnels.
/// </summary>
public static class LanInterfaces
{
    private static readonly string[] Excluded =
    [
        "vethernet", "wsl", "hyper-v", "virtualbox", "vmware", "docker", "vpn", "tap-", "tap ", "wireguard", "tailscale",
        "zerotier", "openvpn", "fortinet", "cisco anyconnect", "globalprotect", "pangp", "hamachi", "npcap", "loopback",
        "bluetooth",
    ];

    public static IReadOnlyList<LanInterface> List()
    {
        var result = new List<LanInterface>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return result; }
        foreach (var nic in nics)
        {
            if (!IsCandidateAdapter(nic.OperationalStatus, nic.NetworkInterfaceType, nic.Name, nic.Description)) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var u in props.UnicastAddresses)
                if (IsCandidateAddress(u.Address))
                    result.Add(new LanInterface(nic.Name, nic.Description, u.Address, u.PrefixLength is > 0 and <= 32 ? u.PrefixLength : 24));
        }
        // Wired first, then by name: a stable order for the selector.
        return result.OrderBy(i => i.Name.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static bool IsCandidateAdapter(OperationalStatus status, NetworkInterfaceType type, string name, string description)
    {
        if (status != OperationalStatus.Up) return false;
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return false;
        string text = (name + " " + description).ToLowerInvariant();
        return !Excluded.Any(text.Contains);
    }

    internal static bool IsCandidateAddress(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && AccessGuard.IsPrivate(ip);
}
