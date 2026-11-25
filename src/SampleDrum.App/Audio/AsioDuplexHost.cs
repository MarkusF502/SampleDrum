using NAudio.Wave;
using NAudio.Wave.Asio;
using SampleDrum.Core;

namespace SampleDrum.App.Audio;

/// <summary>
/// ASIO duplex host (NAudio 3.3.0 <see cref="AsioDevice.InitDuplex"/>): one input channel in, one output channel out,
/// same buffer-switch callback. Never changes the driver's sample rate or buffer size (the OBS shares the X-USB driver).
/// Must be created/started/stopped on the UI (STA) thread: ASIO drivers are COM objects.
/// See docs/asio-naudio.md for the confirmed API.
/// </summary>
public sealed class AsioDuplexHost : IAudioHost
{
    public const int RequiredSampleRate = 48000;

    private readonly string _driverName;
    private readonly int _inputApiIndex;
    private readonly int _outputApiIndex;
    private readonly IBlockProcessor _processor;

    private AsioDevice? _device;
    private bool _stopRequested;
    private SynchronizationContext? _ownerContext;

    // Audio-thread state: allocated in Start(), only read/written by the callback afterwards.
    private float[] _inBuffer = [];
    private float[] _outBuffer = [];
    private AsioSampleType _inFormat;
    private AsioSampleType _outFormat;
    private int _outBytesPerSample;
    private long _callbackCount;

    public AsioDuplexHost(string driverName, int inputApiIndex, int outputApiIndex, IBlockProcessor processor)
    {
        _driverName = driverName ?? throw new ArgumentNullException(nameof(driverName));
        _inputApiIndex = inputApiIndex;
        _outputApiIndex = outputApiIndex;
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

    /// <summary>Installed ASIO driver names (registry HKLM\SOFTWARE\ASIO). Never throws.</summary>
    public static string[] GetDriverNames()
    {
        try { return AsioDevice.GetDriverNames(); }
        catch { return []; }
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
        int inDesk = _inputApiIndex + 1, outDesk = _outputApiIndex + 1;

        if (caps.NbInputChannels == 0 || caps.NbOutputChannels == 0)
            throw new AudioHostException(
                $"O driver \"{_driverName}\" informa {caps.NbInputChannels} entradas e {caps.NbOutputChannels} saídas. " +
                "O X-USB provavelmente está desconectado ou a mesa está desligada.");

        if (_inputApiIndex < 0 || _inputApiIndex >= caps.NbInputChannels)
            throw new AudioHostException(
                $"Entrada USB {inDesk} (índice ASIO {_inputApiIndex}) não existe: o driver \"{_driverName}\" tem " +
                $"{caps.NbInputChannels} entradas (USB 1..{caps.NbInputChannels}).");
        if (_outputApiIndex < 0 || _outputApiIndex >= caps.NbOutputChannels)
            throw new AudioHostException(
                $"Saída USB {outDesk} (índice ASIO {_outputApiIndex}) não existe: o driver \"{_driverName}\" tem " +
                $"{caps.NbOutputChannels} saídas (USB 1..{caps.NbOutputChannels}).");

        // Sample rate: refuse, never change it (OBS shares the driver).
        int rate = device.CurrentSampleRate;
        if (rate != RequiredSampleRate)
            throw new AudioHostException(
                $"O driver \"{_driverName}\" está em {rate} Hz, mas o SampleDrum exige {RequiredSampleRate} Hz. " +
                "O SampleDrum não altera a taxa do driver (o OBS usa o mesmo X-USB): ajuste a taxa na mesa/X-USB " +
                "e inicie de novo.");

        _inFormat = caps.InputChannelInfos[_inputApiIndex].type;
        _outFormat = caps.OutputChannelInfos[_outputApiIndex].type;
        if (!NativeSampleConverter.IsSupported(_inFormat))
            throw new AudioHostException(
                $"Formato nativo {_inFormat} da entrada USB {inDesk} não é suportado " +
                "(suportados: Int32LSB, Float32LSB, Int16LSB, Int24LSB).");
        if (!NativeSampleConverter.IsSupported(_outFormat))
            throw new AudioHostException(
                $"Formato nativo {_outFormat} da saída USB {outDesk} não é suportado " +
                "(suportados: Int32LSB, Float32LSB, Int16LSB, Int24LSB).");
        _outBytesPerSample = NativeSampleConverter.BytesPerSample(_outFormat);

        try
        {
            device.InitDuplex(new AsioDuplexOptions
            {
                InputChannels = [_inputApiIndex],
                OutputChannels = [_outputApiIndex],
                SampleRate = null,   // null = keep the driver's current rate; NAudio then never calls SetSampleRate
                BufferSize = null,   // null = driver's preferred buffer size (unchanged)
                Processor = OnProcess,
            });
        }
        catch (Exception ex)
        {
            throw new AudioHostException($"Falha ao configurar o driver \"{_driverName}\" em duplex: {ex.Message}", ex);
        }

        // Allocate the callback buffers once, at start (generous margin in case a driver hands a bigger block).
        FramesPerBuffer = device.FramesPerBuffer;
        SampleRate = rate;
        int capacity = Math.Max(FramesPerBuffer, caps.BufferMaxSize);
        _inBuffer = new float[capacity];
        _outBuffer = new float[capacity];
        Volatile.Write(ref _callbackCount, 0);
        InputLatencySamples = device.InputLatencySamples;
        OutputLatencySamples = device.OutputLatencySamples;

        string inName = caps.InputChannelInfos[_inputApiIndex].name;
        string outName = caps.OutputChannelInfos[_outputApiIndex].name;
        Description =
            $"ASIO \"{_driverName}\" {rate} Hz, buffer {FramesPerBuffer} amostras " +
            $"({FramesPerBuffer * 1000.0 / rate:0.0} ms); entrada USB {inDesk} \"{inName}\" ({_inFormat}), " +
            $"saída USB {outDesk} \"{outName}\" ({_outFormat}); latência do driver: entrada {InputLatencySamples}, " +
            $"saída {OutputLatencySamples} amostras";
    }

    /// <summary>
    /// ASIO real-time thread. No allocations, locks, logging, I/O or UI calls.
    /// </summary>
    private void OnProcess(in AsioProcessBuffers buffers)
    {
        int frames = buffers.Frames;
        int n = frames <= _inBuffer.Length ? frames : _inBuffer.Length;
        var input = _inBuffer.AsSpan(0, n);
        var output = _outBuffer.AsSpan(0, n);

        NativeSampleConverter.ToFloat(buffers.RawInput(0).Bytes, _inFormat, input);
        output.Clear();
        _processor.Process(input, output);

        // Writing through RawOutput makes NAudio skip its own float->native conversion for this channel,
        // so every frame of the native buffer must be written here.
        var rawOut = buffers.RawOutput(0).Bytes;
        NativeSampleConverter.FromFloat(output, _outFormat, rawOut);
        if (n < frames) rawOut.Slice(n * _outBytesPerSample).Clear();

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
