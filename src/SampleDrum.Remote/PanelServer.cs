using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SampleDrum.Remote;

/// <summary>
/// The phone panel web server: Kestrel in-process, bound to the selected LAN IP + loopback only. Start/stop never
/// touches audio or the active kit; stopping affects only remote control. One instance per app run (start/stop many
/// times). Methods are safe from the UI thread (async, no blocking on the controller).
/// </summary>
public sealed class PanelServer : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private PanelRuntime? _runtime;
    private volatile PanelAppStatus _status = new();

    public PanelServer(PanelBackend backend)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Pairing = new PairingService(backend.Time);
    }

    public PanelBackend Backend { get; }

    /// <summary>Pairing code shown on the PC (lives across start/stop).</summary>
    public PairingService Pairing { get; }

    public DeviceRegistry Devices => Backend.Devices;

    /// <summary>True between a successful <see cref="StartAsync"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => _app is not null;

    /// <summary>URL to show/encode in the QR (LAN address, or loopback when no LAN address), or null when stopped.</summary>
    public string? Url { get; private set; }

    public PanelOptions? Options { get; private set; }

    /// <summary>Started or stopped (raised on the caller's thread of Start/Stop).</summary>
    public event EventHandler? RunningChanged;

    /// <summary>pt-BR log lines for the PC log. Never contain tokens, cookies or the pairing code.</summary>
    public event Action<string>? Log;

    /// <summary>App values of the state (profile, audio, routes, phone test). Set on any thread; SSE clients get a new state.</summary>
    public PanelAppStatus AppStatus
    {
        get => _status;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _status = value;
            _runtime?.Hub.BroadcastState();
        }
    }

    /// <summary>Test hook: changes the builder before Build (e.g. UseTestServer).</summary>
    internal Action<WebApplicationBuilder>? ConfigureBuilder { get; set; }

    /// <summary>Running app (tests: GetTestServer()).</summary>
    internal WebApplication? App => _app;

    internal PanelRuntime? Runtime => _runtime;

    /// <summary>
    /// Starts listening. Throws <see cref="PanelStartException"/> (pt-BR message) when the port is busy or the address
    /// is not available; the app keeps running and the operator can pick another port.
    /// </summary>
    public async Task StartAsync(PanelOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is < 1024 or > 65535)
            throw new PanelStartException($"Porta {options.Port} inválida: use um número entre 1024 e 65535.");
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is not null) return;
            var runtime = new PanelRuntime(this, options);
            var app = BuildApp(options, runtime);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                runtime.Dispose();
                try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
                throw Translate(ex, options);
            }
            runtime.Start();
            _runtime = runtime;
            _app = app;
            Options = options;
            Url = $"http://{FormatHost(options.Address is { } a && !IPAddress.IsLoopback(a) ? a.ToString() : "127.0.0.1")}:{options.Port}/";
            Write($"Painel do celular ligado em {Url} (e http://localhost:{options.Port}/ neste PC).");
        }
        finally { _lifecycle.Release(); }
        RaiseRunningChanged();
    }

    /// <summary>Stops the web server and closes every phone connection. Audio and the kit are not affected.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        bool stopped = false;
        try
        {
            var app = _app;
            var runtime = _runtime;
            if (app is null) return;
            _app = null;
            _runtime = null;
            Url = null;
            Options = null;
            runtime?.Dispose();   // closes SSE streams first so Kestrel does not wait for them
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(timeout.Token).ConfigureAwait(false); } catch (Exception ex) { Write($"AVISO ao parar o painel: {ex.Message}"); }
            try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
            Write("Painel do celular desligado (o áudio e o kit continuam).");
            stopped = true;
        }
        finally { _lifecycle.Release(); }
        if (stopped) RaiseRunningChanged();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    internal void Write(string message)
    {
        try { Log?.Invoke(message); } catch { }
    }

    private void RaiseRunningChanged()
    {
        try { RunningChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }

    internal static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

    private WebApplication BuildApp(PanelOptions options, PanelRuntime runtime)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(PanelServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();   // our own sanitized log only (no request/cookie logging)
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = PanelRuntime.MaxBodyBytes;
            k.Limits.MaxConcurrentConnections = 64;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            if (options.AnyAddress) k.Listen(IPAddress.Any, options.Port);
            else
            {
                k.Listen(IPAddress.Loopback, options.Port);
                if (options.Address is { } a && !IPAddress.IsLoopback(a)) k.Listen(a, options.Port);
            }
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = Wire.Json.PropertyNamingPolicy;
            o.SerializerOptions.DefaultIgnoreCondition = Wire.Json.DefaultIgnoreCondition;
        });
        ConfigureBuilder?.Invoke(builder);
        var app = builder.Build();
        runtime.Map(app);
        return app;
    }

    private static PanelStartException Translate(Exception ex, PanelOptions options)
    {
        var inner = ex;
        while (inner is not null and not SocketException and not AddressInUseException && inner.InnerException is not null)
            inner = inner.InnerException;
        if (inner is AddressInUseException || ex is IOException && (inner as SocketException)?.SocketErrorCode == SocketError.AddressAlreadyInUse
            || (inner as SocketException)?.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            return new PanelStartException(
                $"A porta {options.Port} já está em uso por outro programa (ou bloqueada). Escolha outra porta e clique em \"Ativar painel\" de novo. O áudio continua normalmente.", ex);
        if ((inner as SocketException)?.SocketErrorCode == SocketError.AddressNotAvailable)
            return new PanelStartException(
                $"O endereço {options.Address} não existe mais neste PC (a rede mudou?). Escolha a rede de novo na lista.", ex);
        return new PanelStartException($"Não foi possível ligar o painel do celular: {ex.Message}", ex);
    }
}
