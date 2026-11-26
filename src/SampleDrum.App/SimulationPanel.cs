using SampleDrum.App.Audio;
using SampleDrum.Core.Kit;
using SampleDrum.Core.Simulation;

namespace SampleDrum.App;

/// <summary>
/// "Simulação (sem ASIO)" controls: mic source (synthetic kit / four mic WAVs / one multichannel WAV), block size,
/// loop, and the PC preview (headphones/speakers): on/off, volume, pieces in the mix and mic level.
/// Source/block/loop apply at the next "Iniciar áudio"; preview changes apply live (<see cref="PreviewChanged"/>).
/// </summary>
public sealed class SimulationPanel : GroupBox
{
    private readonly ComboBox _cmbSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _nudSeed = new() { Minimum = 1, Maximum = 9999, Value = 1, Width = 60 };
    private readonly Button _btnSyntheticKit = new() { Text = "Carregar kit sintético", AutoSize = true };
    private readonly ComboBox _cmbBlock = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly CheckBox _chkLoop = new() { Text = "Repetir", Checked = true, AutoSize = true, Margin = new Padding(8, 6, 3, 0) };

    private readonly FlowLayoutPanel _pnlMics = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly Label[] _lblMic = new Label[DrumPieces.Count];
    private readonly string?[] _micPaths = new string?[DrumPieces.Count];

    private readonly FlowLayoutPanel _pnlMulti = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly Label _lblMulti = new() { AutoSize = false, Width = 420, Height = 20, Margin = new Padding(3, 7, 3, 0), AutoEllipsis = true, Text = "nenhum arquivo" };
    private readonly NumericUpDown[] _nudChannel = new NumericUpDown[DrumPieces.Count];
    private string? _multiPath;

    private readonly CheckBox _chkPreview = new() { Text = "Prévia nos fones (saída padrão do PC)", AutoSize = true, Margin = new Padding(3, 6, 8, 0) };
    private readonly NumericUpDown _nudVolume = PiecePanel.DbBox(-60, 12);
    private readonly CheckBox[] _chkInclude = new CheckBox[DrumPieces.Count];
    private readonly CheckBox _chkMics = new() { Text = "Microfones na prévia", AutoSize = true, Margin = new Padding(8, 6, 3, 0) };
    private readonly NumericUpDown _nudMicLevel = PiecePanel.DbBox(-60, 12);
    private readonly Label _lblStatus = PiecePanel.Lbl("");
    private bool _loading;

