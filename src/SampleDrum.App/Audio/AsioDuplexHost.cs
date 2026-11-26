using NAudio.Wave;
using NAudio.Wave.Asio;
using SampleDrum.Core;
using SampleDrum.Core.Kit;

namespace SampleDrum.App.Audio;

/// <summary>
/// ASIO duplex host (NAudio 3.3.0 <see cref="AsioDevice.InitDuplex"/>) for the whole kit: the driver is opened ONCE
/// with every routed piece's input and output channel, all served by the same buffer-switch callback. Pieces without a
/// complete, valid route are not opened: they get silent inputs and their returns are discarded.
/// Never changes the driver's sample rate or buffer size (the OBS shares the X-USB driver).
/// Must be created/started/stopped on the UI (STA) thread: ASIO drivers are COM objects.
/// See docs/asio-naudio.md for the confirmed API.
/// </summary>
public sealed class AsioDuplexHost : IAudioHost
{
    public const int RequiredSampleRate = 48000;

    private readonly string _driverName;
    private readonly ResolvedRouting _routing;
    private readonly IMultiChannelProcessor _processor;

    private AsioDevice? _device;
    private bool _stopRequested;
    private SynchronizationContext? _ownerContext;

    // Audio-thread state: allocated in Start(), only read/written by the callback afterwards.
    private float[][] _inputs = [];
    private float[][] _outputs = [];
    private int[] _routedPieces = [];          // channel position k -> piece index
    private AsioSampleType _inFormat;
    private AsioSampleType _outFormat;
    private int _outBytesPerSample;
    private int _capacity;
    private long _callbackCount;

