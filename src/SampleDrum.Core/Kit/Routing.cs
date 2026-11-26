using System.Text.Json.Serialization;

namespace SampleDrum.Core.Kit;

/// <summary>
/// Direction seen from the PC. <see cref="Input"/> = desk → PC (USB send into the PC, an ASIO input);
/// <see cref="Output"/> = PC → desk (USB return from the PC, an ASIO output). Input USB 1 and output USB 1 are
/// different physical paths.
/// </summary>
public enum ChannelDirection { Input, Output }

/// <summary>USB channels of one piece in desk numbering (1..32); null = "pendente de configuração".</summary>
public sealed record PieceRoute(int? InputDeskChannel, int? OutputDeskChannel)
{
    /// <summary>True when the input or the output is not defined yet.</summary>
    [JsonIgnore]
    public bool IsPending => InputDeskChannel is null || OutputDeskChannel is null;

    public static PieceRoute Pending { get; } = new(null, null);
}

/// <summary>A USB channel the app must never use (e.g. PC inputs USB 1/2 carrying OBS MixBus 13/14).</summary>
public sealed record ReservedChannel(ChannelDirection Direction, int DeskChannel, string Label);

/// <summary>Named routing of the four pieces plus reserved channels. Desk numbering (1-based) throughout.</summary>
public sealed record RoutingProfile
{
    public string Name { get; init; } = "";

    /// <summary>One route per piece, in <see cref="DrumPiece"/> order (missing entries are treated as pending).</summary>
    public IReadOnlyList<PieceRoute> Routes { get; init; } = [PieceRoute.Pending, PieceRoute.Pending, PieceRoute.Pending, PieceRoute.Pending];

    public IReadOnlyList<ReservedChannel> Reserved { get; init; } = [];

    /// <summary>Route of a piece (pending when missing).</summary>
    public PieceRoute this[DrumPiece p] => (int)p < Routes.Count && Routes[(int)p] is { } r ? r : PieceRoute.Pending;

    /// <summary>Copy with one route replaced.</summary>
    public RoutingProfile WithRoute(DrumPiece p, PieceRoute route)
    {
        var list = DrumPieces.All.Select(x => this[x]).ToArray();
        list[(int)p] = route ?? PieceRoute.Pending;
        return this with { Routes = list };
    }
}

/// <summary>Built-in routing profiles.</summary>
public static class RoutingProfiles
{
    public const string ChurchName = "Igreja";
    public const string SimulationName = "Simulação";

    /// <summary>Label of the OBS path that must never be used as an app input.</summary>
    public const string ObsLabel = "OBS MixBus 13/14";

    /// <summary>
    /// Church test scene: Bumbo in USB 3 / out USB 1 (→ Card In 1 → Aux 1). Caixa, Tom and Surdo are pending until
    /// the desk sends/returns are defined. PC inputs USB 1/2 are reserved for OBS (MixBus 13/14).
    /// </summary>
    public static RoutingProfile Church { get; } = new()
    {
        Name = ChurchName,
        Routes = [new PieceRoute(3, 1), PieceRoute.Pending, PieceRoute.Pending, PieceRoute.Pending],
        Reserved =
        [
            new ReservedChannel(ChannelDirection.Input, 1, ObsLabel),
            new ReservedChannel(ChannelDirection.Input, 2, ObsLabel),
        ],
    };

    /// <summary>
    /// Simulation (no desk): four defined pairs. Inputs USB 3/4/5/6 (keep clear of the OBS inputs 1/2),
    /// outputs USB 1/2/3/4. Same reserved inputs as the church, so a profile copied to the church stays valid.
    /// </summary>
    public static RoutingProfile Simulation { get; } = new()
    {
        Name = SimulationName,
        Routes = [new PieceRoute(3, 1), new PieceRoute(4, 2), new PieceRoute(5, 3), new PieceRoute(6, 4)],
        Reserved = Church.Reserved,
    };

    public static IReadOnlyList<RoutingProfile> BuiltIn { get; } = [Church, Simulation];
}

/// <summary>Kind of routing problem.</summary>
public enum RoutingIssueKind
{
    /// <summary>Input and/or output not defined: the piece cannot run on ASIO; the other pieces can (not an error).</summary>
    Pending,
    /// <summary>Channel outside 1..(channels exposed by the driver).</summary>
    OutOfRange,
    /// <summary>Two pieces read the same PC input.</summary>
    DuplicateInput,
    /// <summary>Two pieces write the same PC output.</summary>
    DuplicateOutput,
    /// <summary>Channel reserved for something else (e.g. OBS).</summary>
    Reserved,
}

