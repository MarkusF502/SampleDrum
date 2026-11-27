using System.Net;
using System.Net.NetworkInformation;

namespace SampleDrum.Remote.Tests;

public class LanInterfacesTests
{
    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection", true)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", true)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData("Ethernet 3", "TAP-Windows Adapter V9", false)]
    [InlineData("NordLynx", "WireGuard Tunnel", false)]
    [InlineData("Tailscale", "Tailscale Tunnel", false)]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", false)]
    [InlineData("Conexão de Rede Bluetooth", "Bluetooth Device (Personal Area Network)", false)]
    public void Adapters_ExcludeVirtualVpnAndBluetooth(string name, string description, bool expected) =>
        Assert.Equal(expected, LanInterfaces.IsCandidateAdapter(OperationalStatus.Up, NetworkInterfaceType.Ethernet, name, description));

    [Fact]
    public void Adapters_MustBeUp_AndNotLoopbackOrTunnel()
    {
        Assert.False(LanInterfaces.IsCandidateAdapter(OperationalStatus.Down, NetworkInterfaceType.Ethernet, "Ethernet", "x"));
        Assert.False(LanInterfaces.IsCandidateAdapter(OperationalStatus.Up, NetworkInterfaceType.Loopback, "Loopback", "x"));
        Assert.False(LanInterfaces.IsCandidateAdapter(OperationalStatus.Up, NetworkInterfaceType.Tunnel, "Teredo", "x"));
    }

    [Theory]
    [InlineData("192.168.0.10", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fe80::1", false)]
    public void Addresses_OnlyPrivateIPv4(string ip, bool expected) =>
        Assert.Equal(expected, LanInterfaces.IsCandidateAddress(IPAddress.Parse(ip)));

    [Fact]
    public void List_OnThisMachine_ReturnsOnlyCandidates() =>
        Assert.All(LanInterfaces.List(), i => Assert.True(LanInterfaces.IsCandidateAddress(i.Address)));
}
