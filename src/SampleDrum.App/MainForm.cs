using System.Text;
using SampleDrum.App.Audio;
using SampleDrum.Core;

namespace SampleDrum.App;

/// <summary>
/// Main window (pt-BR). All engine/host control happens on the UI thread; the audio thread only runs
/// <see cref="IBlockProcessor.Process"/>. UI refresh: ~30 Hz timer polling the engine.
/// </summary>
public sealed class MainForm : Form
{
    private const int EngineRate = AsioDuplexHost.RequiredSampleRate;

    private readonly AppSettings _settings;
    private readonly TriggerEngine _engine;
    private IAudioHost? _host;
    private Task? _sampleLoad;
    private bool _sampleLoaded;
    private long _eventCount;
    private DateTime _ledUntil = DateTime.MinValue;
    private bool _ledManual;
    private float _meterDb = LevelMeter.MinDb, _holdDb = LevelMeter.MinDb;
    private DateTime _holdUntil = DateTime.MinValue;

    private readonly ComboBox _cmbDriver = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Button _btnPanel = new() { Text = "Painel ASIO", AutoSize = true };
    private readonly Button _btnRefresh = new() { Text = "Atualizar lista", AutoSize = true };
    private readonly NumericUpDown _nudIn = new() { Minimum = 1, Maximum = ChannelMap.MaxChannels, Width = 50 };
    private readonly NumericUpDown _nudOut = new() { Minimum = 1, Maximum = ChannelMap.MaxChannels, Width = 50 };
    private readonly Label _lblInIdx = Lbl("");
    private readonly Label _lblOutIdx = Lbl("");
    private readonly Button _btnStart = new() { Text = "Iniciar áudio", AutoSize = true };
    private readonly Button _btnStop = new() { Text = "Parar áudio", AutoSize = true, Enabled = false };
    private readonly Label _lblAudio = Lbl("Áudio parado");
    private readonly Button _btnWav = new() { Text = "Escolher WAV...", AutoSize = true };
    private readonly Label _lblWav = Lbl("Nenhum sample carregado");
    private readonly Button _btnTest = new() { Text = "Testar sample", AutoSize = true };
    private readonly Button _btnEnable = new() { Text = "Ativar disparo", AutoSize = true };
    private readonly Label _lblEnabled = Lbl("");
    private readonly LevelMeter _meter = new() { Width = 420 };
    private readonly Panel _led = new() { Width = 22, Height = 22, BackColor = Color.FromArgb(0, 60, 0), Margin = new Padding(8, 3, 3, 3) };
    private readonly Label _lblLevel = Lbl("-∞ dBFS");
    private readonly NumericUpDown _nudThreshold = DbBox(-90, 0);
    private readonly NumericUpDown _nudRearm = DbBox(-120, 0);
    private readonly NumericUpDown _nudInterval = new() { Minimum = 0, Maximum = 5000, Increment = 5, Width = 70 };
    private readonly Label _lblLatency = Lbl("Buffer/latência: áudio parado");

