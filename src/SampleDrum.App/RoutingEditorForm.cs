using SampleDrum.Core;
using SampleDrum.Core.Kit;

namespace SampleDrum.App;

/// <summary>
/// Edits the USB input/output of each piece in a routing profile (desk numbering), with live validation.
/// Reserved channels are shown but not editable here.
/// </summary>
public sealed class RoutingEditorForm : Form
{
    private readonly ComboBox[] _inputs = new ComboBox[DrumPieces.Count];
    private readonly ComboBox[] _outputs = new ComboBox[DrumPieces.Count];
    private readonly ListBox _issues = new() { Width = 640, Height = 110, IntegralHeight = false };
    private readonly Label _summary = PiecePanel.Lbl("");
    private readonly RoutingProfile _original;
    private bool _loading;

    public RoutingEditorForm(RoutingProfile profile)
    {
        _original = profile ?? throw new ArgumentNullException(nameof(profile));
        Profile = profile;
        Text = $"Rotas do perfil \"{profile.Name}\"";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        flow.Controls.Add(PiecePanel.Lbl(
            "Numeração da mesa (USB 1..32). Entrada = mesa → PC (envio USB); saída = PC → mesa (retorno USB).\r\n" +
            "\"pendente\" = ainda não configurado na mesa: a peça não roda no ASIO, as outras rodam."));
        var reserved = profile.Reserved.Count == 0 ? "nenhum"
            : string.Join("; ", profile.Reserved.Select(r =>
                $"{(r.Direction == ChannelDirection.Input ? "entrada" : "saída")} USB {r.DeskChannel} ({r.Label})"));
        flow.Controls.Add(PiecePanel.Lbl($"Canais reservados (o app nunca usa): {reserved}"));

        foreach (var p in DrumPieces.All)
        {
            int i = (int)p;
            _inputs[i] = ChannelCombo();
            _outputs[i] = ChannelCombo();
            flow.Controls.Add(PiecePanel.Row(
                new Label { Text = $"{p.Label()} (CH {p.DeskMicChannel()})", Width = 130, Margin = new Padding(3, 7, 3, 0) },
                PiecePanel.Lbl("Entrada"), _inputs[i], PiecePanel.Lbl("  Saída"), _outputs[i]));
            _inputs[i].SelectedIndexChanged += (_, _) => OnChanged();
            _outputs[i].SelectedIndexChanged += (_, _) => OnChanged();
        }
        flow.Controls.Add(_summary);
        flow.Controls.Add(_issues);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var reset = new Button { Text = "Restaurar padrão de fábrica", AutoSize = true };
        var builtIn = RoutingProfiles.BuiltIn.FirstOrDefault(b => b.Name == profile.Name);
        reset.Enabled = builtIn is not null;
        reset.Click += (_, _) => { if (builtIn is not null) Show(builtIn); };
        flow.Controls.Add(PiecePanel.Row(ok, cancel, reset));
        Controls.Add(flow);
        AcceptButton = ok;
        CancelButton = cancel;

        Show(profile);
    }

    /// <summary>The edited profile (valid after OK).</summary>
    public RoutingProfile Profile { get; private set; }

    private static ComboBox ChannelCombo()
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        c.Items.Add("pendente");
        for (int ch = 1; ch <= ChannelMap.MaxChannels; ch++) c.Items.Add($"USB {ch} (índice ASIO {ch - 1})");
        return c;
    }

    private void Show(RoutingProfile profile)
    {
        _loading = true;
        try
        {
            foreach (var p in DrumPieces.All)
            {
                var r = profile[p];
                _inputs[(int)p].SelectedIndex = r.InputDeskChannel ?? 0;
                _outputs[(int)p].SelectedIndex = r.OutputDeskChannel ?? 0;
            }
        }
        finally { _loading = false; }
        OnChanged();
    }

    private void OnChanged()
    {
        if (_loading) return;
        var prof = _original with { Routes = DrumPieces.All.Select(p => new PieceRoute(
            _inputs[(int)p].SelectedIndex is > 0 and var i ? i : null,
            _outputs[(int)p].SelectedIndex is > 0 and var o ? o : null)).ToArray() };
        Profile = prof;
        var v = RoutingValidator.Validate(prof);
        _issues.Items.Clear();
        foreach (var issue in v.Issues) _issues.Items.Add((issue.IsError ? "ERRO: " : "Aviso: ") + issue.Message);
        _summary.Text = v.HasErrors
            ? "Há erros: o áudio não inicia até corrigir (veja a lista)."
            : $"OK: {v.RunnablePieces.Count} peça(s) com rota completa" +
              (v.RunnablePieces.Count < DrumPieces.Count ? "; as pendentes ficam sem ASIO." : ".");
        _summary.ForeColor = v.HasErrors ? Color.DarkRed : Color.DarkGreen;
    }
}