/// <summary>One routing problem; <see cref="Message"/> is pt-BR.</summary>
public sealed record RoutingIssue(RoutingIssueKind Kind, DrumPiece? Piece, ChannelDirection? Direction, int? DeskChannel, string Message)
{
    /// <summary>
    /// The problem is on a piece whose route is incomplete (pending): that piece never runs, so the problem is only a
    /// warning on it and never blocks the pieces that do run.
    /// </summary>
    public bool OnPendingPiece { get; init; }

    /// <summary>
    /// Errors block start (they always involve a piece with a complete route); <see cref="RoutingIssueKind.Pending"/>
    /// and problems on pending pieces (<see cref="OnPendingPiece"/>) are warnings.
    /// </summary>
    public bool IsError => Kind != RoutingIssueKind.Pending && !OnPendingPiece;
}

/// <summary>Result of <see cref="RoutingValidator.Validate"/>.</summary>
public sealed class RoutingValidation
{
    private readonly bool[] _runnable;

    internal RoutingValidation(RoutingProfile profile, IReadOnlyList<RoutingIssue> issues, bool[] runnable)
    {
        Profile = profile;
        Issues = issues;
        _runnable = runnable;
    }

    public RoutingProfile Profile { get; }
    public IReadOnlyList<RoutingIssue> Issues { get; }

    /// <summary>True when there is at least one error (not counting pending pieces): the host must not start.</summary>
    public bool HasErrors => Issues.Any(i => i.IsError);

    /// <summary>Complete route without errors: the piece runs on ASIO.</summary>
    public bool IsRunnable(DrumPiece p) => _runnable[(int)p];

    public IReadOnlyList<DrumPiece> RunnablePieces => DrumPieces.All.Where(IsRunnable).ToArray();

    /// <summary>Issues of one piece.</summary>
    public IEnumerable<RoutingIssue> IssuesOf(DrumPiece p) => Issues.Where(i => i.Piece == p);

    /// <summary>Piece → ASIO indices (0-based) for runnable pieces; others are not routed.</summary>
    public ResolvedRouting Resolve()
    {
        var inputs = new int?[DrumPieces.Count];
        var outputs = new int?[DrumPieces.Count];
        foreach (var p in DrumPieces.All)
        {
            if (!IsRunnable(p)) continue;
            var r = Profile[p];
            inputs[(int)p] = ChannelMap.DeskToApiIndex(r.InputDeskChannel!.Value);
            outputs[(int)p] = ChannelMap.DeskToApiIndex(r.OutputDeskChannel!.Value);
        }
        return new ResolvedRouting(Profile.Name, inputs, outputs);
    }
}

/// <summary>
/// Validated routing in ASIO terms: per piece the 0-based input and output index, or null when the piece is not
/// routed (pending or blocked). Hosts open only the routed channels; unrouted pieces get silent inputs and their
/// returns are discarded.
/// </summary>
public sealed class ResolvedRouting
{
    private readonly int?[] _inputs;
    private readonly int?[] _outputs;

    public ResolvedRouting(string profileName, IReadOnlyList<int?> inputIndices, IReadOnlyList<int?> outputIndices)
    {
        if (inputIndices.Count != DrumPieces.Count || outputIndices.Count != DrumPieces.Count)
            throw new ArgumentException($"São necessárias {DrumPieces.Count} entradas por direção.");
        ProfileName = profileName ?? "";
        _inputs = inputIndices.ToArray();
        _outputs = outputIndices.ToArray();
        for (int i = 0; i < DrumPieces.Count; i++)
            if (_inputs[i] is null != _outputs[i] is null)
                throw new ArgumentException("Cada peça roteada precisa de entrada e saída.");
    }

    public string ProfileName { get; }

    /// <summary>0-based ASIO input index of the piece, or null.</summary>
    public int? InputIndex(DrumPiece p) => _inputs[(int)p];

    /// <summary>0-based ASIO output index of the piece, or null.</summary>
    public int? OutputIndex(DrumPiece p) => _outputs[(int)p];

    public bool IsRouted(DrumPiece p) => _inputs[(int)p] is not null;

    public IReadOnlyList<DrumPiece> RoutedPieces => DrumPieces.All.Where(IsRouted).ToArray();

    /// <summary>No piece routed.</summary>
    public static ResolvedRouting None { get; } = new("", new int?[DrumPieces.Count], new int?[DrumPieces.Count]);
}

