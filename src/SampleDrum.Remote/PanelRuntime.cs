using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote;

/// <summary>
/// One running server (created by StartAsync, disposed by StopAsync): request guard, endpoints over the app's
/// <see cref="KitController"/>, operation bookkeeping and the SSE hub. See docs/painel-api.md.
/// </summary>
internal sealed class PanelRuntime : IDisposable
{
    public const long MaxBodyBytes = 8 * 1024;
    public const string CookieName = "sd_device";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(400);
    private const string DeviceItem = "sd.device";
    private const string Csp = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
                               "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private readonly object _commandGate = new();
    private readonly CommandLimiter _limiter;
    private bool _subscribed;

    public PanelRuntime(PanelServer server, PanelOptions options)
    {
        Server = server;
        Options = options;
        Guard = new AccessGuard(options);
        Hub = new PanelHub(this);
        Operations = new WebOperations();
        _limiter = new CommandLimiter(server.Backend.Time);
    }

    public PanelServer Server { get; }
    public PanelOptions Options { get; }
    public AccessGuard Guard { get; }
    public PanelHub Hub { get; }
    public WebOperations Operations { get; }
    private PanelBackend Backend => Server.Backend;
    private KitController Controller => Backend.Controller;

    public void Start()
    {
        Controller.StateChanged += OnControllerChanged;
        Backend.Telemetry.Hit += Hub.OnHit;
        Backend.Devices.Revoked += OnRevoked;
        _subscribed = true;
        Hub.Start();
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            Controller.StateChanged -= OnControllerChanged;
            Backend.Telemetry.Hit -= Hub.OnHit;
            Backend.Devices.Revoked -= OnRevoked;
            _subscribed = false;
        }
        Hub.Dispose();
    }

    // Controller worker thread: enqueue only, never wait.
    private void OnControllerChanged(object? sender, EventArgs e) => Hub.OnControllerChanged();

    private void OnRevoked(string key)
    {
        Operations.RemoveDevice(key);
        Hub.Revoke(key);
    }

    // ---------------- pipeline ----------------

    public void Map(WebApplication app)
    {
        app.Use(GuardAsync);

        app.MapGet("/", (HttpContext ctx) => ServeAsset(ctx, "index.html"));
        app.MapGet("/assets/{**path}", (HttpContext ctx, string path) => ServeAsset(ctx, path));

        app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)PairAsync);
        app.MapPost("/api/logout", (Func<HttpContext, Task<IResult>>)LogoutAsync);
        app.MapGet("/api/state", (HttpContext ctx) => Json(BuildState(Device(ctx).Name)));
        app.MapGet("/api/events", (HttpContext ctx) => Hub.HandleAsync(ctx, Device(ctx)));
        app.MapGet("/api/samples", Samples);
        app.MapGet("/api/presets", Presets);
        app.MapPost("/api/pieces/{piece}/sample", PieceSampleAsync);
        app.MapPost("/api/kit/preset", (Func<HttpContext, Task<IResult>>)PresetAsync);
        app.MapPost("/api/pieces/{piece}/test", TestAsync);
        app.MapGet("/api/operations/{opId}", (HttpContext ctx, string opId) => Operation(opId));
        app.MapFallback((HttpContext ctx) => Problem(404, "not_found", "Não encontrado", "Rota inexistente."));
    }

    /// <summary>Host, network, method, Origin, Content-Type and pairing checks (order matters: cheapest first).</summary>
    private async Task GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        var req = ctx.Request;
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
        h["Content-Security-Policy"] = Csp;
        h.CacheControl = "no-store";

        if (!Guard.IsHostAllowed(req.Host.Value))
        { await WriteProblem(ctx, 403, "host_not_allowed", "Endereço não permitido", "Abra o painel pelo endereço mostrado no PC."); return; }
        if (!Guard.IsRemoteAllowed(ctx.Connection.RemoteIpAddress))
        { await WriteProblem(ctx, 403, "network_not_allowed", "Rede não permitida", "O celular precisa estar na mesma rede local do PC."); return; }
        if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsPost(req.Method))   // HEAD/OPTIONS/PUT...: 405
        { await WriteProblem(ctx, 405, "method_not_allowed", "Método não permitido", $"Método {req.Method} não é aceito."); return; }

        if (HttpMethods.IsPost(req.Method))
        {
            if (!Guard.IsOriginAllowed(req.Headers.Origin.ToString()))
            { await WriteProblem(ctx, 403, "origin_not_allowed", "Origem não permitida", "Pedido recusado: origem diferente do painel."); return; }
            if (!IsJson(req.ContentType))
            { await WriteProblem(ctx, 415, "unsupported_media_type", "Formato não aceito", "Envie Content-Type: application/json."); return; }
            if (req.ContentLength > MaxBodyBytes)
            { await WriteProblem(ctx, 413, "payload_too_large", "Pedido grande demais", $"Máximo {MaxBodyBytes} bytes."); return; }
        }

        string path = req.Path.Value ?? "";
        if (path.StartsWith("/api/", StringComparison.Ordinal) && path != "/api/pair")
        {
            var device = Backend.Devices.Validate(req.Cookies[CookieName]);
            if (device is null)
            { await WriteProblem(ctx, 401, "not_paired", "Celular não pareado", "Digite o código de pareamento mostrado no PC."); return; }
            ctx.Items[DeviceItem] = device;
        }

        await next(ctx);

        if (ctx.Response.StatusCode == 405 && !ctx.Response.HasStarted)
            await WriteProblem(ctx, 405, "method_not_allowed", "Método não permitido", $"Método {req.Method} não é aceito nesta rota.");
    }

    private static bool IsJson(string? contentType)
    {
        if (contentType is null) return false;
        var media = contentType.Split(';')[0].Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static PairedDevice Device(HttpContext ctx) => (PairedDevice)ctx.Items[DeviceItem]!;

    private static IResult ServeAsset(HttpContext ctx, string path)
    {
        var asset = StaticAssets.Get(path);
        if (asset is null) return Problem(404, "not_found", "Não encontrado", "Arquivo inexistente.");
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.ETag = asset.ETag;
        if (ctx.Request.Headers.IfNoneMatch.ToString() == asset.ETag) return Results.StatusCode(304);
        return Results.Bytes(asset.Content, asset.ContentType);
    }

    // ---------------- pairing ----------------

    private async Task<IResult> PairAsync(HttpContext ctx)
    {
        var (body, error) = await ReadBodyAsync<PairRequest>(ctx);
        if (error is not null) return error;
        var ip = ctx.Connection.RemoteIpAddress;
        string who = ip?.ToString() ?? "local";
        if (Backend.Devices.Count >= DeviceRegistry.MaxDevices)
            return Problem(409, "too_many_devices", "Aparelhos demais",
                $"Já há {DeviceRegistry.MaxDevices} celulares pareados. Remova algum no PC.");
        var result = Server.Pairing.TryPair(body!.Code, ip);
        switch (result.Outcome)
        {
            case PairingOutcome.Locked:
                int secs = Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds));
                ctx.Response.Headers.RetryAfter = secs.ToString();
                return Problem(429, "pairing_locked", "Muitas tentativas",
                    $"Muitas tentativas erradas. Espere {secs} s e confira o código no PC.", retryAfter: secs);
            case PairingOutcome.Closed:
                return Problem(401, "pairing_closed", "Pareamento fechado", "No PC, clique em Gerar novo código para parear este celular.");
            case PairingOutcome.Invalid:
                Server.Write($"Painel: código de pareamento errado vindo de {who}.");
                return Problem(401, "pairing_invalid", "Código inválido", "Código errado ou vencido. Confira o código no PC.");
        }
        var added = Backend.Devices.Add(body.DeviceName);
        if (added is null)
            return Problem(409, "too_many_devices", "Aparelhos demais", "Limite de celulares pareados atingido. Remova algum no PC.");
        ctx.Response.Cookies.Append(CookieName, added.Value.Token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Secure = false, MaxAge = CookieLifetime, IsEssential = true,
        });
        Server.Write($"Painel: celular \"{added.Value.Device.Name}\" pareado ({who}).");
        return Results.NoContent();
    }

    private async Task<IResult> LogoutAsync(HttpContext ctx)
    {
        var (_, error) = await ReadBodyAsync<JsonElement>(ctx);
        if (error is not null) return error;
        var device = Device(ctx);
        Backend.Devices.Remove(device.Key);
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict });
        Server.Write($"Painel: celular \"{device.Name}\" saiu (logout).");
        return Results.NoContent();
    }

    // ---------------- catalog ----------------

    private IResult Samples()
    {
        var list = Controller.Library.List()
            .Select(s => new SampleDto(s.Id, s.Name, (int)Math.Round(s.DurationSeconds * 1000), s.SampleRate, s.Channels))
            .ToArray();
        return Json(list);
    }

    private IResult Presets()
    {
        var library = Controller.Library;
        var presets = KitPreset.LoadFolder(Backend.PresetsFolder(), out _);
        return Json(presets.Where(p => p.Id is not null).Select(p => ToDto(p, library)).ToArray());
    }

    private static PresetDto ToDto(KitPreset preset, SampleLibrary library)
    {
        bool available = true;
        var pieces = new Dictionary<string, PresetPieceDto>();
        foreach (var piece in DrumPieces.All)
        {
            var pp = preset[piece];
            string? id = null, name = null;
            if (pp?.SamplePath is { Length: > 0 } path)
            {
                try
                {
                    string full = library.ResolvePath(path);
                    name = Path.GetFileNameWithoutExtension(full);
                    string rel = Path.GetRelativePath(library.Folder, full).Replace('\\', '/');
                    if (library.TryResolveInLibrary(rel, out _)) id = rel;
                    if (!File.Exists(full)) available = false;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    available = false;
                }
            }
            pieces[Wire.PieceId(piece)] = new PresetPieceDto(id, name, pp?.GainDb);
        }
        return new PresetDto(preset.Id!, preset.Name, pieces, available);
    }

    // ---------------- commands ----------------

    private async Task<IResult> PieceSampleAsync(HttpContext ctx, string piece)
    {
        if (!Wire.TryParsePiece(piece, out var p))
            return Problem(404, "unknown_piece", "Peça desconhecida", $"Peça \"{piece}\" não existe (use kick, snare, tom ou floorTom).");
        var (body, error) = await ReadBodyAsync<PieceSampleRequest>(ctx);
        if (error is not null) return error;
        var device = Device(ctx);
        if (!TryClientOpId(body!.ClientOpId, out string clientOpId)) return InvalidClientOpId();
        string fingerprint = $"sample|{Wire.PieceId(p)}|{body.SampleId}";
        lock (_commandGate)
        {
            if (Operations.Find(device.Key, clientOpId) is { } existing) return Repeat(existing, fingerprint);
            if (body.BaseVersion is not { } baseVersion || baseVersion < 0) return MissingBaseVersion();
            var library = Controller.Library;
            if (string.IsNullOrWhiteSpace(body.SampleId) || !library.TryResolveInLibrary(body.SampleId, out var full))
                return Problem(400, "invalid_sample_id", "Sample inválido", "Escolha um sample da lista.");
            if (!File.Exists(full))
                return Problem(404, "sample_not_found", "Sample não encontrado", "Esse sample não está mais na pasta do PC. Atualize a lista.");
            var snap = Controller.Snapshot;
            if (baseVersion > snap.Version || snap.Pieces[(int)p].SampleVersion > baseVersion)
                return Stale(device.Name, $"{p.Label()} mudou depois da versão {baseVersion}. Confira e toque de novo.");
            if (!_limiter.TryAcquire(device.Key, out var wait)) return RateLimited(ctx, wait);
            var op = Controller.TrySetPieceSample(p, body.SampleId, baseVersion);
            if (op is null) return Stale(device.Name, "Há uma troca nesta peça em andamento ou ela mudou. Confira e toque de novo.");
            var web = Operations.AddOrGet(new WebOp
            {
                OpId = op.Id, ClientOpId = clientOpId, DeviceKey = device.Key, Kind = WebOpKind.PieceSample,
                Fingerprint = fingerprint, Piece = p, SampleId = body.SampleId, Operation = op, CreatedAt = Backend.Time.GetUtcNow(),
            });
            Server.Write($"Painel: \"{device.Name}\" pediu {p.Label()} → {Path.GetFileNameWithoutExtension(full)}.");
            Hub.Watch(web);
            return Accepted(web);
        }
    }

    private async Task<IResult> PresetAsync(HttpContext ctx)
    {
        var (body, error) = await ReadBodyAsync<PresetRequest>(ctx);
        if (error is not null) return error;
        var device = Device(ctx);
        if (!TryClientOpId(body!.ClientOpId, out string clientOpId)) return InvalidClientOpId();
        string fingerprint = $"preset|{body.PresetId}";
        lock (_commandGate)
        {
            if (Operations.Find(device.Key, clientOpId) is { } existing) return Repeat(existing, fingerprint);
            if (body.BaseVersion is not { } baseVersion || baseVersion < 0) return MissingBaseVersion();
            if (string.IsNullOrWhiteSpace(body.PresetId))
                return Problem(400, "invalid_preset_id", "Preset inválido", "Escolha um preset da lista.");
            var preset = KitPreset.LoadFolder(Backend.PresetsFolder(), out _)
                .FirstOrDefault(x => string.Equals(x.Id, body.PresetId, StringComparison.Ordinal));
            if (preset is null)
                return Problem(404, "preset_not_found", "Preset não encontrado", "Esse preset não está mais na pasta do PC. Atualize a lista.");
            var snap = Controller.Snapshot;
            if (baseVersion > snap.Version || snap.KitVersion > baseVersion)
                return Stale(device.Name, $"O kit mudou depois da versão {baseVersion}. Confira e toque de novo.");
            if (!_limiter.TryAcquire(device.Key, out var wait)) return RateLimited(ctx, wait);
            var op = Controller.TryApplyPreset(preset, baseVersion);
            if (op is null) return Stale(device.Name, "Há uma troca no kit em andamento ou ele mudou. Confira e toque de novo.");
            var web = Operations.AddOrGet(new WebOp
            {
                OpId = op.Id, ClientOpId = clientOpId, DeviceKey = device.Key, Kind = WebOpKind.Preset,
                Fingerprint = fingerprint, PresetId = preset.Id, Operation = op, CreatedAt = Backend.Time.GetUtcNow(),
            });
            Server.Write($"Painel: \"{device.Name}\" pediu o preset \"{preset.Name}\".");
            Hub.Watch(web);
            return Accepted(web);
        }
    }

    private async Task<IResult> TestAsync(HttpContext ctx, string piece)
    {
        if (!Wire.TryParsePiece(piece, out var p))
            return Problem(404, "unknown_piece", "Peça desconhecida", $"Peça \"{piece}\" não existe.");
        var (body, error) = await ReadBodyAsync<TestRequest>(ctx);
        if (error is not null) return error;
        var device = Device(ctx);
        if (!TryClientOpId(body!.ClientOpId, out string clientOpId)) return InvalidClientOpId();
        string fingerprint = $"test|{Wire.PieceId(p)}";
        lock (_commandGate)
        {
            if (Operations.Find(device.Key, clientOpId) is { } existing) return Repeat(existing, fingerprint);
            var status = Server.AppStatus;
            if (!status.AllowPhoneTest)
                return Problem(403, "test_not_allowed", "Teste desligado", "O teste pelo celular está desligado no PC.");
            if (!status.AudioRunning)
                return Problem(409, "audio_stopped", "Áudio parado", "O áudio está parado no PC: nada tocaria.");
            if (Backend.Engine.ActiveKit?[p].Samples is null)
                return Problem(409, "no_sample", "Peça sem sample", $"{p.Label()} não tem sample.");
            if (!_limiter.TryAcquire(device.Key, out var wait)) return RateLimited(ctx, wait);
            (Backend.RequestTest ?? (x => Backend.Engine.RequestTest(x, 1f)))(p);
            var web = Operations.AddOrGet(new WebOp
            {
                OpId = Guid.NewGuid(), ClientOpId = clientOpId, DeviceKey = device.Key, Kind = WebOpKind.Test,
                Fingerprint = fingerprint, Piece = p, CreatedAt = Backend.Time.GetUtcNow(),
            });
            Server.Write($"Painel: \"{device.Name}\" testou {p.Label()}.");
            Hub.Watch(web);
            return Accepted(web);
        }
    }

    private IResult Operation(string opId)
    {
        if (!Guid.TryParse(opId, out var id))
            return Problem(404, "operation_not_found", "Operação não encontrada", "Identificador inválido.");
        var web = Operations.Find(id);
        var dto = web is not null ? BuildOperation(web, web.Operation?.Snapshot)
            : Controller.GetOperation(id) is { } s ? BuildOperation(null, s) : null;
        return dto is null
            ? Problem(404, "operation_not_found", "Operação não encontrada", "A operação não existe ou já saiu do histórico.")
            : Json(dto);
    }

    private static bool TryClientOpId(string? value, out string normalized)
    {
        normalized = "";
        if (!Guid.TryParse(value, out var g)) return false;
        normalized = g.ToString("D");
        return true;
    }

    private static IResult Repeat(WebOp existing, string fingerprint) =>
        existing.Fingerprint == fingerprint ? Accepted(existing)
            : Problem(409, "client_op_reused", "Pedido repetido diferente", "Esse clientOpId já foi usado para outro pedido.");

    private static IResult Accepted(WebOp op) =>
        Results.Json(new OperationAccepted(op.OpId.ToString("D")), Wire.Json, "application/json", 202);

    private static IResult InvalidClientOpId() =>
        Problem(400, "invalid_client_op_id", "clientOpId inválido", "Envie um clientOpId (UUID) novo para cada pedido.");

    private static IResult MissingBaseVersion() =>
        Problem(400, "missing_base_version", "baseVersion ausente", "Envie o baseVersion do estado mostrado.");

    private static IResult RateLimited(HttpContext ctx, TimeSpan wait)
    {
        int secs = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        ctx.Response.Headers.RetryAfter = secs.ToString();
        return Problem(429, "rate_limited", "Pedidos demais", "Muitos pedidos seguidos. Espere um instante.", retryAfter: secs);
    }

    private IResult Stale(string deviceName, string detail) =>
        Results.Json(new ProblemDto("urn:sampledrum:stale_state", "Estado alterado em outro dispositivo", 409, detail,
            "stale_state", BuildState(deviceName)), Wire.Json, "application/problem+json", 409);

    // ---------------- DTO builders ----------------

    /// <summary>Full state for one device (controller snapshot + engine kit names + app status).</summary>
    public StateDto BuildState(string deviceName)
    {
        var snap = Controller.Snapshot;
        var kit = Backend.Engine.ActiveKit;
        var status = Server.AppStatus;
        var pieces = DrumPieces.All.Select(p =>
        {
            var ps = snap.Pieces[(int)p];
            PieceSampleDto? sample = null;
            if (ps.SamplePath is { } path)
            {
                var kp = kit?[p];
                bool same = kp?.SourcePath is not null && string.Equals(kp.SourcePath, path, StringComparison.OrdinalIgnoreCase);
                int? ms = same && kp!.Samples is { } s ? (int)Math.Round(s.Length * 1000.0 / Backend.Engine.SampleRate) : null;
                sample = new PieceSampleDto(ps.SampleId, same && kp!.Name is { } n ? n : Path.GetFileNameWithoutExtension(path), ms);
            }
            else if (kit?[p] is { Samples: { } s, SourcePath: null } unnamed)
            {
                // Prepared kit without files (e.g. the synthetic simulation kit).
                sample = new PieceSampleDto(null, unnamed.Name ?? "sample", (int)Math.Round(s.Length * 1000.0 / Backend.Engine.SampleRate));
            }
            bool routed = status.Routed.Count > (int)p && status.Routed[(int)p];
            return new PieceStateDto(Wire.PieceId(p), p.Label(), ps.Enabled, routed ? "ok" : "pending", sample,
                MathF.Round(ps.GainDb, 2), ps.SampleVersion, Operations.PendingFor(p)?.OpId.ToString("D"));
        }).ToArray();
        return new StateDto(snap.Version, snap.KitVersion, status.ProfileName, status.Simulation, status.AudioRunning,
            snap.KitEnabled, new KitInfoDto(kit?.Name ?? "", snap.ActivePresetId), pieces, status.AllowPhoneTest,
            new DeviceInfoDto(deviceName));
    }

    /// <summary>Operation DTO (web op and/or controller snapshot); null when neither is known.</summary>
    public OperationDto? BuildOperation(WebOp? web, KitOperationSnapshot? s)
    {
        var status = Server.AppStatus;
        if (web?.Kind == WebOpKind.Test)
            return new OperationDto(web.OpId.ToString("D"), web.ClientOpId, "test", Wire.PieceId(web.Piece!.Value), null, null,
                "applied", false, status.AudioRunning, [], null);
        if (s is null) return null;
        bool confirmed = s.Status == KitOperationStatus.Applied && status.AudioRunning &&
                         s.KitGeneration is { } g && Backend.Engine.AppliedKitGeneration >= g;
        string kind = web?.Kind switch
        {
            WebOpKind.PieceSample => "pieceSample",
            WebOpKind.Preset => "preset",
            _ => s.Kind switch
            {
                KitOperationKind.PieceSample => "pieceSample",
                KitOperationKind.Preset => "preset",
                KitOperationKind.PreparedKit => "kit",
                KitOperationKind.PieceEnabled => "pieceEnabled",
                KitOperationKind.PieceGain => "pieceGain",
                KitOperationKind.PieceSettings => "pieceSettings",
                KitOperationKind.KitEnabled => "kitEnabled",
                KitOperationKind.LibraryFolder => "libraryFolder",
                _ => "other",
            },
        };
        // Errors of operations that are not about one piece carry a placeholder piece in Core: report null.
        bool pieceErrors = s.Kind is KitOperationKind.PieceSample or KitOperationKind.Preset;
        var errors = s.Errors.Select(e => new OperationErrorDto(pieceErrors ? Wire.PieceId(e.Piece) : null,
            Wire.ErrorCode(e.Code), Wire.PhoneMessage(e))).ToArray();
        return new OperationDto(s.Id.ToString("D"), web?.ClientOpId, kind,
            s.Piece is { } pc ? Wire.PieceId(pc) : null, web?.SampleId, web?.PresetId,
            Wire.Status(s.Status), confirmed, status.AudioRunning, errors,
            s.Status == KitOperationStatus.Applied ? s.ResultVersion : null);
    }

    // ---------------- helpers ----------------

    private static IResult Json<T>(T value) => Results.Json(value, Wire.Json, "application/json; charset=utf-8");

    internal static IResult Problem(int status, string code, string title, string detail, int? retryAfter = null) =>
        Results.Json(new ProblemDto($"urn:sampledrum:{code}", title, status, detail, code, null, retryAfter),
            Wire.Json, "application/problem+json", status);

    internal static async Task WriteProblem(HttpContext ctx, int status, string code, string title, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(
            new ProblemDto($"urn:sampledrum:{code}", title, status, detail, code), Wire.Json));
    }

    /// <summary>Reads a bounded JSON object body. Any failure returns the problem to send.</summary>
    private static async Task<(T? Body, IResult? Error)> ReadBodyAsync<T>(HttpContext ctx)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        int total = 0;
        try
        {
            while (total < buffer.Length)
            {
                int n = await ctx.Request.Body.ReadAsync(buffer.AsMemory(total), ctx.RequestAborted);
                if (n == 0) break;
                total += n;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            return (default, Problem(413, "payload_too_large", "Pedido grande demais", $"Máximo {MaxBodyBytes} bytes."));
        }
        if (total > MaxBodyBytes)
            return (default, Problem(413, "payload_too_large", "Pedido grande demais", $"Máximo {MaxBodyBytes} bytes."));
        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, total));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
            var value = doc.RootElement.Deserialize<T>(Wire.Json);
            if (value is null) throw new JsonException("null");
            return (value, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return (default, Problem(400, "invalid_json", "JSON inválido", "O corpo precisa ser um objeto JSON válido."));
        }
    }
}
