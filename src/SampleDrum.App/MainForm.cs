using System.Text;
using SampleDrum.App.Audio;
using SampleDrum.App.Harness;
using SampleDrum.Core;
using SampleDrum.Core.Kit;
using SampleDrum.Remote;

namespace SampleDrum.App;

/// <summary>
/// Main window (pt-BR) for the four-piece kit. All engine/host control happens on the UI thread; the audio thread only
/// runs <see cref="IMultiChannelProcessor.Process"/>. UI refresh and logging: ~30 Hz timer on the UI thread.
/// </summary>
public sealed class MainForm : Form
{
    private const int EngineRate = AsioDuplexHost.RequiredSampleRate;

    private readonly AppConfig _cfg;
    private readonly KitEngine _kit;
    private readonly PiecePanel[] _panels = new PiecePanel[DrumPieces.Count];
    private readonly SimulationPanel _sim = new();
    private IAudioHost? _host;
    private bool _hostStopFailed;      // last host Dispose threw: its audio thread may still be running
    private readonly KitController _controller; // ONE for the whole run: single path for kit changes
    private bool _kitWanted;           // last requested kit master state (button toggles this)
    private bool _syntheticActive;     // the synthetic simulation kit is loaded in the engine (never persisted)
    private PreparedKit? _operatorKit; // operator's kit saved when the synthetic kit was loaded
    private readonly bool _savedUseSimulation;     // persisted values a --simulation run must not overwrite
    private readonly string _savedProfileName;
    private readonly string? _savedDriverName;
    private Task? _initialLoad;
    private long _eventCount;
    private string _lastLatencyText = "";

