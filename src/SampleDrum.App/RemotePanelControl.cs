using System.Net;
using System.Net.NetworkInformation;
using Net.Codecrete.QrCodeGenerator;
using SampleDrum.Core.Kit;
using SampleDrum.Remote;

namespace SampleDrum.App;

/// <summary>
/// "Painel do celular" group of the main window: start/stop the phone panel, network + port, URL + QR code, pairing
/// code with countdown, paired phones (Remover / Remover todos) and "Permitir teste pelo celular". UI thread only;
/// server events from other threads are marshalled with BeginInvoke. Never touches audio or the kit.
/// </summary>
public sealed class RemotePanelControl : GroupBox
{
    private const string LocalOnly = "127.0.0.1";
    private const string AnyAddress = "0.0.0.0";

    private readonly PanelServer _server;
    private readonly bool _simulation;
    private readonly Button _btnToggle = new() { Text = "Ativar painel", AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f, FontStyle.Bold) };
    private readonly Label _lblStatus = PiecePanel.Lbl("Painel desligado (o áudio não depende dele).");
    private readonly ComboBox _cmbNet = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, AccessibleName = "Rede do painel" };
    private readonly Button _btnNetRefresh = new() { Text = "Atualizar redes", AutoSize = true };
    private readonly NumericUpDown _nudPort = new() { Minimum = 1024, Maximum = 65535, Width = 75, AccessibleName = "Porta do painel" };
    private readonly LinkLabel _lnkUrl = new() { AutoSize = true, Margin = new Padding(3, 7, 3, 0), Font = new Font(SystemFonts.DefaultFont.FontFamily, 11f, FontStyle.Bold) };
    private readonly Label _lblAltUrl = PiecePanel.Lbl("");
    private readonly PictureBox _picQr = new() { Width = 168, Height = 168, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Visible = false, AccessibleName = "QR code do painel" };
    private readonly Label _lblCode = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 20f, FontStyle.Bold), Margin = new Padding(3, 2, 3, 0), Text = "— — —" };
    private readonly Label _lblExpires = PiecePanel.Lbl("");
    private readonly Button _btnNewCode = new() { Text = "Gerar novo código", AutoSize = true };
    private readonly ListView _lstDevices = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Width = 520, Height = 96,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, AccessibleName = "Celulares autorizados",
    };
    private readonly Button _btnRemove = new() { Text = "Remover", AutoSize = true, Enabled = false };
    private readonly Button _btnRemoveAll = new() { Text = "Remover todos", AutoSize = true, Enabled = false };
    private readonly CheckBox _chkAllowTest = new() { Text = "Permitir teste pelo celular (o teste toca na mesa: pode sair no PA/transmissão)", AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private string? _qrText;
    private bool _busy;
    private bool _loading;

    public RemotePanelControl(PanelServer server, RemotePanelConfig initial, bool simulation)
    {
        _server = server;
        _simulation = simulation;
        Text = simulation ? "Painel do celular (simulação: nada é salvo na configuração da igreja)" : "Painel do celular";
        AutoSize = true;
        Dock = DockStyle.Top;
        Padding = new Padding(8, 4, 8, 4);
        _lstDevices.Columns.Add("Celular", 200);
        _lstDevices.Columns.Add("Pareado em", 140);
        _lstDevices.Columns.Add("Último acesso", 140);

        _loading = true;
        _nudPort.Value = initial.Port is >= 1024 and <= 65535 ? initial.Port : RemotePanelConfig.DefaultPort;
        _chkAllowTest.Checked = initial.AllowPhoneTest;
        BindAddress = initial.BindAddress;
        WantEnabled = initial.Enabled;
        PopulateNetworks(initial.BindAddress);
        _loading = false;

        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        left.Controls.Add(PiecePanel.Row(_btnToggle, _lblStatus));
        left.Controls.Add(PiecePanel.Row(PiecePanel.Lbl("Rede:"), _cmbNet, _btnNetRefresh, PiecePanel.Lbl("Porta:"), _nudPort));
        left.Controls.Add(PiecePanel.Row(PiecePanel.Lbl("Endereço:"), _lnkUrl));
        left.Controls.Add(PiecePanel.Row(_lblAltUrl));
        left.Controls.Add(PiecePanel.Row(PiecePanel.Lbl("Código de pareamento:"), _lblCode, _lblExpires, _btnNewCode));
        left.Controls.Add(PiecePanel.Row(PiecePanel.Lbl("Celulares autorizados:")));
        left.Controls.Add(PiecePanel.Row(_lstDevices, _btnRemove, _btnRemoveAll));
        left.Controls.Add(PiecePanel.Row(_chkAllowTest));
        left.Controls.Add(PiecePanel.Row(PiecePanel.Lbl(
            "Se o celular não abrir a página, libere a porta no Firewall do Windows (README, \"Painel do celular\"). Só HTTP em rede local confiável.")));
        var outer = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        outer.Controls.Add(left);
        outer.Controls.Add(_picQr);
        Controls.Add(outer);

        _btnToggle.Click += async (_, _) => await ToggleAsync();
        _btnNetRefresh.Click += (_, _) => PopulateNetworks(SelectedBind());
        _cmbNet.SelectedIndexChanged += (_, _) => { if (!_loading) { BindAddress = SelectedBind(); RaiseSettingsChanged(); } };
        _nudPort.ValueChanged += (_, _) => { if (!_loading) RaiseSettingsChanged(); };
        _btnNewCode.Click += (_, _) => { _server.Pairing.Regenerate(); RefreshCode(); };
        _chkAllowTest.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            LogMessage?.Invoke(_chkAllowTest.Checked ? "Painel: teste pelo celular PERMITIDO." : "Painel: teste pelo celular desligado.");
            RaiseSettingsChanged();
        };
        _lstDevices.SelectedIndexChanged += (_, _) => _btnRemove.Enabled = _lstDevices.SelectedItems.Count > 0;
        _btnRemove.Click += (_, _) => RemoveSelected();
        _btnRemoveAll.Click += (_, _) => RemoveAll();
        _lnkUrl.LinkClicked += (_, _) => OpenInBrowser();
        _timer.Tick += (_, _) => { RefreshCode(); RefreshDevicesIfVisible(); };

        _server.Pairing.CodeChanged += (_, _) => Post(RefreshCode);
        _server.Devices.Changed += (_, _) => Post(RefreshDevices);
        _server.RunningChanged += (_, _) => Post(UpdateUi);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Disposed += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _timer.Dispose();
        };
        _timer.Start();
        RefreshDevices();
        UpdateUi();
    }

    /// <summary>Operator changed Ativar/Parar, network, port or the phone test permission (persist + status).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>pt-BR lines for the main log.</summary>
    public event Action<string>? LogMessage;

    /// <summary>Last Ativar/Parar choice of the operator (persisted for the church config only).</summary>
    public bool WantEnabled { get; private set; }
    public int Port => (int)_nudPort.Value;
    public string? BindAddress { get; private set; }
    public bool AllowPhoneTest => _chkAllowTest.Checked;

    /// <summary>Startup: starts the panel when it was left on (never fails the app: errors are shown and logged).</summary>
    public async Task StartIfWantedAsync()
    {
        if (WantEnabled && !_server.IsRunning) await StartAsync(showDialog: false);
    }

    public async Task ToggleAsync()
    {
        if (_busy) return;
        if (_server.IsRunning)
        {
            _busy = true;
            UpdateUi();
            try { await _server.StopAsync(); }
            finally { _busy = false; }
            WantEnabled = false;
            RaiseSettingsChanged();
            UpdateUi();
        }
        else await StartAsync(showDialog: true);
    }

    private async Task StartAsync(bool showDialog)
    {
        var options = BuildOptions(out string? problem);
        if (options is null)
        {
            ShowError(problem!, showDialog);
            return;
        }
        _busy = true;
        UpdateUi();
        try
        {
            await _server.StartAsync(options);
            WantEnabled = true;
            if (options.AnyAddress)
                LogMessage?.Invoke("AVISO: painel ouvindo em TODAS as redes (0.0.0.0). Prefira escolher só a rede da igreja.");
            if (options.Address is null && !options.AnyAddress)
                LogMessage?.Invoke("Painel só neste PC (localhost): o celular não alcança este endereço.");
            RaiseSettingsChanged();
        }
        catch (PanelStartException ex)
        {
            ShowError(ex.Message, showDialog);
        }
        finally
        {
            _busy = false;
            UpdateUi();
        }
    }

    private void ShowError(string message, bool dialog)
    {
        _lblStatus.Text = "ERRO: " + message;
        _lblStatus.ForeColor = Color.DarkRed;
        LogMessage?.Invoke("ERRO no painel do celular: " + message);
        if (dialog) MessageBox.Show(FindForm(), message, "SampleDrum – painel do celular", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private PanelOptions? BuildOptions(out string? problem)
    {
        problem = null;
        var item = _cmbNet.SelectedItem as NetItem;
        if (item is null)
        {
            problem = "Nenhuma rede escolhida. Conecte o PC à rede e clique em \"Atualizar redes\".";
            return null;
        }
        return new PanelOptions
        {
            Port = Port,
            Address = item.Lan?.Address,
            PrefixLength = item.Lan?.PrefixLength ?? 24,
            AnyAddress = item.Any,
        };
    }

    private void PopulateNetworks(string? select)
    {
        bool wasLoading = _loading;
        _loading = true;
        _cmbNet.Items.Clear();
        var lans = LanInterfaces.List();
        foreach (var lan in lans) _cmbNet.Items.Add(new NetItem(lan, $"{lan.Name} – {lan.Address}", false));
        _cmbNet.Items.Add(new NetItem(null, "Só este PC (localhost, para teste)", false));
        _cmbNet.Items.Add(new NetItem(null, "Todas as redes (0.0.0.0) – não recomendado", true));
        int index = 0;
        for (int i = 0; i < _cmbNet.Items.Count; i++)
            if (((NetItem)_cmbNet.Items[i]!).Bind == select) { index = i; break; }
        if (select is not null && index == 0 && lans.Count > 0 && lans[0].Address.ToString() != select)
            LogMessage?.Invoke($"Painel: a rede salva ({select}) não está disponível agora; usando {lans[0].Address}.");
        _cmbNet.SelectedIndex = index;
        if (lans.Count == 0)
            LogMessage?.Invoke("Painel: nenhuma rede local (IPv4 privado) encontrada; só \"localhost\" está disponível.");
        else if (lans.Count > 1)
            LogMessage?.Invoke($"Painel: {lans.Count} redes encontradas ({string.Join(", ", lans.Select(l => l.Address))}); escolha a da igreja.");
        _loading = wasLoading;
        BindAddress = SelectedBind();
    }

    private string? SelectedBind() => (_cmbNet.SelectedItem as NetItem)?.Bind;

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Post(() =>
    {
        var lans = LanInterfaces.List();
        if (_server.IsRunning && _server.Options?.Address is { } bound && !IPAddress.IsLoopback(bound)
            && lans.All(l => !l.Address.Equals(bound)))
        {
            LogMessage?.Invoke($"AVISO: o endereço do painel ({bound}) sumiu (a rede mudou?). Pare o painel, escolha a rede de novo e " +
                               "ative; os celulares precisarão parear de novo se o endereço mudar.");
            _lblStatus.Text = "AVISO: a rede mudou; reative o painel na rede certa.";
            _lblStatus.ForeColor = Color.DarkOrange;
        }
    });

    private void UpdateUi()
    {
        bool running = _server.IsRunning;
        _btnToggle.Text = running ? "Parar painel" : "Ativar painel";
        _btnToggle.Enabled = !_busy;
        _cmbNet.Enabled = _nudPort.Enabled = _btnNetRefresh.Enabled = !running && !_busy;
        _btnNewCode.Enabled = running;
        if (running)
        {
            _lblStatus.Text = "Painel LIGADO: abra o endereço no celular (mesmo Wi-Fi) ou leia o QR code.";
            _lblStatus.ForeColor = Color.DarkGreen;
            _lnkUrl.Text = _server.Url ?? "";
            var o = _server.Options!;
            _lblAltUrl.Text = o.Address is null && !o.AnyAddress
                ? "(só neste PC: o celular não alcança localhost)"
                : $"Também: http://{Environment.MachineName.ToLowerInvariant()}.local:{o.Port}/  •  neste PC: http://localhost:{o.Port}/";
        }
        else if (!_lblStatus.Text.StartsWith("ERRO", StringComparison.Ordinal))
        {
            _lblStatus.Text = "Painel desligado (o áudio não depende dele).";
            _lblStatus.ForeColor = SystemColors.ControlText;
            _lnkUrl.Text = "";
            _lblAltUrl.Text = "";
        }
        else
        {
            _lnkUrl.Text = "";
            _lblAltUrl.Text = "";
        }
        RefreshCode();
    }

    private void RefreshCode()
    {
        if (!_server.IsRunning)
        {
            _lblCode.Text = "— — —";
            _lblExpires.Text = "";
            SetQr(null);
            return;
        }
        string code = _server.Pairing.Code;
        var left = _server.Pairing.Remaining;
        if (code.Length == 0)
        {
            _lblCode.Text = "Fechado";
            _lblExpires.Text = "Clique em Gerar novo código para parear outro celular.";
            SetQr(_server.Url);
            return;
        }
        _lblCode.Text = $"{code[..4]} {code[4..]}";
        _lblExpires.Text = $"vence em {(int)left.TotalMinutes}:{left.Seconds:00} (uso único)";
        SetQr(_server.Url is { } url ? $"{url}#pair={code}" : null);
    }

    private void SetQr(string? text)
    {
        if (text == _qrText) return;
        _qrText = text;
        var old = _picQr.Image;
        _picQr.Image = text is null ? null : RenderQr(text);
        _picQr.Visible = text is not null;
        old?.Dispose();
    }

    /// <summary>QR code bitmap (Net.Codecrete.QrCodeGenerator, MIT): 4-module quiet zone, 4 px per module.</summary>
    internal static Bitmap RenderQr(string text)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        const int border = 4, scale = 4;
        int size = (qr.Size + 2 * border) * scale;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                if (qr.GetModule(x, y))
                    g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
        return bmp;
    }

    private int _deviceRefreshTick;

    private void RefreshDevicesIfVisible()
    {
        if (++_deviceRefreshTick % 5 == 0) RefreshDevices();   // last-seen column, every ~5 s
    }

    private void RefreshDevices()
    {
        var selected = _lstDevices.SelectedItems.Count > 0 ? (string?)_lstDevices.SelectedItems[0].Tag : null;
        var devices = _server.Devices.List();
        _lstDevices.BeginUpdate();
        _lstDevices.Items.Clear();
        foreach (var d in devices)
        {
            var item = new ListViewItem([d.Name, d.PairedAtUtc.ToLocalTime().ToString("dd/MM HH:mm"),
                d.LastSeenUtc?.ToLocalTime().ToString("dd/MM HH:mm:ss") ?? "—"]) { Tag = d.Key };
            _lstDevices.Items.Add(item);
            if (d.Key == selected) item.Selected = true;
        }
        _lstDevices.EndUpdate();
        _btnRemoveAll.Enabled = devices.Count > 0;
        _btnRemove.Enabled = _lstDevices.SelectedItems.Count > 0;
    }

    private void RemoveSelected()
    {
        if (_lstDevices.SelectedItems.Count == 0) return;
        var item = _lstDevices.SelectedItems[0];
        string name = item.Text;
        if (MessageBox.Show(FindForm(), $"Remover o celular \"{name}\"? Ele perde o acesso na hora e precisa parear de novo.",
                "SampleDrum – painel do celular", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (_server.Devices.Remove((string)item.Tag!)) LogMessage?.Invoke($"Painel: celular \"{name}\" removido.");
        RefreshDevices();
    }

    private void RemoveAll()
    {
        if (MessageBox.Show(FindForm(), "Remover TODOS os celulares autorizados? Todos perdem o acesso na hora.",
                "SampleDrum – painel do celular", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        int n = _server.Devices.RemoveAll();
        LogMessage?.Invoke($"Painel: {n} celular(es) removido(s).");
        RefreshDevices();
    }

    private void OpenInBrowser()
    {
        if (_server.Options is not { } o) return;
        try
        {
            // Opens this PC's own view through loopback (never the pairing code in a URL the PC logs).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://localhost:{o.Port}/") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogMessage?.Invoke("Não foi possível abrir o navegador: " + ex.Message);
        }
    }

    private void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private sealed record NetItem(LanInterface? Lan, string Label, bool Any)
    {
        public string Bind => Any ? AnyAddress : Lan?.Address.ToString() ?? LocalOnly;
        public override string ToString() => Label;
    }
}
