using SampleDrum.Core;
using SampleDrum.Core.Kit;

namespace SampleDrum.App;

/// <summary>
/// One piece of the kit (Bumbo, Caixa, Tom, Surdo): enable, routing, meter + trigger LED, sample, gain, test and the
/// detection parameters (basic + collapsible "Avançado", as in v1). UI thread only; raises events, never touches the
/// engine itself.
/// </summary>
public sealed class PiecePanel : GroupBox
{
    private readonly CheckBox _chkEnabled = new() { Text = "Ativa", AutoSize = true, Margin = new Padding(3, 6, 8, 0) };
    private readonly Label _lblRoute = Lbl("");
    private readonly LevelMeter _meter = new() { Width = 260 };
    private readonly Panel _led = new() { Width = 20, Height = 20, BackColor = LedOff, Margin = new Padding(6, 4, 3, 3) };
    private readonly Label _lblLevel = Lbl("-∞ dBFS");

    private readonly Label _lblSample = new() { AutoSize = false, Width = 260, Height = 20, Margin = new Padding(3, 7, 3, 0), AutoEllipsis = true };
    private readonly Button _btnWav = new() { Text = "Escolher WAV...", AutoSize = true };
    private readonly Button _btnClear = new() { Text = "Sem sample", AutoSize = true };
    private readonly NumericUpDown _nudGain = DbBox(-60, 24);
    private readonly Button _btnTest = new() { Text = "Testar", AutoSize = true };