    // Phone panel: one server per run over the same controller; the UI timer is the single engine sampler and fans
    // meters/hits out to the web hub through _telemetry (the hub never reads the engine queues).
    private readonly PanelTelemetry _telemetry = new();
    private readonly DeviceRegistry _devices;
    private readonly PanelServer _panelServer;
    private readonly RemotePanelControl _remote;
    private readonly SimulationLibrary? _simLibrary;   // --simulation: demo samples/presets in a temp folder
    private readonly AppConfig? _startupCfg;           // --simulation: what the church config held at start
    private int _stateChangedPosted;                   // coalesces controller StateChanged into one UI refresh
    private long _shownVersion;
    private long _shownKitVersion;
    private long _persistBaseline = long.MaxValue;     // controller Version after the startup restore
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 500 };

    private readonly ComboBox _cmbDriver = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Button _btnPanel = new() { Text = "Painel ASIO", AutoSize = true };
    private readonly Button _btnRefresh = new() { Text = "Atualizar lista", AutoSize = true };
    private readonly ComboBox _cmbProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Button _btnEditRoutes = new() { Text = "Editar rotas...", AutoSize = true };
    private readonly Label _lblRouting = PiecePanel.Lbl("");
    private readonly ListBox _lstIssues = new() { Width = 860, Height = 64, IntegralHeight = false };
    private readonly Button _btnStart = new() { Text = "Iniciar áudio", AutoSize = true };
    private readonly Button _btnStop = new() { Text = "Parar áudio", AutoSize = true, Enabled = false };
    private readonly Label _lblAudio = PiecePanel.Lbl("Áudio parado");
    private readonly Label _lblLatency = PiecePanel.Lbl("Buffer/latência: áudio parado");

    private readonly Button _btnKit = new() { Text = "Ativar kit", AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 11f, FontStyle.Bold) };
    private readonly Label _lblKit = PiecePanel.Lbl("");
    private readonly ComboBox _cmbPresets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Button _btnLoadPreset = new() { Text = "Aplicar preset", AutoSize = true };
    private readonly Button _btnSavePreset = new() { Text = "Salvar preset...", AutoSize = true };
    private readonly Button _btnPresetFile = new() { Text = "Abrir arquivo...", AutoSize = true };
    private readonly Label _lblSamples = PiecePanel.Lbl("");
    private readonly Button _btnSamplesFolder = new() { Text = "Pasta de samples...", AutoSize = true };

    private readonly TextBox _txtLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window,
        AccessibleName = "Registro",   // own name for screen readers (otherwise borrowed from a nearby label)
    };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 33 };

    public MainForm()
    {
        Text = "SampleDrum – reforço de bateria (bumbo, caixa, tom, surdo)";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 700);
        Size = new Size(1060, 980);

        _cfg = AppConfig.Load(out var cfgNote);
        _savedUseSimulation = _cfg.UseSimulation;
        _savedProfileName = _cfg.ActiveProfileName;
        _savedDriverName = _cfg.DriverName;
        string? simNote = null;
        if (StartupOptions.SimulationMode)
        {
            _startupCfg = AppConfig.Parse(_cfg.ToJson(), out _);
            try { _simLibrary = SimulationLibrary.Create(StartupOptions.SimulationKit()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                simNote = $"AVISO: não foi possível criar a biblioteca de demonstração da simulação ({ex.Message}).";
            }
            // --simulation: Simulação profile + simulation host + synthetic kit (no ASIO driver is touched).
            _cfg.UseSimulation = true;
            _cfg.ActiveProfileName = RoutingProfiles.SimulationName;
        }
        _kit = new KitEngine(EngineRate, _cfg.ToKitSettings()) { KitEnabled = false }; // always start DISABLED
        foreach (var p in DrumPieces.All) _kit.SetPieceEnabled(p, _cfg[p].Enabled);
        _kit.ApplyKit(PreparedKit.Empty(EngineRate));
        _controller = new KitController(_kit, new SampleLibrary(_simLibrary?.SamplesFolder ?? _cfg.EffectiveSamplesFolder));
        _shownVersion = _controller.Version;
        _shownKitVersion = _controller.Snapshot.KitVersion;

        // --simulation never reads or writes the operator's paired phones / panel settings.
        _devices = new DeviceRegistry(StartupOptions.SimulationMode ? null : _cfg.RemotePanel.PairedDevices);
        _panelServer = new PanelServer(new PanelBackend
        {
            Controller = _controller, Engine = _kit, Devices = _devices, Telemetry = _telemetry,
            PresetsFolder = () => PresetsFolder,
        });
        _remote = new RemotePanelControl(_panelServer,
            StartupOptions.SimulationMode ? new RemotePanelConfig() : _cfg.RemotePanel, StartupOptions.SimulationMode);

        BuildLayout();
        LoadConfigIntoUi();
        WireEvents();
        UpdateKitUi();
        UpdateAudioUi();

        Log("SampleDrum iniciado. O kit começa DESATIVADO.");
        Log($"Configurações: {AppConfig.DefaultFilePath}");
        if (cfgNote is not null) Log(cfgNote);
        if (simNote is not null) Log(simNote);
        if (StartupOptions.SimulationMode)
        {
            Log("Modo --simulation: perfil \"Simulação\", host de simulação e kit sintético.");
            if (_simLibrary is not null)
                Log($"Simulação: biblioteca de demonstração em {_simLibrary.Root} (samples e presets de teste; nada disso vai " +
                    "para a configuração da igreja).");
            _initialLoad = LoadSyntheticKitAsync(StartupOptions.SimulationSeed);
        }
        else
        {
            _initialLoad = LoadConfiguredSamplesAsync();
        }
        // Changes that do not come from this window (the phone) are shown and saved only after the startup restore.
        _initialLoad.ContinueWith(_ => PostToUi(() =>
        {
            _persistBaseline = _controller.Version;
            OnControllerStateChanged();
        }), TaskScheduler.Default);
        _uiTimer.Start();
    }

    /// <summary>Samples folder in use (the demo library in --simulation).</summary>
    private string SamplesFolder => _simLibrary?.SamplesFolder ?? _cfg.EffectiveSamplesFolder;

    /// <summary>Presets folder in use (the demo presets in --simulation). Read by web threads too.</summary>
    private string PresetsFolder => _simLibrary?.PresetsFolder ?? _cfg.EffectivePresetsFolder;


    // ---------- layout ----------

    private static GroupBox Group(string title, params Control[] rows)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.AddRange(rows);
        var box = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8, 4, 8, 4) };
        box.Controls.Add(flow);
        return box;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 26));

        var content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill,
        };
        content.Controls.Add(Group("Áudio",
            PiecePanel.Row(PiecePanel.Lbl("Driver:"), _cmbDriver, _btnPanel, _btnRefresh),
            PiecePanel.Row(PiecePanel.Lbl("Perfil de rotas:"), _cmbProfile, _btnEditRoutes, _lblRouting),
            _lstIssues,
            PiecePanel.Row(_btnStart, _btnStop, _lblAudio),
            PiecePanel.Row(_lblLatency)));
        content.Controls.Add(_sim);
        content.Controls.Add(Group("Kit",
            PiecePanel.Row(_btnKit, _lblKit),
            PiecePanel.Row(PiecePanel.Lbl("Preset:"), _cmbPresets, _btnLoadPreset, _btnPresetFile, _btnSavePreset),
            PiecePanel.Row(PiecePanel.Lbl("Samples:"), _lblSamples, _btnSamplesFolder)));
        content.Controls.Add(_remote);
        foreach (var p in DrumPieces.All)
        {
            var panel = new PiecePanel(p) { Dock = DockStyle.None };
            _panels[(int)p] = panel;
            content.Controls.Add(panel);
        }
        root.Controls.Add(content, 0, 0);

        var logBox = new GroupBox { Text = "Status / log", Dock = DockStyle.Fill, Padding = new Padding(6) };
        logBox.Controls.Add(_txtLog);
        root.Controls.Add(logBox, 0, 1);
        Controls.Add(root);
    }

    private void LoadConfigIntoUi()
    {
        PopulateDrivers();
        PopulateProfiles();
        var settings = _kit.Settings;
        foreach (var p in DrumPieces.All)
        {
            var panel = _panels[(int)p];
            panel.ShowSettings(settings[p]);
            panel.SetPieceEnabled(_cfg[p].Enabled);
            panel.AdvancedExpanded = _cfg[p].AdvancedExpanded;
            panel.SetSampleText(_cfg[p].SamplePath is null ? "nenhum sample" : $"carregando {Path.GetFileName(_cfg[p].SamplePath)}...");
        }
        _lblSamples.Text = SamplesFolder;
        RefreshPresetList();
        RefreshRouting();
    }

    private void PopulateDrivers()
    {
        string? keep = _cmbDriver.SelectedItem as string ?? (_cfg.UseSimulation ? FakeAudioHost.DisplayName : _cfg.DriverName);
        var names = AsioDuplexHost.GetDriverNames();
        _cmbDriver.Items.Clear();
        _cmbDriver.Items.Add(FakeAudioHost.DisplayName);
        foreach (var n in names) _cmbDriver.Items.Add(n);
        int idx = keep is null ? -1 : _cmbDriver.Items.IndexOf(keep);
        _cmbDriver.SelectedIndex = idx >= 0 ? idx : 0;
        Log(names.Length == 0
            ? "Nenhum driver ASIO encontrado neste PC; disponível apenas \"Simulação (sem ASIO)\"."
            : $"Drivers ASIO encontrados: {string.Join(", ", names)}");
        if (keep is not null && idx < 0) Log($"AVISO: o driver salvo \"{keep}\" não está instalado/visível agora.");
    }

    private void PopulateProfiles()
    {
        _cmbProfile.Items.Clear();
        foreach (var prof in _cfg.Profiles) _cmbProfile.Items.Add(prof.Name);
        _cmbProfile.SelectedItem = _cfg.ActiveProfile.Name;
    }

    private void WireEvents()
    {
        _cmbDriver.SelectedIndexChanged += async (_, _) =>
        {
            UpdateAudioUi();
            RefreshRouting();
            if (!IsSimulationSelected) await LeaveSyntheticAsync("driver ASIO escolhido");
        };
        _sim.PreviewChanged += (_, _) =>
        {
            if (_host is FakeAudioHost f)
            {
                f.Preview = _sim.Preview;
                f.PreviewEnabled = _sim.PreviewEnabled;
            }
        };
        _btnRefresh.Click += (_, _) => PopulateDrivers();
        _btnPanel.Click += (_, _) => OpenAsioPanel();
        _cmbProfile.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbProfile.SelectedItem is string name && name != _cfg.ActiveProfileName)
            {
                _cfg.ActiveProfileName = name;
                Log($"Perfil de rotas: {name}.");
                SaveConfig();
            }
            RefreshRouting();
        };
        _btnEditRoutes.Click += (_, _) => EditRoutes();
        _btnStart.Click += async (_, _) => await StartAudioAsync();
        _btnStop.Click += (_, _) => StopAudio("Áudio parado pelo usuário.");
        _btnKit.Click += async (_, _) =>
        {
            _kitWanted = !_kitWanted;
            var result = await _controller.SetKitEnabled(_kitWanted).Completion;
            if (result.Status != KitOperationStatus.Applied) return;   // superseded by a newer click
            Log(_kit.KitEnabled ? "Kit ATIVADO." : "Kit DESATIVADO (retornos em silêncio; teste manual continua funcionando).");
            UpdateKitUi();
        };
        _btnLoadPreset.Click += async (_, _) =>
        {
            if (_cmbPresets.SelectedItem is PresetItem item) await ApplyPresetAsync(item.Preset);
        };
        _btnPresetFile.Click += async (_, _) => await OpenPresetFileAsync();
        _btnSavePreset.Click += (_, _) => SavePreset();
        _btnSamplesFolder.Click += async (_, _) => await ChooseSamplesFolderAsync();

        foreach (var panel in _panels)
        {
            var p = panel.Piece;
            panel.SettingsChanged += (_, s) =>
            {
                _controller.SetPieceSettings(p, s);
                // Tweaks of the synthetic simulation kit are never written over the operator's calibration.
                if (!_syntheticActive) _cfg.SetPieceSettings(p, s);
            };
            panel.PieceEnabledChanged += (_, on) =>
            {
                _controller.SetPieceEnabled(p, on);
                _cfg[p].Enabled = on;
                Log($"{p.Label()}: {(on ? "ativa" : "desativada (retorno em silêncio)")}.");
                SaveConfig();
            };
            panel.TestRequested += (_, _) => TestPiece(p);
            panel.ChooseWavRequested += async (_, _) => await ChooseWavAsync(p);
            panel.ClearSampleRequested += async (_, _) => await SetPieceSampleAsync(p, null);
        }
        _sim.SyntheticKitRequested += async (_, seed) => await LoadSyntheticKitAsync(seed);
        _uiTimer.Tick += (_, _) => OnUiTick();
        FormClosing += (_, _) => OnClosing();

        // Phone panel.
        _controller.StateChanged += (_, _) =>
        {
            // Controller worker thread: never wait for the UI; one pending refresh at a time.
            if (Interlocked.Exchange(ref _stateChangedPosted, 1) == 0) PostToUi(OnControllerStateChanged);
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveConfig(); };
        _panelServer.Log += msg => PostToUi(() => Log(msg));
        _devices.Changed += (_, _) => PostToUi(() =>
        {
            if (StartupOptions.SimulationMode) return;
            _cfg.RemotePanel.PairedDevices = _devices.Export();
            ScheduleSave();
        });
        _remote.LogMessage += Log;
        _remote.SettingsChanged += (_, _) =>
        {
            PushPanelStatus();
            if (StartupOptions.SimulationMode) return;   // a simulation run never changes the church panel settings
            _cfg.RemotePanel.Enabled = _remote.WantEnabled;
            _cfg.RemotePanel.Port = _remote.Port;
            _cfg.RemotePanel.BindAddress = _remote.BindAddress;
            _cfg.RemotePanel.AllowPhoneTest = _remote.AllowPhoneTest;
            SaveConfig();
        };
        Shown += async (_, _) => await _remote.StartIfWantedAsync();
    }

    /// <summary>
    /// UI thread: the controller state changed (PC or phone). Panels follow the snapshot; kit selection changes made
    /// after the startup restore are copied into the configuration and saved (debounced, atomic). Nothing from a
    /// --simulation run or from the synthetic kit is saved over the operator's kit.
    /// </summary>
    private void OnControllerStateChanged()
    {
        Interlocked.Exchange(ref _stateChangedPosted, 0);
        var snap = _controller.Snapshot;
        if (snap.Version == _shownVersion) return;
        bool selectionChanged = snap.KitVersion != _shownKitVersion;
        _shownVersion = snap.Version;
        _shownKitVersion = snap.KitVersion;
        var kit = _kit.ActiveKit;
        foreach (var p in DrumPieces.All)
        {
            var panel = _panels[(int)p];
            var ps = snap.Pieces[(int)p];
            if (selectionChanged && kit is not null)
            {
                panel.SetSampleText(SampleText(kit[p]) + (_syntheticActive && kit[p].SourcePath is null && kit[p].Samples is not null ? " [sintético]" : ""));
                panel.ShowSettings(ps.Settings);             // a preset may carry gain/detection
            }
            panel.SetPieceEnabled(ps.Enabled);
        }
        UpdateKitUi();
        if (StartupOptions.SimulationMode || _syntheticActive || _controller.Version < _persistBaseline) return;
        foreach (var ps in snap.Pieces)
        {
            if (ps.SampleVersion > _persistBaseline) _cfg[ps.Piece].SamplePath = ps.SamplePath;
            _cfg[ps.Piece].Enabled = ps.Enabled;
            _cfg.SetPieceSettings(ps.Piece, ps.Settings);
        }
        if (snap.KitVersion > _persistBaseline) _cfg.ActivePresetId = snap.ActivePresetId;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>App values the phone shows (profile, audio running, routes, test permission).</summary>
    private void PushPanelStatus()
    {
        if (_panelServer is null || _remote is null) return;   // during construction
        var v = RoutingValidator.Validate(_cfg.ActiveProfile);
        _panelServer.AppStatus = new PanelAppStatus
        {
            ProfileName = _cfg.ActiveProfile.Name,
            Simulation = StartupOptions.SimulationMode,
            AudioRunning = _host?.IsRunning == true,
            AllowPhoneTest = _remote.AllowPhoneTest,
            Routed = DrumPieces.All.Select(v.IsRunnable).ToArray(),
        };
    }

    private bool IsSimulationSelected => _cmbDriver.SelectedIndex <= 0;

    // ---------- routing ----------

    private void RefreshRouting()
    {
        var v = RoutingValidator.Validate(_cfg.ActiveProfile);
        foreach (var panel in _panels)
        {
            var p = panel.Piece;
            var problem = v.IssuesOf(p).FirstOrDefault(i => i.IsError)?.Kind switch
            {
                RoutingIssueKind.DuplicateInput => "entrada repetida",
                RoutingIssueKind.DuplicateOutput => "saída repetida",
                RoutingIssueKind.Reserved => "canal reservado",
                RoutingIssueKind.OutOfRange => "fora da faixa",
                _ => null,
            };
            panel.SetRoute(_cfg.ActiveProfile[p], v.IsRunnable(p), problem ?? (_cfg.ActiveProfile[p].IsPending ? "pendente de configuração" : null));
        }

        _lstIssues.Items.Clear();
        foreach (var issue in v.Issues) _lstIssues.Items.Add((issue.IsError ? "ERRO: " : "Aviso: ") + issue.Message);
        if (v.Issues.Count == 0) _lstIssues.Items.Add("Roteamento OK: as quatro peças têm entrada e saída.");
        _lblRouting.Text = v.HasErrors
            ? "há erros: o ASIO não inicia até corrigir"
            : IsSimulationSelected
                ? $"{v.RunnablePieces.Count} de {DrumPieces.Count} peças recebem sinal na simulação (só as roteadas no perfil)"
                : $"{v.RunnablePieces.Count} de {DrumPieces.Count} peças rodam no ASIO";
        _lblRouting.ForeColor = v.HasErrors ? Color.DarkRed : SystemColors.ControlText;
        PushPanelStatus();
    }

    private void EditRoutes()
    {
        using var dlg = new RoutingEditorForm(_cfg.ActiveProfile);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _cfg.SetProfile(dlg.Profile);
        Log($"Rotas do perfil \"{dlg.Profile.Name}\" atualizadas" +
            (_host?.IsRunning == true ? " (valem no próximo \"Iniciar áudio\")." : "."));
        SaveConfig();
        RefreshRouting();
    }

    // ---------- samples / presets ----------

    private sealed record PresetItem(KitPreset Preset)
    {
        public override string ToString() => Preset.Name;
    }

    /// <summary>Startup: one controller operation per configured sample, so one bad file does not block the others.</summary>
    /// <summary>
    /// Startup: the saved kit comes back as ONE whole-kit operation (samples only, calibration untouched) that keeps
    /// the saved ActivePresetId when the pieces still match that preset file. If any file fails, falls back to one
    /// operation per piece so a single bad file does not block the others.
    /// </summary>
    private async Task LoadConfiguredSamplesAsync()
    {
        if (DrumPieces.All.All(p => _cfg[p].SamplePath is null)) return;
        KitPreset? active = null;
        if (_cfg.ActivePresetId is { Length: > 0 } id)
        {
            string file = Path.Combine(_cfg.EffectivePresetsFolder, id + ".json");
            try { if (File.Exists(file)) active = KitPreset.Load(file); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log($"AVISO: preset ativo \"{id}\" não pôde ser lido ({ex.Message}).");
            }
        }
        var startup = _cfg.ToStartupPreset(active);
        var whole = await _controller.ApplyPreset(startup).Completion;
        if (whole.Status == KitOperationStatus.Applied)
        {
            foreach (var p in DrumPieces.All) _panels[(int)p].SetSampleText(SampleText(_kit.ActiveKit![p]));
            if (startup.Id is not null) Log($"Kit do preset \"{startup.Name}\" restaurado.");
            return;
        }
        ShowPieceErrors(whole.Errors);
        Log("Alguns samples salvos falharam; carregando as outras peças uma a uma.");
        var ops = DrumPieces.All.Where(p => _cfg[p].SamplePath is not null && whole.Errors.All(e => e.Piece != p))
            .Select(p => (Piece: p, Op: _controller.SetPieceSamplePath(p, _cfg[p].SamplePath))).ToList();
        foreach (var (p, op) in ops)
        {
            var r = await op.Completion;
            if (r.Status == KitOperationStatus.Applied) _panels[(int)p].SetSampleText(SampleText(_kit.ActiveKit![p]));
            else ShowPieceErrors(r.Errors);
        }
    }

    private async Task ChooseWavAsync(DrumPiece p)
    {
        using var dlg = new OpenFileDialog
        {
            Title = $"Escolher sample: {p.Label()}",
            Filter = "Arquivos WAV (*.wav)|*.wav|Todos os arquivos (*.*)|*.*",
        };
        var current = _cfg[p].SamplePath;
        dlg.InitialDirectory = current is not null && Path.IsPathRooted(current) && !StartupOptions.SimulationMode
            ? Path.GetDirectoryName(current)
            : Directory.Exists(SamplesFolder) ? SamplesFolder : null;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await SetPieceSampleAsync(p, dlg.FileName);
    }

    /// <summary>
    /// Single-piece change through the controller: prepared off the UI thread, then one ApplyKit (valid from the next
    /// hit). A newer request for the same piece (or a preset) supersedes this one.
    /// </summary>
    private async Task SetPieceSampleAsync(DrumPiece p, string? path)
    {
        if (!StartupOptions.SimulationMode && !await LeaveSyntheticAsync("troca de sample")) return;
        var r = await _controller.SetPieceSamplePath(p, path).Completion;
        switch (r.Status)
        {
            case KitOperationStatus.Applied:
                _cfg[p].SamplePath = path;
                _cfg.ActivePresetId = null;                       // custom selection
                _panels[(int)p].SetSampleText(SampleText(_kit.ActiveKit![p]));
                Log(path is null ? $"{p.Label()}: sem sample." : $"{p.Label()}: sample {path} (vale a partir do próximo golpe).");
                SaveConfig();
                break;
            case KitOperationStatus.Failed:
                ShowPieceErrors(r.Errors);
                Log($"{p.Label()}: sample anterior mantido.");
                break;
            default:
                Log($"{p.Label()}: troca de sample substituída por um pedido mais novo.");
                break;
        }
    }

    /// <summary>Applies a preset through the controller: ALL samples prepared first; only a complete kit is applied (one swap).</summary>
    private async Task ApplyPresetAsync(KitPreset preset)
    {
        if (!StartupOptions.SimulationMode && !await LeaveSyntheticAsync("preset escolhido")) return;
        Log($"Preparando o preset \"{preset.Name}\"...");
        var r = await _controller.ApplyPreset(preset).Completion;
        switch (r.Status)
        {
            case KitOperationStatus.Applied:
                _cfg.ApplyPreset(preset);
                var settings = _kit.Settings;
                foreach (var p in DrumPieces.All)
                {
                    _panels[(int)p].ShowSettings(settings[p]);
                    _panels[(int)p].SetSampleText(SampleText(_kit.ActiveKit![p]));
                }
                Log($"Preset \"{preset.Name}\" aplicado (as quatro peças trocadas no mesmo bloco).");
                SaveConfig();
                break;
            case KitOperationStatus.Failed:
                ShowPieceErrors(r.Errors);
                Log($"Preset \"{preset.Name}\" NÃO aplicado: o kit atual foi mantido.");
                break;
            default:
                Log($"Preset \"{preset.Name}\" substituído por um pedido mais novo.");
                break;
        }
    }

    private async Task OpenPresetFileAsync()
    {
        using var dlg = new OpenFileDialog { Title = "Abrir preset de kit", Filter = "Preset SampleDrum (*.json)|*.json" };
        if (Directory.Exists(PresetsFolder)) dlg.InitialDirectory = PresetsFolder;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        KitPreset preset;
        try { preset = KitPreset.Load(dlg.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log($"ERRO ao ler o preset: {ex.Message}");
            return;
        }
        await ApplyPresetAsync(preset);
    }

    private void SavePreset()
    {
        string? name = Prompt("Salvar preset", "Nome do preset:", _kit.ActiveKit?.Name is { Length: > 0 } n && n != "configuração" ? n : "Kit");
        if (string.IsNullOrWhiteSpace(name)) return;
        string path = Path.Combine(PresetsFolder, KitPreset.FileNameFor(name));
        try
        {
            _cfg.ToPreset(name.Trim()).Save(path);
            Log($"Preset salvo: {path}");
            RefreshPresetList(name.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"ERRO ao salvar o preset: {ex.Message}");
        }
    }

    private void RefreshPresetList(string? select = null)
    {
        var presets = KitPreset.LoadFolder(PresetsFolder, out var errors);
        _cmbPresets.Items.Clear();
        foreach (var p in presets) _cmbPresets.Items.Add(new PresetItem(p));
        foreach (var e in errors) Log($"AVISO: preset ignorado: {e}");
        var match = _cmbPresets.Items.Cast<PresetItem>().FirstOrDefault(i => i.Preset.Name == select);
        if (match is not null) _cmbPresets.SelectedItem = match;
        else if (_cmbPresets.Items.Count > 0) _cmbPresets.SelectedIndex = 0;
        _btnLoadPreset.Enabled = _cmbPresets.Items.Count > 0;
    }

    private async Task ChooseSamplesFolderAsync()
    {
        using var dlg = new FolderBrowserDialog { Description = "Pasta com os samples (WAV)", UseDescriptionForTitle = true };
        if (Directory.Exists(SamplesFolder)) dlg.SelectedPath = SamplesFolder;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // Same controller for the whole run: the folder change is an operation (Version keeps increasing).
        var r = await _controller.SetLibraryFolder(dlg.SelectedPath).Completion;
        if (r.Status != KitOperationStatus.Applied)
        {
            foreach (var e in r.Errors) Log("ERRO: " + e.Message);   // not a piece error: keep the piece labels as they are
            Log("Pasta de samples NÃO trocada.");
            return;
        }
        _cfg.SamplesFolder = dlg.SelectedPath;
        _lblSamples.Text = dlg.SelectedPath;
        var list = _controller.Library.List();
        Log($"Pasta de samples: {dlg.SelectedPath} ({list.Count} WAV)" +
            (list.Count > 0 ? ": " + string.Join(", ", list.Take(12).Select(s => $"{s.Name} ({s.DurationSeconds * 1000:0} ms)")) +
                              (list.Count > 12 ? ", ..." : "") : ""));
        SaveConfig();
    }

    private static string SampleText(PreparedPiece piece) => piece.Samples is null
        ? "nenhum sample"
        : $"{piece.Name} ({piece.Samples.Length * 1000.0 / EngineRate:0} ms)";

    private void ShowPieceErrors(IReadOnlyList<PieceLoadError> errors)
    {
        foreach (var e in errors)
        {
            Log("ERRO: " + e.Message);
            _panels[(int)e.Piece].SetSampleText("erro: " + e.Message, isError: true);
        }
    }

    private static string? Prompt(string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10),
        };
        var box = new TextBox { Text = initial, Width = 320 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        flow.Controls.Add(PiecePanel.Lbl(label));
        flow.Controls.Add(box);
        flow.Controls.Add(PiecePanel.Row(ok, cancel));
        form.Controls.Add(flow);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    /// <summary>
    /// Synthetic kit of the simulation scenario: its samples (one ApplyKit) and the per-piece detection that matches
    /// it, shown in the panels. Not written to the configuration (the operator's own samples/settings stay saved).
    /// </summary>
    private async Task LoadSyntheticKitAsync(int seed)
    {
        bool sameSeed = StartupOptions.SimulationSeed == seed;
        StartupOptions.SimulationSeed = seed;
        var kit = _simLibrary is not null && sameSeed ? _simLibrary.Kit : StartupOptions.SimulationKit();
        var settings = StartupOptions.SimulationKitSettings();
        var operatorKit = _syntheticActive ? _operatorKit : _kit.ActiveKit;
        var r = await _controller.ApplyKit(kit, settings).Completion;
        if (r.Status != KitOperationStatus.Applied)
        {
            Log($"Kit sintético não aplicado ({r.Status}).");
            return;
        }
        _operatorKit = operatorKit;
        _syntheticActive = true;
        foreach (var p in DrumPieces.All)
        {
            _panels[(int)p].ShowSettings(settings[p]);
            _panels[(int)p].SetSampleText(SampleText(kit[p]) + (kit[p].SourcePath is null ? " [sintético]" : " [demonstração]"));
        }
        Log($"Kit sintético (semente {seed}) carregado: samples e ajustes de detecção da simulação (não salvos na " +
            (StartupOptions.SimulationMode
                ? "configuração; samples e presets de demonstração podem ser trocados aqui ou pelo celular)."
                : "configuração; o kit do operador volta ao escolher um driver ASIO, um sample ou um preset)."));
    }

    /// <summary>
    /// Unloads the synthetic kit: the operator's samples (kit saved when the synthetic kit was loaded) and the
    /// calibration stored in the configuration go back into the engine, through the controller. True when no
    /// synthetic kit is loaded anymore.
    /// </summary>
    private async Task<bool> LeaveSyntheticAsync(string reason)
    {
        if (!_syntheticActive) return true;
        // In a --simulation run the operator's configured samples were never loaded: reload whatever is missing.
        bool reload = DrumPieces.All.Any(p => _cfg[p].SamplePath is not null && _operatorKit?[p].Samples is null);
        var kit = (_operatorKit ?? PreparedKit.Empty(EngineRate)).SamplesOnly();
        var settings = _cfg.ToKitSettings();
        var r = await _controller.ApplyKit(kit, settings).Completion;
        if (r.Status != KitOperationStatus.Applied)
        {
            Log($"ERRO: não foi possível restaurar o kit do operador ({r.Status}); o kit sintético continua carregado.");
            return false;
        }
        _syntheticActive = false;
        _operatorKit = null;
        foreach (var p in DrumPieces.All)
        {
            _panels[(int)p].ShowSettings(settings[p]);
            _panels[(int)p].SetSampleText(SampleText(_kit.ActiveKit![p]));
        }
        Log($"Kit sintético descarregado ({reason}): samples e ajustes do operador restaurados.");
        if (reload) await LoadConfiguredSamplesAsync();
        return true;
    }

    // ---------- audio ----------

    private async Task StartAudioAsync()
    {
        if (_host?.IsRunning == true) return;
        _btnStart.Enabled = false;
        try
        {
            if (_initialLoad is not null) await _initialLoad; // samples ready BEFORE audio starts

            if (!IsSimulationSelected && !await LeaveSyntheticAsync("início do áudio ASIO"))
            {
                MessageBox.Show(this, "O kit sintético da simulação ainda está carregado e não foi possível restaurar o kit " +
                    "do operador. O áudio ASIO não foi iniciado.", "SampleDrum – kit sintético",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var v = RoutingValidator.Validate(_cfg.ActiveProfile);
            RefreshRouting();
            if (!v.HasErrors && v.RunnablePieces.Count == 0)
            {
                const string none = "Nenhuma peça tem entrada e saída definidas (sem erros) neste perfil: nada para iniciar.";
                Log("ERRO: " + none);
                MessageBox.Show(this, none, "SampleDrum – roteamento", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (v.HasErrors)
            {
                string errors = string.Join("\r\n", v.Issues.Where(i => i.IsError).Select(i => "• " + i.Message));
                Log("ERRO: o roteamento tem erros; o áudio não foi iniciado.\r\n" + errors);
                MessageBox.Show(this, "Corrija o roteamento antes de iniciar:\r\n\r\n" + errors,
                    "SampleDrum – roteamento inválido", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            foreach (var issue in v.Issues) Log("Aviso: " + issue.Message);
            var routing = v.Resolve();

            IAudioHost host;
            if (IsSimulationSelected)
            {
                var options = _sim.BuildOptions();
                Log($"Simulação: fonte {options.Source}, bloco {options.BlockSize}, " +
                    $"{(options.Loop ? "repetindo" : "sem repetir")}, peças com sinal: " +
                    (routing.RoutedPieces.Count == 0 ? "nenhuma" : string.Join(", ", routing.RoutedPieces.Select(p => p.Label()))) + ".");
                var fake = new FakeAudioHost(_kit, routing, options);
                fake.Finished += () => PostToUi(() =>
                {
                    if (ReferenceEquals(_host, fake)) StopAudio("Simulação: fim dos arquivos de microfone.");
                });
                host = fake;
            }
            else
            {
                string driver = (string)_cmbDriver.SelectedItem!;
                Log($"Abrindo \"{driver}\" com {routing.RoutedPieces.Count} peça(s): " + string.Join(", ", routing.RoutedPieces.Select(p =>
                    $"{p.Label()} entrada USB {routing.InputIndex(p) + 1} (índice ASIO {routing.InputIndex(p)}) → " +
                    $"saída USB {routing.OutputIndex(p) + 1} (índice ASIO {routing.OutputIndex(p)})")) + "...");
                host = new AsioDuplexHost(driver, routing, _kit);
            }

            DisposeHost();
            // A previous run may have been stopped mid-voice/mid-detection: start from a clean engine state.
            // Only safe when the previous host really stopped (no audio thread can be inside Process).
            if (_hostStopFailed)
            {
                _hostStopFailed = false;
                Log("AVISO: o áudio anterior não fechou corretamente; o estado do disparo não foi limpo.");
            }
            else
            {
                _kit.Reset();
            }

            _host = host;
            // Faulted may come from a non-UI thread (simulation thread, ThreadPool): always marshal.
            host.Faulted += msg => PostToUi(() => OnHostFaulted(host, msg));
            host.Start();
            Log("Áudio iniciado: " + host.Description);
            if (host is FakeAudioHost started)
                foreach (var note in started.LoadNotes) Log("Simulação: " + note);
            UpdateLatencyLabel();
            Log("Para anotar no ensaio: " + _lblLatency.Text);
            SaveConfig();
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
        try { h?.Dispose(); }
        catch (Exception ex)
        {
            _hostStopFailed = true;
            Log($"AVISO ao fechar o áudio: {ex.Message}");
        }
    }

    private void TestPiece(DrumPiece p)
    {
        _kit.RequestTest(p, 1f);
        if (_host?.IsRunning != true) Log($"{p.Label()}: teste pedido, mas o áudio está parado (clique em \"Iniciar áudio\").");
        else if (_kit.ActiveKit?[p].Samples is null) Log($"{p.Label()}: teste pedido, mas a peça não tem sample.");
        else if (_host is AsioDuplexHost h && !h.Routing.IsRouted(p)) Log($"{p.Label()}: teste pedido, mas a peça não tem rota (pendente): nada sai na mesa.");
    }

    private void OpenAsioPanel()
    {
        if (IsSimulationSelected) return;
        string driver = (string)_cmbDriver.SelectedItem!;
        Log($"Abrindo o painel do driver \"{driver}\". Atenção: não altere taxa nem buffer — o OBS usa o mesmo driver.");
        try { AsioDuplexHost.ShowControlPanel(driver, _host); }
        catch (Exception ex) { Log("ERRO ao abrir o painel ASIO: " + ex.Message); }
    }

    private void UpdateKitUi()
    {
        bool on = _kit.KitEnabled;
        _btnKit.Text = on ? "Desativar kit" : "Ativar kit";
        _lblKit.Text = on ? "Kit ATIVADO (disparo automático nas peças ativas)" : "Kit DESATIVADO (retornos em silêncio)";
        _lblKit.ForeColor = on ? Color.DarkGreen : Color.DarkRed;
    }

    private void UpdateAudioUi()
    {
        bool running = _host?.IsRunning == true;
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _cmbDriver.Enabled = _btnRefresh.Enabled = _cmbProfile.Enabled = !running;
        _btnPanel.Enabled = !IsSimulationSelected;
        _sim.Visible = IsSimulationSelected;
        _sim.SetRunning(running);
        if (!running) _lblAudio.Text = "Áudio parado";
        PushPanelStatus();
    }

    // ---------- 30 Hz UI refresh ----------

    private void OnUiTick()
    {
        var now = DateTime.UtcNow;
        foreach (var panel in _panels)
        {
            float peak = _kit.ReadAndResetInputPeak(panel.Piece);   // single consumer: this timer
            panel.UpdateMeter(peak, now);
            _telemetry.PublishPeak(panel.Piece, peak);
        }

        StringBuilder? sb = null;
        int drained = 0;
        while (drained < 256 && _kit.TryDequeueEvent(out var ke))
        {
            drained++;
            var e = ke.Event;
            _panels[(int)ke.Piece].Flash(e.IsManualTest, now);
            _telemetry.PublishHit(ke.Piece, e.Velocity, e.IsManualTest);
            sb ??= new StringBuilder();
            double t = e.SampleIndex / (double)EngineRate;
            if (e.IsManualTest)
                sb.Append($"{Stamp()} {ke.Piece.Label()}: teste manual (t={t:0.000} s, ganho {e.Velocity:0.00})\r\n");
            else
            {
                _eventCount++;
                sb.Append($"{Stamp()} {ke.Piece.Label()}: disparo #{_eventCount}{(e.IsRetrigger ? " (redisparo)" : "")} t={t:0.000} s, " +
                          $"pico {ToDb(e.Peak):0.0} dBFS, velocidade {e.Velocity:0.00}, ganho {ToDb(e.Gain):0.0} dB\r\n");
            }
        }
        if (sb is not null) AppendLog(sb.ToString());

        if (_host?.IsRunning == true)
            _lblAudio.Text = $"Rodando: {_host.CallbackCount} blocos de {_host.FramesPerBuffer}, {_eventCount} disparos";
        if (_host is FakeAudioHost fake && fake.IsRunning)
        {
            double pos = fake.PositionSamples / (double)EngineRate, len = fake.SourceLengthSamples / (double)EngineRate;
            string preview = !fake.PreviewEnabled ? "prévia desligada"
                : fake.PreviewError is { } err ? $"prévia: ERRO {err}"
                : $"prévia ligada ({fake.PreviewUnderruns} falhas de buffer)";
            _sim.SetStatus($"  {pos:0.0} / {len:0.0} s; {preview}", fake.PreviewError is not null);
        }
        UpdateLatencyLabel();
    }

    /// <summary>Buffer size and latencies reported by the running host, for the operator to note.</summary>
    private void UpdateLatencyLabel()
    {
        string text;
        var h = _host;
        if (h is null || !h.IsRunning) text = "Buffer/latência: áudio parado";
        else
        {
            double rate = h.SampleRate > 0 ? h.SampleRate : EngineRate;
            double Ms(int samples) => samples * 1000.0 / rate;
            var s = _kit.Settings;
            var pieces = h is AsioDuplexHost a ? a.Routing.RoutedPieces : DrumPieces.All;
            double detectMs = pieces.Count == 0 ? 0 : pieces.Max(p => Math.Max(s[p].Trigger.DetectTimeMs, s[p].Trigger.ScanMs));
            if (h is FakeAudioHost)
                text = $"Simulação: bloco {h.FramesPerBuffer} amostras = {Ms(h.FramesPerBuffer):0.00} ms @ {rate:0} Hz " +
                       $"(sem driver); detecção até +{detectMs:0.0} ms";
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
        if (_txtLog.TextLength > 200_000) _txtLog.Text = _txtLog.Text[^100_000..];
        _txtLog.AppendText(text);
    }

    // ---------- config / shutdown ----------

    private void SaveConfig()
    {
        foreach (var panel in _panels) _cfg[panel.Piece].AdvancedExpanded = panel.AdvancedExpanded;
        try
        {
            if (StartupOptions.SimulationMode)
            {
                // A --simulation run never changes what the operator chose for normal runs, and never creates the
                // settings file when it does not exist yet (see AppConfig.SaveSimulationRun). The kit selection, the
                // samples/presets folders and the phone panel (paired phones included) stay as they were at start:
                // the run works on a demo library in a temp folder.
                var copy = AppConfig.Parse(_cfg.ToJson(), out _);
                if (_startupCfg is { } start)
                {
                    copy.SamplesFolder = start.SamplesFolder;
                    copy.PresetsFolder = start.PresetsFolder;
                    copy.ActivePresetId = start.ActivePresetId;
                    copy.RemotePanel = start.RemotePanel;
                    foreach (var p in DrumPieces.All) copy[p].SamplePath = start[p].SamplePath;
                }
                copy.SaveSimulationRun(AppConfig.DefaultFilePath, _savedUseSimulation, _savedProfileName, _savedDriverName);
            }
            else
            {
                _cfg.UseSimulation = IsSimulationSelected;
                if (!IsSimulationSelected) _cfg.DriverName = (string)_cmbDriver.SelectedItem!;
                _cfg.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"AVISO: não foi possível salvar {AppConfig.DefaultFilePath}: {ex.Message}");
        }
    }

    private void OnClosing()
    {
        _uiTimer.Stop();
        _saveTimer.Stop();
        // Panel first (closes phone connections); off the UI thread so nothing can wait on it.
        try { Task.Run(() => _panelServer.StopAsync()).Wait(TimeSpan.FromSeconds(4)); }
        catch (AggregateException ex) { Log($"AVISO ao parar o painel do celular: {ex.InnerException?.Message}"); }
        DisposeHost();
        // The controller worker never touches the UI, so waiting here cannot deadlock.
        _controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        SaveConfig();
        _simLibrary?.Dispose();
    }
}