/// <summary>Validates a <see cref="RoutingProfile"/>; inputs and outputs are checked per direction.</summary>
public static class RoutingValidator
{
    /// <param name="profile">Profile to check.</param>
    /// <param name="inputCount">ASIO inputs exposed by the driver (use 32 = X-USB before opening it).</param>
    /// <param name="outputCount">ASIO outputs exposed by the driver.</param>
    public static RoutingValidation Validate(RoutingProfile profile, int inputCount = ChannelMap.MaxChannels,
        int outputCount = ChannelMap.MaxChannels)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<RoutingIssue>();
        var blocked = new bool[DrumPieces.Count];
        var runnable = new bool[DrumPieces.Count];

        foreach (var p in DrumPieces.All)
        {
            var r = profile[p];
            if (r.InputDeskChannel is null || r.OutputDeskChannel is null)
            {
                string what = r.InputDeskChannel is null && r.OutputDeskChannel is null ? "entrada e saída pendentes"
                    : r.InputDeskChannel is null ? "entrada pendente" : "saída pendente";
                issues.Add(new RoutingIssue(RoutingIssueKind.Pending, p, null, null,
                    $"{p.Label()}: {what} de configuração; a peça não roda no ASIO (as outras rodam)."));
            }
        }

        CheckDirection(profile, ChannelDirection.Input, inputCount, issues, blocked);
        CheckDirection(profile, ChannelDirection.Output, outputCount, issues, blocked);

        foreach (var p in DrumPieces.All)
            runnable[(int)p] = !profile[p].IsPending && !blocked[(int)p];
        return new RoutingValidation(profile, issues, runnable);
    }

    private static void CheckDirection(RoutingProfile profile, ChannelDirection dir, int count,
        List<RoutingIssue> issues, bool[] blocked)
    {
        string dirText = dir == ChannelDirection.Input ? "entrada" : "saída";
        int limit = Math.Min(Math.Max(count, 0), ChannelMap.MaxChannels);
        var byChannel = new Dictionary<int, List<DrumPiece>>();

        // Problems on a pending piece (incomplete route) are warnings on that piece only: it never runs.
        void Add(RoutingIssueKind kind, DrumPiece p, int c, string message)
        {
            bool pending = profile[p].IsPending;
            issues.Add(new RoutingIssue(kind, p, dir, c, pending ? $"{message} (peça pendente: aviso, não impede as outras)" : message)
                { OnPendingPiece = pending });
            if (!pending) blocked[(int)p] = true;
        }

        foreach (var p in DrumPieces.All)
        {
            var r = profile[p];
            int? ch = dir == ChannelDirection.Input ? r.InputDeskChannel : r.OutputDeskChannel;
            if (ch is not int c) continue;

            if (c < 1 || c > limit)
            {
                Add(RoutingIssueKind.OutOfRange, p, c, $"{p.Label()}: {dirText} USB {c} fora da faixa (o driver tem {dirText}s 1 a {limit}).");
                continue;
            }

            foreach (var res in profile.Reserved)
                if (res.Direction == dir && res.DeskChannel == c)
                    Add(RoutingIssueKind.Reserved, p, c, $"{p.Label()}: {dirText} USB {c} é reservada ({res.Label}); escolha outro canal.");

            if (!byChannel.TryGetValue(c, out var list)) byChannel[c] = list = new List<DrumPiece>();
            list.Add(p);
        }

        var kind = dir == ChannelDirection.Input ? RoutingIssueKind.DuplicateInput : RoutingIssueKind.DuplicateOutput;
        string verb = dir == ChannelDirection.Input ? "leem a mesma entrada" : "escrevem na mesma saída";
        foreach (var (c, pieces) in byChannel)
        {
            if (pieces.Count < 2) continue;
            // Errors only between pieces that would really run (complete routes).
            var complete = pieces.Where(x => !profile[x].IsPending).ToList();
            if (complete.Count >= 2)
            {
                string names = string.Join(" e ", complete.Select(x => x.Label()));
                foreach (var p in complete) Add(kind, p, c, $"{names} {verb} USB {c}.");
            }
            foreach (var p in pieces.Where(x => profile[x].IsPending))
            {
                string others = string.Join(" e ", pieces.Where(x => x != p).Select(x => x.Label()));
                Add(kind, p, c, $"{p.Label()} usa a mesma {dirText} USB {c} que {others}; corrija antes de completar a rota.");
            }
        }
    }
}