    private readonly NumericUpDown _nudThreshold = DbBox(-90, 0);
    private readonly NumericUpDown _nudRearm = DbBox(-120, 0);
    private readonly NumericUpDown _nudInterval = new() { Minimum = 0, Maximum = 5000, Increment = 5, Width = 65 };
    private readonly CheckBox _chkAdvanced = new()
    {
        Text = "Avançado ▸", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(8, 3, 3, 3),
    };
    private readonly FlowLayoutPanel _pnlAdvanced = new()
    {
        FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0),
    };
    private readonly NumericUpDown _nudHighPass = Box(0, 2000, 0, 5m);
    private readonly NumericUpDown _nudLowPass = Box(0, 20000, 0, 10m);
    private readonly NumericUpDown _nudDetect = Box(0, 20, 1, 0.1m);
    private readonly NumericUpDown _nudScan = Box(0, 50, 1, 0.5m);
    private readonly Label _lblTriggerDelay = Lbl("");
    private readonly NumericUpDown _nudRearmTime = Box(0, 1000, 0, 1m);
    private readonly NumericUpDown _nudRetrigger = Box(0, 48, 1, 0.5m);
    private readonly NumericUpDown _nudVelRange = Box(1, 120, 1, 1m);
    private readonly NumericUpDown _nudMinGain = Box(-96, 0, 1, 1m);
    private readonly CheckBox _chkFixedVelocity = new()
    {
        Text = "Velocidade fixa (todo disparo a 0 dB)", AutoSize = true, Margin = new Padding(3, 6, 3, 0),
    };
    private readonly Button _btnDefaults = new() { Text = "Restaurar padrões da peça", AutoSize = true };

    private static readonly Color LedOff = Color.FromArgb(0, 60, 0);
    private bool _loading;
    private TriggerSettings _current = TriggerSettings.Default;
    private DateTime _ledUntil = DateTime.MinValue;
    private bool _ledManual;
    private float _meterDb = LevelMeter.MinDb, _holdDb = LevelMeter.MinDb;
    private DateTime _holdUntil = DateTime.MinValue;

    public PiecePanel(DrumPiece piece)
    {
        Piece = piece;
        Text = $"{piece.Label()}  (CH {piece.DeskMicChannel()} / {piece.StageSource()})";
        AutoSize = true;
        Dock = DockStyle.Top;
        Padding = new Padding(8, 4, 8, 4);

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.Add(Row(_chkEnabled, _lblRoute));
        flow.Controls.Add(Row(Lbl("Entrada:"), _meter, _led, _lblLevel));
        flow.Controls.Add(Row(Lbl("Sample:"), _lblSample, _btnWav, _btnClear, Lbl("  Ganho (dB)"), _nudGain, _btnTest));
        flow.Controls.Add(Row(Lbl("Limiar (dBFS)"), _nudThreshold, Lbl("  Rearme (dBFS)"), _nudRearm,
            Lbl("  Intervalo mínimo (ms)"), _nudInterval, _chkAdvanced));
        flow.Controls.Add(_pnlAdvanced);
        _pnlAdvanced.Controls.AddRange(
        [
            Row(Lbl("Passa-altas no detector (Hz, 0 = desligado)"), _nudHighPass,
                Lbl("  Passa-baixas (Hz, 0 = desligado)"), _nudLowPass),
            Row(Lbl("Tempo de detecção (ms)"), _nudDetect, Lbl("  Janela de velocidade (ms)"), _nudScan, _lblTriggerDelay),
            Row(Lbl("Tempo de rearme (ms)"), _nudRearmTime, Lbl("  Redisparo por subida (dB, 0 = desligado)"), _nudRetrigger),
            Row(Lbl("Faixa de velocidade (dB)"), _nudVelRange, Lbl("  Ganho mínimo (dB)"), _nudMinGain),
            Row(_chkFixedVelocity, _btnDefaults),
        ]);
        Controls.Add(flow);

        _chkEnabled.CheckedChanged += (_, _) => { if (!_loading) PieceEnabledChanged?.Invoke(this, _chkEnabled.Checked); };
        _btnWav.Click += (_, _) => ChooseWavRequested?.Invoke(this, EventArgs.Empty);
        _btnClear.Click += (_, _) => ClearSampleRequested?.Invoke(this, EventArgs.Empty);
        _btnTest.Click += (_, _) => TestRequested?.Invoke(this, EventArgs.Empty);
        _chkAdvanced.CheckedChanged += (_, _) => UpdateAdvancedUi();
        _btnDefaults.Click += (_, _) =>
        {
            ShowSettings(PieceSettings.DefaultFor(Piece));
            RaiseSettingsChanged();
        };
        _nudThreshold.ValueChanged += (_, _) => OnThresholdChanged();
        foreach (var nud in new[] { _nudGain, _nudRearm, _nudInterval, _nudHighPass, _nudLowPass, _nudDetect, _nudScan,
                                    _nudRearmTime, _nudRetrigger, _nudVelRange, _nudMinGain })
            nud.ValueChanged += (_, _) => RaiseSettingsChanged();
        _chkFixedVelocity.CheckedChanged += (_, _) => RaiseSettingsChanged();
    }

    public DrumPiece Piece { get; }

    /// <summary>The detection/gain controls changed (already normalized).</summary>
    public event EventHandler<PieceSettings>? SettingsChanged;
    public event EventHandler<bool>? PieceEnabledChanged;
    public event EventHandler? ChooseWavRequested;
    public event EventHandler? ClearSampleRequested;
    public event EventHandler? TestRequested;

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AdvancedExpanded
    {
        get => _chkAdvanced.Checked;
        set { _chkAdvanced.Checked = value; UpdateAdvancedUi(); }
    }

    public void SetPieceEnabled(bool on)
    {
        _loading = true;
        try { _chkEnabled.Checked = on; }
        finally { _loading = false; }
    }

    /// <summary>Routing line, e.g. "Entrada USB 3 (índice ASIO 2) → Saída USB 1 (índice ASIO 0)" or "pendente".</summary>
    public void SetRoute(PieceRoute route, bool runnable, string? problem)
    {
        static string Side(string name, int? ch) => ch is int c ? $"{name} USB {c} (índice ASIO {c - 1})" : $"{name} pendente";
        _lblRoute.Text = $"{Side("Entrada", route.InputDeskChannel)}  →  {Side("Saída", route.OutputDeskChannel)}" +
                         (runnable ? "" : problem is null ? "   [não roda no ASIO]" : $"   [{problem}]");
        _lblRoute.ForeColor = runnable ? SystemColors.ControlText : route.IsPending ? Color.DarkOrange : Color.DarkRed;
    }

    public void SetSampleText(string text, bool isError = false)
    {
        _lblSample.Text = text;
        _lblSample.ForeColor = isError ? Color.DarkRed : SystemColors.ControlText;
    }

    public void SetBusy(bool busy) => _btnWav.Enabled = _btnClear.Enabled = !busy;

    /// <summary>Shows settings without raising <see cref="SettingsChanged"/>.</summary>
    public void ShowSettings(PieceSettings s)
    {
        _loading = true;
        try
        {
            var t = s.Trigger.Normalized();
            _current = t;
            SetNud(_nudGain, s.OutputGainDb);
            SetNud(_nudThreshold, t.ThresholdDb);
            SetNud(_nudRearm, t.RearmDb);
            SetNud(_nudInterval, t.MinIntervalMs);
            SetNud(_nudHighPass, t.HighPassHz);
            SetNud(_nudLowPass, t.LowPassHz);
            SetNud(_nudDetect, t.DetectTimeMs);
            SetNud(_nudScan, t.ScanMs);
            SetNud(_nudRearmTime, t.RearmTimeMs);
            SetNud(_nudRetrigger, t.RetriggerRiseDb);
            SetNud(_nudVelRange, t.VelocityRangeDb);
            SetNud(_nudMinGain, t.MinGainDb);
            _chkFixedVelocity.Checked = t.FixedVelocity;
        }
        finally { _loading = false; }
        UpdateMarkers();
    }

    /// <summary>~30 Hz from the UI timer: input peak (linear) since the last tick.</summary>
    public void UpdateMeter(float peakLinear, DateTime now)
    {
        float db = peakLinear > 1e-6f ? 20f * MathF.Log10(peakLinear) : -120f;
        _meterDb = Math.Max(db, _meterDb - 1.5f);           // ~45 dB/s fall
        if (db >= _holdDb || now > _holdUntil) { _holdDb = db; _holdUntil = now.AddSeconds(1.5); }
        _meter.LevelDb = _meterDb;
        _meter.PeakHoldDb = _holdDb;
        _lblLevel.Text = _meterDb <= LevelMeter.MinDb ? "-∞ dBFS" : $"{_meterDb:0.0} dBFS";
        if (now > _ledUntil) _led.BackColor = LedOff;
        else _led.BackColor = _ledManual ? Color.DeepSkyBlue : Color.Lime;
    }

    /// <summary>Lights the trigger LED (automatic = green, manual test = blue).</summary>
    public void Flash(bool manual, DateTime now)
    {
        _ledManual = manual;
        _ledUntil = now.AddMilliseconds(120);
        _led.BackColor = manual ? Color.DeepSkyBlue : Color.Lime;
    }

    private void OnThresholdChanged()
    {
        if (_loading) return;
        // Moving the threshold moves the re-arm level by the same amount (keeps the hysteresis), as in v1.
        var moved = _current.WithThreshold((float)_nudThreshold.Value);
        _loading = true;
        try { SetNud(_nudRearm, moved.RearmDb); }
        finally { _loading = false; }
        RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged()
    {
        if (_loading) return;
        if (_nudRearm.Value > _nudThreshold.Value)
        {
            _loading = true;
            try { _nudRearm.Value = _nudThreshold.Value; }
            finally { _loading = false; }
        }
        _current = (_current with
        {
            ThresholdDb = (float)_nudThreshold.Value,
            RearmDb = (float)_nudRearm.Value,
            MinIntervalMs = (float)_nudInterval.Value,
            HighPassHz = (float)_nudHighPass.Value,
            LowPassHz = (float)_nudLowPass.Value,
            DetectTimeMs = (float)_nudDetect.Value,
            ScanMs = (float)_nudScan.Value,
            RearmTimeMs = (float)_nudRearmTime.Value,
            RetriggerRiseDb = (float)_nudRetrigger.Value,
            VelocityRangeDb = (float)_nudVelRange.Value,
            MinGainDb = (float)_nudMinGain.Value,
            FixedVelocity = _chkFixedVelocity.Checked,
        }).Normalized();
        UpdateMarkers();
        SettingsChanged?.Invoke(this, new PieceSettings { Trigger = _current, OutputGainDb = (float)_nudGain.Value });
    }

    private void UpdateMarkers()
    {
        _meter.ThresholdDb = (float)_nudThreshold.Value;
        _meter.RearmDb = (float)_nudRearm.Value;
        decimal delay = Math.Max(_nudDetect.Value, _nudScan.Value);
        _lblTriggerDelay.Text = $"atraso do disparo = {delay:0.0} ms";
    }

    private void UpdateAdvancedUi()
    {
        _pnlAdvanced.Visible = _chkAdvanced.Checked;
        _chkAdvanced.Text = _chkAdvanced.Checked ? "Avançado ▾" : "Avançado ▸";
    }

    internal static Label Lbl(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 0) };

    internal static NumericUpDown DbBox(int min, int max) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = 1, Increment = 0.5m, Width = 65 };

    internal static NumericUpDown Box(int min, int max, int decimals, decimal increment) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = increment, Width = 65 };

    internal static void SetNud(NumericUpDown nud, float value) =>
        nud.Value = Math.Clamp((decimal)MathF.Round(value, nud.DecimalPlaces), nud.Minimum, nud.Maximum);

    internal static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 1, 0, 1) };
        row.Controls.AddRange(controls);
        return row;
    }
}