    public AsioDuplexHost(string driverName, ResolvedRouting routing, IMultiChannelProcessor processor)
    {
        _driverName = driverName ?? throw new ArgumentNullException(nameof(driverName));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public event Action<string>? Faulted;

    public string Description { get; private set; } = "ASIO";
    public bool IsRunning => _device?.State == AsioDeviceState.Running;
    public int FramesPerBuffer { get; private set; }
    public long CallbackCount => Volatile.Read(ref _callbackCount);
    public int SampleRate { get; private set; } = RequiredSampleRate;
    public int InputLatencySamples { get; private set; }
    public int OutputLatencySamples { get; private set; }
    public IMultiChannelProcessor Processor => _processor;
    public ResolvedRouting Routing => _routing;

    /// <summary>Installed ASIO driver names (registry HKLM\SOFTWARE\ASIO). Never throws.</summary>
    public static string[] GetDriverNames()
    {
        try { return AsioDevice.GetDriverNames(); }
        catch { return []; }
    }

    /// <summary>
    /// Input/output channel counts of a driver (opens and closes it; UI thread). Null when it cannot be opened.
    /// Used to validate the routing against what the driver really exposes before starting.
    /// </summary>
    public static (int Inputs, int Outputs)? TryGetChannelCounts(string driverName, IAudioHost? runningHost, out string? error)
    {
        error = null;
        try
        {
            if (runningHost is AsioDuplexHost h && h._device is not null && h._driverName == driverName)
                return (h._device.Capabilities.NbInputChannels, h._device.Capabilities.NbOutputChannels);
            using var device = OpenDevice(driverName);
            return (device.Capabilities.NbInputChannels, device.Capabilities.NbOutputChannels);
        }
        catch (AudioHostException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string DriverListText()
    {
        var names = GetDriverNames();
        return names.Length == 0
            ? "Nenhum driver ASIO instalado neste PC."
            : "Drivers ASIO disponíveis: " + string.Join(", ", names.Select(n => $"\"{n}\""));
    }

    /// <summary>Opens the driver's control panel. Uses the running device when there is one.</summary>
    public static void ShowControlPanel(string driverName, IAudioHost? runningHost)
    {
        if (runningHost is AsioDuplexHost h && h._device is not null && h._driverName == driverName)
        {
            h._device.ShowControlPanel();
            return;
        }
        using var device = OpenDevice(driverName);
        device.ShowControlPanel();
    }

    private static AsioDevice OpenDevice(string driverName)
    {
        var names = GetDriverNames();
        if (names.Length == 0)
            throw new AudioHostException(
                "Nenhum driver ASIO instalado neste PC. Instale o driver X-USB da Behringer " +
                "ou use \"Simulação (sem ASIO)\".");
        if (!names.Contains(driverName))
            throw new AudioHostException($"Driver ASIO \"{driverName}\" não encontrado. {DriverListText()}");
        try
        {
            return AsioDevice.Open(driverName);
        }
        catch (Exception ex)
        {
            throw new AudioHostException(
                $"Não foi possível abrir o driver ASIO \"{driverName}\": {ex.Message}\r\n" +
                "Verifique: o X-USB está conectado (cabo USB e mesa ligada)? Outro programa está usando o driver " +
                $"de forma exclusiva? {DriverListText()}", ex);
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        DisposeDevice();
        _stopRequested = false;
        if (_routing.RoutedPieces.Count == 0)
            throw new AudioHostException(
                $"Nenhuma peça tem entrada e saída definidas no perfil \"{_routing.ProfileName}\": nada para abrir no ASIO.");
        // Start() runs on the UI thread; device events are funnelled back to it (see OnDeviceStopped).
        _ownerContext = SynchronizationContext.Current;

        var device = OpenDevice(_driverName);
        try
        {
            Configure(device);
            _device = device;
            device.Stopped += OnDeviceStopped;
            device.DriverResetRequest += OnDriverResetRequest;
            device.LatenciesChanged += OnLatenciesChanged;
            try
            {
                device.Start();
            }
            catch (Exception ex)
            {
                throw new AudioHostException(
                    $"O driver \"{_driverName}\" foi aberto mas não iniciou: {ex.Message}\r\n" +
                    "Verifique se o X-USB está conectado e se nenhum outro programa bloqueia o driver.", ex);
            }
        }
        catch
        {
            _device = null;
            device.Stopped -= OnDeviceStopped;
            device.DriverResetRequest -= OnDriverResetRequest;
            device.LatenciesChanged -= OnLatenciesChanged;
            try { device.Dispose(); } catch { /* best effort */ }
            throw;
        }
    }

    private void Configure(AsioDevice device)
    {
        var caps = device.Capabilities;
        if (caps.NbInputChannels == 0 || caps.NbOutputChannels == 0)
            throw new AudioHostException(
                $"O driver \"{_driverName}\" informa {caps.NbInputChannels} entradas e {caps.NbOutputChannels} saídas. " +
                "O X-USB provavelmente está desconectado ou a mesa está desligada.");

        // Sample rate: refuse, never change it (OBS shares the driver).
        int rate = device.CurrentSampleRate;
        if (rate != RequiredSampleRate)
            throw new AudioHostException(
                $"O driver \"{_driverName}\" está em {rate} Hz, mas o SampleDrum exige {RequiredSampleRate} Hz. " +
                "O SampleDrum não altera a taxa do driver (o OBS usa o mesmo X-USB): ajuste a taxa na mesa/X-USB " +
                "e inicie de novo.");

        var pieces = _routing.RoutedPieces;
        var ins = pieces.Select(p => _routing.InputIndex(p)!.Value).ToArray();
        var outs = pieces.Select(p => _routing.OutputIndex(p)!.Value).ToArray();
        if (ins.Distinct().Count() != ins.Length || outs.Distinct().Count() != outs.Length)
            throw new AudioHostException("Roteamento inválido: duas peças no mesmo canal (corrija o perfil).");

        for (int k = 0; k < pieces.Count; k++)
        {
            var p = pieces[k];
            if (ins[k] >= caps.NbInputChannels)
                throw new AudioHostException(
                    $"{p.Label()}: entrada USB {ins[k] + 1} (índice ASIO {ins[k]}) não existe: o driver \"{_driverName}\" " +
                    $"tem {caps.NbInputChannels} entradas (USB 1..{caps.NbInputChannels}).");
            if (outs[k] >= caps.NbOutputChannels)
                throw new AudioHostException(
                    $"{p.Label()}: saída USB {outs[k] + 1} (índice ASIO {outs[k]}) não existe: o driver \"{_driverName}\" " +
                    $"tem {caps.NbOutputChannels} saídas (USB 1..{caps.NbOutputChannels}).");
        }

        _inFormat = SingleFormat(ins.Select(i => caps.InputChannelInfos[i].type), "entradas");
        _outFormat = SingleFormat(outs.Select(o => caps.OutputChannelInfos[o].type), "saídas");
        _outBytesPerSample = NativeSampleConverter.BytesPerSample(_outFormat);

        try
        {
            device.InitDuplex(new AsioDuplexOptions
            {
                InputChannels = ins,
                OutputChannels = outs,
                SampleRate = null,   // null = keep the driver's current rate; NAudio then never calls SetSampleRate
                BufferSize = null,   // null = driver's preferred buffer size (unchanged)
                Processor = OnProcess,
            });
        }
        catch (Exception ex)
        {
            throw new AudioHostException($"Falha ao configurar o driver \"{_driverName}\" em duplex: {ex.Message}", ex);
        }

        // Allocate the callback buffers once (generous margin in case a driver hands a bigger block). Unrouted pieces'
        // input arrays are never written, so they stay silent.
        FramesPerBuffer = device.FramesPerBuffer;
        SampleRate = rate;
        _capacity = Math.Max(FramesPerBuffer, caps.BufferMaxSize);
        _inputs = DrumPieces.All.Select(_ => new float[_capacity]).ToArray();
        _outputs = DrumPieces.All.Select(_ => new float[_capacity]).ToArray();
        _routedPieces = pieces.Select(p => (int)p).ToArray();
        Volatile.Write(ref _callbackCount, 0);
        InputLatencySamples = device.InputLatencySamples;
        OutputLatencySamples = device.OutputLatencySamples;

        var routes = pieces.Select((p, k) =>
            $"{p.Label()}: entrada USB {ins[k] + 1} \"{caps.InputChannelInfos[ins[k]].name}\" → " +
            $"saída USB {outs[k] + 1} \"{caps.OutputChannelInfos[outs[k]].name}\"");
        var skipped = DrumPieces.All.Where(p => !_routing.IsRouted(p)).Select(p => p.Label()).ToArray();
        Description =
            $"ASIO \"{_driverName}\" {rate} Hz, buffer {FramesPerBuffer} amostras ({FramesPerBuffer * 1000.0 / rate:0.0} ms), " +
            $"formato {_inFormat}/{_outFormat}; {string.Join("; ", routes)}" +
            (skipped.Length > 0 ? $"; sem rota (não abertas): {string.Join(", ", skipped)}" : "") +
            $"; latência do driver: entrada {InputLatencySamples}, saída {OutputLatencySamples} amostras";
    }

    private AsioSampleType SingleFormat(IEnumerable<AsioSampleType> types, string side)
    {
        var distinct = types.Distinct().ToArray();
        if (distinct.Length != 1)
            throw new AudioHostException(
                $"As {side} escolhidas usam formatos nativos diferentes ({string.Join(", ", distinct)}); " +
                "o NAudio exige um formato único por lado.");
        if (!NativeSampleConverter.IsSupported(distinct[0]))
            throw new AudioHostException(
                $"Formato nativo {distinct[0]} das {side} não é suportado (suportados: Int32LSB, Float32LSB, Int16LSB, Int24LSB).");
        return distinct[0];
    }

    /// <summary>ASIO real-time thread. No allocations, locks, logging, I/O or UI calls.</summary>
    private void OnProcess(in AsioProcessBuffers buffers)
    {
        int frames = buffers.Frames;
        int n = frames <= _capacity ? frames : _capacity;
        var routed = _routedPieces;

        for (int k = 0; k < routed.Length; k++)
            NativeSampleConverter.ToFloat(buffers.RawInput(k).Bytes, _inFormat, _inputs[routed[k]].AsSpan(0, n));
        for (int p = 0; p < _outputs.Length; p++) _outputs[p].AsSpan(0, n).Clear();

        _processor.Process(_inputs, _outputs, n);

        // Writing through RawOutput makes NAudio skip its own float->native conversion for this channel,
        // so every frame of the native buffer must be written here.
        for (int k = 0; k < routed.Length; k++)
        {
            var rawOut = buffers.RawOutput(k).Bytes;
            NativeSampleConverter.FromFloat(_outputs[routed[k]].AsSpan(0, n), _outFormat, rawOut);
            if (n < frames) rawOut.Slice(n * _outBytesPerSample).Clear();
        }

        Volatile.Write(ref _callbackCount, _callbackCount + 1);
    }

    // Threading (verified in NAudio.Asio 3.3.0 AsioDevice.DispatchEvent/RaiseStopped): Stopped and DriverResetRequest
    // are never invoked synchronously on the ASIO driver thread; NAudio Posts them to the SynchronizationContext
    // captured in AsioDevice.Open, or queues them on the ThreadPool when there is none. We still re-post to the
    // context captured in Start() so that stopping/disposing the device and raising Faulted always happen on the
    // owner (UI) thread, regardless of where Open ran, and never inside the buffer-switch callback.
    private void OnDeviceStopped(object? sender, StoppedEventArgs e)
    {
        var device = sender as AsioDevice;
        string reason = e.Exception is null ? "o driver parou" : e.Exception.Message;
        RunOnOwner(() =>
        {
            if (_stopRequested || device is null || !ReferenceEquals(device, _device)) return;
            DisposeDevice();
            Faulted?.Invoke($"O áudio ASIO parou inesperadamente: {reason}");
        });
    }

    private void OnDriverResetRequest(object? sender, EventArgs e)
    {
        var device = sender as AsioDevice;
        RunOnOwner(() =>
        {
            if (_stopRequested || device is null || !ReferenceEquals(device, _device)) return;
            // Someone changed the rate/buffer (e.g. in the ASIO panel). Stop rather than re-negotiate:
            // the user restarts, and Start() re-checks the 48 kHz requirement.
            Stop();
            Faulted?.Invoke(
                "O driver ASIO pediu reinicialização (taxa ou buffer alterado no painel). O áudio foi parado; " +
                "clique em \"Iniciar áudio\" para abrir de novo.");
        });
    }

    private void OnLatenciesChanged(object? sender, EventArgs e)
    {
        var device = sender as AsioDevice;
        RunOnOwner(() =>
        {
            if (device is null || !ReferenceEquals(device, _device)) return;
            try
            {
                InputLatencySamples = device.InputLatencySamples;
                OutputLatencySamples = device.OutputLatencySamples;
            }
            catch { /* device going away */ }
        });
    }

    private void RunOnOwner(Action action)
    {
        var ctx = _ownerContext;
        if (ctx is not null) ctx.Post(_ => action(), null);
        else ThreadPool.QueueUserWorkItem(_ => action()); // headless: still off the ASIO callback thread
    }

    /// <summary>Stops; NAudio's Dispose waits for a running callback to finish, so the processor may be reset after.</summary>
    public void Stop()
    {
        _stopRequested = true;
        DisposeDevice();
    }

    private void DisposeDevice()
    {
        var device = _device;
        _device = null;
        if (device is null) return;
        device.Stopped -= OnDeviceStopped;
        device.DriverResetRequest -= OnDriverResetRequest;
        device.LatenciesChanged -= OnLatenciesChanged;
        try { if (device.State == AsioDeviceState.Running) device.Stop(); } catch { /* best effort */ }
        try { device.Dispose(); } catch { /* best effort */ }
    }

    public void Dispose() => Stop();
}
