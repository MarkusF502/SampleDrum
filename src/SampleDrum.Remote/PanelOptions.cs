using System.Net;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote;

/// <summary>Where the panel listens. Loopback is always added; 0.0.0.0 only when <see cref="AnyAddress"/> is set.</summary>
public sealed record PanelOptions
{
    /// <summary>Selected LAN IPv4 (one interface). Null = loopback only (tests, or no network).</summary>
    public IPAddress? Address { get; init; }

    /// <summary>Prefix length of <see cref="Address"/>'s subnet: clients outside it are refused (defence in depth).</summary>
    public int PrefixLength { get; init; } = 24;

    public int Port { get; init; } = RemotePanelConfig.DefaultPort;

    /// <summary>Listen on every interface (0.0.0.0). Never the default; only when explicitly chosen by the operator.</summary>
    public bool AnyAddress { get; init; }

    /// <summary>Host name accepted as <c>name.local:port</c> (mDNS); defaults to this machine's name.</summary>
    public string MachineName { get; init; } = Environment.MachineName;
}

/// <summary>
/// App values shown in the phone state that do not come from the controller. The app replaces the whole record on
/// its UI thread (<see cref="PanelServer.AppStatus"/>); web threads only read it.
/// </summary>
public sealed record PanelAppStatus
{
    public string ProfileName { get; init; } = "";
    public bool Simulation { get; init; }
    public bool AudioRunning { get; init; }
    public bool AllowPhoneTest { get; init; }

    /// <summary>Per piece (DrumPiece order): true when the active profile routes it ("ok"), false = "pending".</summary>
    public IReadOnlyList<bool> Routed { get; init; } = [false, false, false, false];
}

/// <summary>What the panel works on. One instance per app run; the controller is the app's single controller.</summary>
public sealed class PanelBackend
{
    /// <summary>The app's single kit controller: every phone change goes through it.</summary>
    public required KitController Controller { get; init; }

    /// <summary>Read-only use: ActiveKit (names/durations), AppliedKitGeneration (confirmation), SampleRate.</summary>
    public required KitEngine Engine { get; init; }

    /// <summary>Presets folder (re-read on each request; can change while the app runs).</summary>
    public required Func<string> PresetsFolder { get; init; }

    /// <summary>Paired phones (hashes only). The app persists it on <see cref="DeviceRegistry.Changed"/>.</summary>
    public required DeviceRegistry Devices { get; init; }

    /// <summary>Meters and hits pushed by the app's single sampler (the hub never reads the engine queues).</summary>
    public PanelTelemetry Telemetry { get; init; } = new();

    /// <summary>Manual test of a piece (default: Engine.RequestTest at gain 1). Only called when allowed.</summary>
    public Action<DrumPiece>? RequestTest { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>The panel could not start (port busy, address not available...). Message in pt-BR for the operator.</summary>
public sealed class PanelStartException(string message, Exception? inner = null) : Exception(message, inner);