    // "Avançado" (calibration at the rehearsal), collapsible.
    private readonly CheckBox _chkAdvanced = new()
    {
        Text = "Avançado ▸", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(3, 6, 3, 3),
    };
    private readonly FlowLayoutPanel _pnlAdvanced = new()
    {
        FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false,
        Margin = new Padding(0),
    };
    private readonly NumericUpDown _nudHighPass = Box(0, 2000, 0, 5m);
    private readonly NumericUpDown _nudDetect = Box(0, 20, 1, 0.1m);
    private readonly NumericUpDown _nudRearmTime = Box(0, 1000, 0, 1m);
    private readonly NumericUpDown _nudRetrigger = Box(0, 48, 1, 0.5m);
    private readonly NumericUpDown _nudVelRange = Box(1, 120, 1, 1m);
    private readonly NumericUpDown _nudMinGain = Box(-96, 0, 1, 1m);
    private readonly CheckBox _chkFixedVelocity = new()
    {
        Text = "Velocidade fixa (todo disparo a 0 dB)", AutoSize = true, Margin = new Padding(3, 6, 3, 0),
    };
    private readonly Button _btnDefaults = new() { Text = "Restaurar padrões", AutoSize = true };
    private bool _loadingUi;
    private string _lastLatencyText = "";
    private readonly TextBox _txtLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window,
    };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 33 };

    public MainForm()
    {
        Text = "SampleDrum – reforço de bumbo";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 640);
        Size = new Size(880, 860);

        _settings = AppSettings.Load(out var settingsNote);
        _engine = new TriggerEngine(EngineRate, _settings.ToTriggerSettings()); // Core defaults + stored overrides
        _engine.Enabled = false; // always start DISABLED

        BuildLayout();
        LoadSettingsIntoUi();
        WireEvents();
        UpdateEnabledUi();
        UpdateAudioUi();

        Log("SampleDrum iniciado. O disparo automático começa DESATIVADO.");
        Log($"Configurações: {AppSettings.FilePath}");
        if (settingsNote is not null) Log(settingsNote);
        if (!string.IsNullOrEmpty(_settings.SamplePath)) _sampleLoad = LoadSampleAsync(_settings.SamplePath);
        _uiTimer.Start();
    }

    // ---------- layout ----------

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 0) };

    private static NumericUpDown DbBox(int min, int max) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = 1, Increment = 0.5m, Width = 70 };

    private static NumericUpDown Box(int min, int max, int decimals, decimal increment) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = increment, Width = 70 };

    private static void SetNud(NumericUpDown nud, float value) =>
        nud.Value = Math.Clamp((decimal)MathF.Round(value, nud.DecimalPlaces), nud.Minimum, nud.Maximum);

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static GroupBox Group(string title, params Control[] rows)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill,
        };
        flow.Controls.AddRange(rows);
        var box = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        box.Controls.Add(flow);
        return box;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(Group("Áudio",
            Row(Lbl("Driver:"), _cmbDriver, _btnPanel, _btnRefresh),
            Row(Lbl("Entrada USB"), _nudIn, _lblInIdx, Lbl("    Saída USB"), _nudOut, _lblOutIdx),
            Row(_btnStart, _btnStop, _lblAudio),
            Row(_lblLatency)));

        root.Controls.Add(Group("Sample",
            Row(_btnWav, _lblWav),
            Row(_btnTest, Lbl("(toca o sample no retorno mesmo com o disparo desativado)"))));

        root.Controls.Add(Group("Disparo",
            Row(_btnEnable, _lblEnabled),
            Row(Lbl("Entrada:"), _meter, _led, _lblLevel),
            Row(Lbl("Limiar (dBFS)"), _nudThreshold, Lbl("  Rearme (dBFS)"), _nudRearm,
                Lbl("  Intervalo mínimo (ms)"), _nudInterval),
            Row(_chkAdvanced),
            _pnlAdvanced));

        _pnlAdvanced.Controls.AddRange(
        [
            Row(Lbl("Passa-altas no detector (Hz, 0 = desligado)"), _nudHighPass,
                Lbl("  Tempo de detecção (ms)"), _nudDetect),
            Row(Lbl("Tempo de rearme (ms)"), _nudRearmTime,
                Lbl("  Redisparo por subida (dB, 0 = desligado)"), _nudRetrigger),
            Row(Lbl("Faixa de velocidade (dB)"), _nudVelRange, Lbl("  Ganho mínimo (dB)"), _nudMinGain),
            Row(_chkFixedVelocity, _btnDefaults),
        ]);

        var logBox = new GroupBox { Text = "Status / log", Dock = DockStyle.Fill, Padding = new Padding(6) };
        logBox.Controls.Add(_txtLog);
        root.Controls.Add(logBox);

        Controls.Add(root);
    }

    private void LoadSettingsIntoUi()
    {
        PopulateDrivers();
        _nudIn.Value = Math.Clamp(_settings.InputDeskChannel, 1, ChannelMap.MaxChannels);
        _nudOut.Value = Math.Clamp(_settings.OutputDeskChannel, 1, ChannelMap.MaxChannels);
        ShowTriggerSettings(_engine.Settings); // normalized
        _chkAdvanced.Checked = _settings.AdvancedExpanded;
        UpdateAdvancedUi();
        UpdateChannelLabels();
    }

    private void ShowTriggerSettings(TriggerSettings s)
    {
        _loadingUi = true;
        try
        {
            SetNud(_nudThreshold, s.ThresholdDb);
            SetNud(_nudRearm, s.RearmDb);
            SetNud(_nudInterval, s.MinIntervalMs);
            SetNud(_nudHighPass, s.HighPassHz);
            SetNud(_nudDetect, s.DetectTimeMs);
            SetNud(_nudRearmTime, s.RearmTimeMs);
            SetNud(_nudRetrigger, s.RetriggerRiseDb);
            SetNud(_nudVelRange, s.VelocityRangeDb);
            SetNud(_nudMinGain, s.MinGainDb);
            _chkFixedVelocity.Checked = s.FixedVelocity;
        }
        finally { _loadingUi = false; }
        UpdateMeterMarkers();
    }

    private void UpdateAdvancedUi()
    {
        _pnlAdvanced.Visible = _chkAdvanced.Checked;
        _chkAdvanced.Text = _chkAdvanced.Checked ? "Avançado ▾" : "Avançado ▸";
    }

    private void PopulateDrivers()
    {
        string? keep = _cmbDriver.SelectedItem as string
                       ?? (_settings.UseSimulation ? FakeAudioHost.DisplayName : _settings.DriverName);
        var names = AsioDuplexHost.GetDriverNames();
        _cmbDriver.Items.Clear();
        _cmbDriver.Items.Add(FakeAudioHost.DisplayName);
        foreach (var n in names) _cmbDriver.Items.Add(n);
        int idx = keep is null ? -1 : _cmbDriver.Items.IndexOf(keep);
        _cmbDriver.SelectedIndex = idx >= 0 ? idx : 0;
        Log(names.Length == 0
            ? "Nenhum driver ASIO encontrado neste PC; disponível apenas \"Simulação (sem ASIO)\"."
            : $"Drivers ASIO encontrados: {string.Join(", ", names)}");
        if (keep is not null && idx < 0)
            Log($"AVISO: o driver salvo \"{keep}\" não está instalado/visível agora.");
    }

    private void WireEvents()
    {
        _nudIn.ValueChanged += (_, _) => UpdateChannelLabels();
        _nudOut.ValueChanged += (_, _) => UpdateChannelLabels();
        _cmbDriver.SelectedIndexChanged += (_, _) => _btnPanel.Enabled = !IsSimulationSelected;
        _btnRefresh.Click += (_, _) => PopulateDrivers();
        _btnPanel.Click += (_, _) => OpenAsioPanel();
        _btnStart.Click += async (_, _) => await StartAudioAsync();
        _btnStop.Click += (_, _) => StopAudio("Áudio parado pelo usuário.");
        _btnWav.Click += async (_, _) => await ChooseWavAsync();
        _btnTest.Click += (_, _) => TestSample();
        _btnEnable.Click += (_, _) => { _engine.Enabled = !_engine.Enabled; UpdateEnabledUi(); };
        _nudThreshold.ValueChanged += (_, _) => OnThresholdChanged();
        foreach (var nud in new[] { _nudRearm, _nudInterval, _nudHighPass, _nudDetect, _nudRearmTime, _nudRetrigger,
                                    _nudVelRange, _nudMinGain })
            nud.ValueChanged += (_, _) => ApplyTriggerSettings();
        _chkFixedVelocity.CheckedChanged += (_, _) => ApplyTriggerSettings();
        _chkAdvanced.CheckedChanged += (_, _) => UpdateAdvancedUi();
        _btnDefaults.Click += (_, _) => RestoreDefaults();
        _uiTimer.Tick += (_, _) => OnUiTick();
        FormClosing += (_, _) => OnClosing();
    }

    private bool IsSimulationSelected => _cmbDriver.SelectedIndex <= 0;

    private void UpdateChannelLabels()
    {
        _lblInIdx.Text = $"(índice ASIO {ChannelMap.DeskToApiIndex((int)_nudIn.Value)})";
        _lblOutIdx.Text = $"(índice ASIO {ChannelMap.DeskToApiIndex((int)_nudOut.Value)})";
    }

    private void UpdateEnabledUi()
    {
        bool on = _engine.Enabled;
        _btnEnable.Text = on ? "Desativar disparo" : "Ativar disparo";
        _lblEnabled.Text = on ? "Disparo automático ATIVADO" : "Disparo automático DESATIVADO (retorno em silêncio)";
        _lblEnabled.ForeColor = on ? Color.DarkGreen : Color.DarkRed;
    }

    private void UpdateAudioUi()
    {
        bool running = _host?.IsRunning == true;
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _cmbDriver.Enabled = _nudIn.Enabled = _nudOut.Enabled = _btnRefresh.Enabled = !running;
        _btnPanel.Enabled = !IsSimulationSelected;
        if (!running) _lblAudio.Text = "Áudio parado";
    }

    private void UpdateMeterMarkers()
    {
        _meter.ThresholdDb = (float)_nudThreshold.Value;
        _meter.RearmDb = (float)_nudRearm.Value;
    }

    // ---------- actions ----------

    /// <summary>Moving the threshold moves the re-arm level by the same amount (keeps the hysteresis).</summary>
    private void OnThresholdChanged()
    {
        if (_loadingUi) return;
        var moved = _engine.Settings.WithThreshold((float)_nudThreshold.Value);
        _loadingUi = true;
        try { SetNud(_nudRearm, moved.RearmDb); }
        finally { _loadingUi = false; }
        ApplyTriggerSettings();
    }

    private void ApplyTriggerSettings()
    {
        if (_loadingUi) return;
        if (_nudRearm.Value > _nudThreshold.Value)
        {
            _loadingUi = true;
            try { _nudRearm.Value = _nudThreshold.Value; }
            finally { _loadingUi = false; }
        }
        _engine.UpdateSettings(_engine.Settings with
        {
            ThresholdDb = (float)_nudThreshold.Value,
            RearmDb = (float)_nudRearm.Value,
            MinIntervalMs = (float)_nudInterval.Value,
            HighPassHz = (float)_nudHighPass.Value,
            DetectTimeMs = (float)_nudDetect.Value,
            RearmTimeMs = (float)_nudRearmTime.Value,
            RetriggerRiseDb = (float)_nudRetrigger.Value,
            VelocityRangeDb = (float)_nudVelRange.Value,
            MinGainDb = (float)_nudMinGain.Value,
            FixedVelocity = _chkFixedVelocity.Checked,
        });
        UpdateMeterMarkers();
    }

    private void RestoreDefaults()
    {
        var d = TriggerSettings.Default.Normalized();
        ShowTriggerSettings(d);
        _engine.UpdateSettings(d);
        Log($"Padrões do detector restaurados: limiar {d.ThresholdDb:0.0} dBFS, rearme {d.RearmDb:0.0} dBFS, " +
            $"intervalo {d.MinIntervalMs:0} ms, passa-altas {d.HighPassHz:0} Hz, detecção {d.DetectTimeMs:0.0} ms, " +
            $"tempo de rearme {d.RearmTimeMs:0} ms, redisparo {d.RetriggerRiseDb:0.0} dB, faixa {d.VelocityRangeDb:0} dB, " +
            $"ganho mínimo {d.MinGainDb:0} dB, velocidade {(d.FixedVelocity ? "fixa" : "variável")}.");
    }

    private async Task ChooseWavAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Escolher sample de bumbo",
            Filter = "Arquivos WAV (*.wav)|*.wav|Todos os arquivos (*.*)|*.*",
        };
        if (!string.IsNullOrEmpty(_settings.SamplePath))
            dlg.InitialDirectory = Path.GetDirectoryName(_settings.SamplePath);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _sampleLoad = LoadSampleAsync(dlg.FileName);
        await _sampleLoad;
    }

    /// <summary>Reads + converts the WAV to mono float @ 48 kHz off the UI/audio threads, then swaps it into the engine.</summary>
    private async Task LoadSampleAsync(string path)
    {
        _btnWav.Enabled = false;
        _lblWav.Text = $"Carregando {Path.GetFileName(path)}...";
        try
        {
            var (samples, note) = await Task.Run(() =>
            {
                var data = WavFile.ReadMonoAtRate(path, EngineRate, out var n);
                return (data, n);
            });
            _engine.SetSample(samples);
            _sampleLoaded = true;
            _settings.SamplePath = path;
            _lblWav.Text = $"{Path.GetFileName(path)} ({samples.Length * 1000.0 / EngineRate:0} ms @ {EngineRate} Hz)";
            Log($"Sample carregado: {path} ({samples.Length} amostras, {samples.Length * 1000.0 / EngineRate:0} ms).");
            if (note is not null) Log(note);
        }
        catch (Exception ex)
        {
            _lblWav.Text = _sampleLoaded ? _lblWav.Text : "Nenhum sample carregado";
            Log($"ERRO ao carregar o sample \"{path}\": {ex.Message}");
        }
        finally
        {
            _btnWav.Enabled = true;
        }
    }

    private async Task StartAudioAsync()
    {
        if (_host?.IsRunning == true) return;
        _btnStart.Enabled = false;
        try
        {
            if (_sampleLoad is not null) await _sampleLoad; // the sample is ready BEFORE audio starts
            if (!_sampleLoaded) Log("AVISO: nenhum sample carregado; o retorno ficará em silêncio.");

            _host?.Dispose();
            if (IsSimulationSelected)
            {
                _host = new FakeAudioHost(_engine, EngineRate);
            }
            else
            {
                string driver = (string)_cmbDriver.SelectedItem!;
                int inIdx = ChannelMap.DeskToApiIndex((int)_nudIn.Value);
                int outIdx = ChannelMap.DeskToApiIndex((int)_nudOut.Value);
                Log($"Abrindo \"{driver}\": entrada USB {(int)_nudIn.Value} (índice ASIO {inIdx}), " +
                    $"saída USB {(int)_nudOut.Value} (índice ASIO {outIdx})...");
                _host = new AsioDuplexHost(driver, inIdx, outIdx, _engine);
            }
            var host = _host;
            // Faulted may come from a non-UI thread (simulation thread, ThreadPool): always marshal.
            host.Faulted += msg => PostToUi(() => OnHostFaulted(host, msg));
            host.Start();
            Log("Áudio iniciado: " + host.Description);
            UpdateLatencyLabel();
            Log("Para anotar no ensaio: " + _lblLatency.Text);
            SaveSettings();
        }
        catch (AudioHostException ex)
        {
            DisposeHost();
            Log("ERRO: " + ex.Message);
            MessageBox.Show(this, ex.Message, "SampleDrum – não foi possível iniciar o áudio",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            DisposeHost();
            Log($"ERRO inesperado ao iniciar o áudio: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            UpdateAudioUi();
        }
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* window closing: handle already destroyed */ }
    }

    private void OnHostFaulted(IAudioHost host, string message)
    {
        if (!ReferenceEquals(host, _host)) return;
        Log("ERRO: " + message);
        DisposeHost();
        UpdateAudioUi();
    }

    private void StopAudio(string message)
    {
        if (_host is null) return;
        DisposeHost();
        Log(message);
        UpdateAudioUi();
    }

    private void DisposeHost()
    {
        var h = _host;
        _host = null;
        try { h?.Dispose(); } catch (Exception ex) { Log($"AVISO ao fechar o áudio: {ex.Message}"); }
    }

    private void TestSample()
    {
        _engine.RequestTest(1f);
        if (_host?.IsRunning != true) Log("Teste pedido, mas o áudio está parado: clique em \"Iniciar áudio\" para ouvir.");
        else if (!_sampleLoaded) Log("Teste pedido, mas nenhum sample está carregado.");
    }

    private void OpenAsioPanel()
    {
        if (IsSimulationSelected) return;
        string driver = (string)_cmbDriver.SelectedItem!;
        Log($"Abrindo o painel do driver \"{driver}\". Atenção: não altere taxa nem buffer — o OBS usa o mesmo driver.");
        try { AsioDuplexHost.ShowControlPanel(driver, _host); }
        catch (Exception ex) { Log("ERRO ao abrir o painel ASIO: " + ex.Message); }
    }

    // ---------- 30 Hz UI refresh ----------

    private void OnUiTick()
    {
        var now = DateTime.UtcNow;

        float peak = _engine.ReadAndResetInputPeak();
        float db = peak > 1e-6f ? 20f * MathF.Log10(peak) : LevelMeter.MinDb;
        _meterDb = Math.Max(db, _meterDb - 1.5f); // ~45 dB/s fall
        if (db >= _holdDb || now > _holdUntil) { _holdDb = db; _holdUntil = now.AddSeconds(1.5); }
        _meter.LevelDb = _meterDb;
        _meter.PeakHoldDb = _holdDb;
        _lblLevel.Text = _holdDb <= LevelMeter.MinDb ? "-∞ dBFS" : $"{_holdDb:0.0} dBFS";

        StringBuilder? sb = null;
        int drained = 0;
        while (drained < 256 && _engine.TryDequeueEvent(out var e))
        {
            drained++;
            _eventCount++;
            _ledUntil = now.AddMilliseconds(120);
            _ledManual = e.IsManualTest;
            sb ??= new StringBuilder();
            double t = e.SampleIndex / (double)EngineRate;
            if (e.IsManualTest)
                sb.Append($"{Stamp()} Teste manual (t={t:0.000} s, ganho {e.Velocity:0.00})\r\n");
            else
                sb.Append($"{Stamp()} Disparo #{_eventCount}{(e.IsRetrigger ? " (redisparo)" : "")}: t={t:0.000} s, " +
                          $"pico {ToDb(e.Peak):0.0} dBFS, velocidade {e.Velocity:0.00}, ganho {ToDb(e.Gain):0.0} dB\r\n");
        }
        if (sb is not null) AppendLog(sb.ToString());

        bool lit = now < _ledUntil;
        _led.BackColor = lit ? (_ledManual ? Color.Gold : Color.Lime) : Color.FromArgb(0, 60, 0);

        if (_host?.IsRunning == true)
        {
            string extra = _host is FakeAudioHost f ? $", saída {ToDb(f.ReadAndResetOutputPeak()):0} dBFS" : "";
            _lblAudio.Text = $"Rodando: {_host.CallbackCount} blocos de {_host.FramesPerBuffer}, {_eventCount} eventos{extra}";
        }
        UpdateLatencyLabel();
    }

    /// <summary>Buffer size and latencies measured/reported by the running host, for the operator to note.</summary>
    private void UpdateLatencyLabel()
    {
        string text;
        var h = _host;
        if (h is null || !h.IsRunning)
            text = "Buffer/latência: áudio parado";
        else
        {
            double rate = h.SampleRate > 0 ? h.SampleRate : EngineRate;
            double Ms(int samples) => samples * 1000.0 / rate;
            var s = _engine.Settings;
            double detectMs = Math.Max(s.DetectTimeMs, s.ScanMs);
            if (h is FakeAudioHost)
                text = $"Simulação: bloco {h.FramesPerBuffer} amostras = {Ms(h.FramesPerBuffer):0.00} ms @ {rate:0} Hz " +
                       $"(sem driver); detecção +{detectMs:0.0} ms";
            else
            {
                int inL = h.InputLatencySamples, outL = h.OutputLatencySamples;
                text = $"Buffer ASIO {h.FramesPerBuffer} amostras = {Ms(h.FramesPerBuffer):0.00} ms @ {rate:0} Hz | " +
                       $"latência do driver: entrada {inL} ({Ms(inL):0.0} ms), saída {outL} ({Ms(outL):0.0} ms) | " +
                       $"PC (entrada+saída+detecção) ≈ {Ms(inL) + Ms(outL) + detectMs:0.0} ms";
            }
        }
        if (text != _lastLatencyText) { _lastLatencyText = text; _lblLatency.Text = text; }
    }

    private static float ToDb(float linear) => linear > 1e-6f ? 20f * MathF.Log10(linear) : -120f;

    // ---------- log ----------

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    private void Log(string message) => AppendLog($"{Stamp()} {message}\r\n");

    private void AppendLog(string text)
    {
        if (_txtLog.TextLength > 200_000)
        {
            _txtLog.Text = _txtLog.Text[^100_000..];
        }
        _txtLog.AppendText(text);
    }

    // ---------- settings / shutdown ----------

    private void SaveSettings()
    {
        _settings.UseSimulation = IsSimulationSelected;
        if (!IsSimulationSelected) _settings.DriverName = (string)_cmbDriver.SelectedItem!;
        _settings.InputDeskChannel = (int)_nudIn.Value;
        _settings.OutputDeskChannel = (int)_nudOut.Value;
        _settings.FromTriggerSettings(_engine.Settings);
        _settings.AdvancedExpanded = _chkAdvanced.Checked;
        try { _settings.Save(); }
        catch (Exception ex) { Log($"AVISO: não foi possível salvar {AppSettings.FilePath}: {ex.Message}"); }
    }

    private void OnClosing()
    {
        _uiTimer.Stop();
        DisposeHost();
        SaveSettings();
    }
}