    public SimulationPanel()
    {
        Text = "Simulação (sem ASIO)";
        AutoSize = true;
        Padding = new Padding(8, 4, 8, 4);

        _cmbSource.Items.AddRange(["Kit sintético", "4 WAVs de microfone", "WAV multicanal"]);
        _cmbSource.SelectedIndex = 0;
        foreach (var b in FakeHostOptions.BlockSizes) _cmbBlock.Items.Add(b);
        _cmbBlock.SelectedItem = 256;
        _loading = true;
        PiecePanel.SetNud(_nudVolume, PreviewMixSettings.Default.VolumeDb);
        PiecePanel.SetNud(_nudMicLevel, -12f);
        _loading = false;

        foreach (var p in DrumPieces.All)
        {
            int i = (int)p;
            _lblMic[i] = new Label { AutoSize = false, Width = 380, Height = 20, Margin = new Padding(3, 7, 3, 0), AutoEllipsis = true, Text = "silêncio" };
            var choose = new Button { Text = "Escolher...", AutoSize = true };
            var clear = new Button { Text = "Silêncio", AutoSize = true };
            choose.Click += (_, _) => ChooseMic(p);
            clear.Click += (_, _) => { _micPaths[i] = null; _lblMic[i].Text = "silêncio"; };
            _pnlMics.Controls.Add(PiecePanel.Row(new Label { Text = p.Label(), Width = 60, Margin = new Padding(3, 7, 3, 0) }, _lblMic[i], choose, clear));

            _nudChannel[i] = new NumericUpDown { Minimum = 0, Maximum = 64, Value = i + 1, Width = 50 };
            _chkInclude[i] = new CheckBox { Text = p.Label(), Checked = true, AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
            _chkInclude[i].CheckedChanged += (_, _) => RaisePreviewChanged();
        }
        var chooseMulti = new Button { Text = "Escolher...", AutoSize = true };
        chooseMulti.Click += (_, _) => ChooseMultichannel();
        var channels = new List<Control> { PiecePanel.Lbl("Canal do arquivo (0 = silêncio):") };
        foreach (var p in DrumPieces.All) { channels.Add(PiecePanel.Lbl(p.Label())); channels.Add(_nudChannel[(int)p]); }
        _pnlMulti.Controls.Add(PiecePanel.Row(PiecePanel.Lbl("Arquivo:"), _lblMulti, chooseMulti));
        _pnlMulti.Controls.Add(PiecePanel.Row(channels.ToArray()));

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.Add(PiecePanel.Row(PiecePanel.Lbl("Fonte dos microfones:"), _cmbSource, PiecePanel.Lbl("  Semente"), _nudSeed,
            _btnSyntheticKit, PiecePanel.Lbl("  Bloco (amostras)"), _cmbBlock, _chkLoop));
        flow.Controls.Add(_pnlMics);
        flow.Controls.Add(_pnlMulti);
        var include = new List<Control> { _chkPreview, PiecePanel.Lbl("Volume (dB)"), _nudVolume, PiecePanel.Lbl("  Peças:") };
        include.AddRange(_chkInclude);
        flow.Controls.Add(PiecePanel.Row(include.ToArray()));
        flow.Controls.Add(PiecePanel.Row(_chkMics, PiecePanel.Lbl("nível (dB)"), _nudMicLevel, _lblStatus));
        Controls.Add(flow);

        _cmbSource.SelectedIndexChanged += (_, _) => UpdateSourceUi();
        _btnSyntheticKit.Click += (_, _) => SyntheticKitRequested?.Invoke(this, Seed);
        _chkPreview.CheckedChanged += (_, _) => RaisePreviewChanged();
        _nudVolume.ValueChanged += (_, _) => RaisePreviewChanged();
        _chkMics.CheckedChanged += (_, _) => RaisePreviewChanged();
        _nudMicLevel.ValueChanged += (_, _) => RaisePreviewChanged();
        UpdateSourceUi();
    }

    /// <summary>Preview on/off or mix settings changed (apply live to a running host).</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>"Carregar kit sintético": load the synthetic scenario's samples and detection (argument = seed).</summary>
    public event EventHandler<int>? SyntheticKitRequested;

    public int Seed => (int)_nudSeed.Value;

    public bool PreviewEnabled => _chkPreview.Checked;

    public PreviewMixSettings Preview => new()
    {
        VolumeDb = (float)_nudVolume.Value,
        IncludePiece = _chkInclude.Select(c => c.Checked).ToArray(),
        MicLevelDb = _chkMics.Checked ? (float)_nudMicLevel.Value : null,
    };

    /// <summary>Host options from the controls (validated by the host at Start).</summary>
    public FakeHostOptions BuildOptions() => new()
    {
        Source = (SimulationSource)_cmbSource.SelectedIndex,
        Seed = Seed,
        MicPaths = _micPaths.ToArray(),
        MultichannelPath = _multiPath,
        MultichannelChannels = _nudChannel.Select(n => (int)n.Value).ToArray(),
        BlockSize = (int)_cmbBlock.SelectedItem!,
        Loop = _chkLoop.Checked,
        PreviewEnabled = PreviewEnabled,
        Preview = Preview,
    };

    /// <summary>Source controls are locked while the simulation runs (they apply at start).</summary>
    public void SetRunning(bool running) =>
        _cmbSource.Enabled = _nudSeed.Enabled = _cmbBlock.Enabled = _chkLoop.Enabled = _pnlMics.Enabled = _pnlMulti.Enabled = !running;

    public void SetStatus(string text, bool isError = false)
    {
        if (_lblStatus.Text == text) return;
        _lblStatus.Text = text;
        _lblStatus.ForeColor = isError ? Color.DarkRed : SystemColors.ControlText;
    }

    private void RaisePreviewChanged()
    {
        if (!_loading) PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSourceUi()
    {
        var src = (SimulationSource)_cmbSource.SelectedIndex;
        _pnlMics.Visible = src == SimulationSource.MicWavs;
        _pnlMulti.Visible = src == SimulationSource.MultichannelWav;
        _nudSeed.Enabled = src == SimulationSource.SyntheticKit;
    }

    private void ChooseMic(DrumPiece p)
    {
        using var dlg = new OpenFileDialog { Title = $"Microfone simulado: {p.Label()}", Filter = "Arquivos WAV (*.wav)|*.wav" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _micPaths[(int)p] = dlg.FileName;
        _lblMic[(int)p].Text = dlg.FileName;
    }

    private void ChooseMultichannel()
    {
        using var dlg = new OpenFileDialog { Title = "WAV multicanal (uma faixa por peça)", Filter = "Arquivos WAV (*.wav)|*.wav" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _multiPath = dlg.FileName;
        _lblMulti.Text = dlg.FileName;
    }
}
