using System.Net;
using System.Text;

namespace SampleDrum.Remote.Tests;

public class AuthTests
{
    [Fact]
    public async Task Pairing_SetsHttpOnlyStrictCookie_CodeIsSingleUse_AndTheTokenNeverLeaks()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = h.Client();
        string code = h.Server.Pairing.Code;
        var r = await c.PairAsync(code, "Celular do Lucas");
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.NotNull(c.Cookie);
        Assert.Equal(43, c.Cookie!.Length);                            // 256-bit base64url
        string sc = c.SetCookieHeader!.ToLowerInvariant();
        Assert.Contains("httponly", sc);
        Assert.Contains("samesite=strict", sc);
        Assert.Contains("path=/", sc);
        Assert.NotEqual(code, h.Server.Pairing.Code);                  // single use

        var device = Assert.Single(h.Devices.List());
        Assert.Equal("Celular do Lucas", device.Name);
        Assert.Equal(DeviceRegistry.Hash(c.Cookie), device.Key);       // only the hash is stored
        Assert.DoesNotContain(h.Devices.Export(), d => d.TokenSha256Hex.Contains(c.Cookie));

        var state = await c.StateAsync();
        Assert.Equal("Celular do Lucas", state.Device.Name);

        // Token never in a body, an SSE payload or a log line.
        string body = await (await c.GetAsync("/api/state")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(c.Cookie, body);
        await using (var sse = await c.EventsAsync())
        {
            var first = await sse.NextAsync("state");
            Assert.DoesNotContain(c.Cookie, first!.Data);
        }
        Assert.NotEmpty(h.Logs);
        Assert.All(h.Logs, l => { Assert.DoesNotContain(c.Cookie, l); Assert.DoesNotContain(code, l); });

        // The same code again is refused (already used).
        using var other = h.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PairAsync(code)).StatusCode);
    }

    [Fact]
    public async Task WrongCodes_401_ThenIpLockout429_WithRetryAfter()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = h.Client();
        string good = h.Server.Pairing.Code;
        string wrong = good == "000000" ? "111111" : "000000";
        for (int i = 0; i < PairingService.MaxFailuresPerIp; i++)
        {
            var r = await c.PairAsync(wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("pairing_invalid", (await r.ProblemAsync()).Code);
        }
        var locked = await c.PairAsync(h.Server.Pairing.Code);        // even the right code: locked now
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("pairing_locked", (await locked.ProblemAsync()).Code);
        Assert.Empty(h.Devices.List());
        // Malformed bodies do not pair either.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(c.Request(HttpMethod.Post, "/api/pair", rawBody: "[1]"))).StatusCode);
    }

    [Fact]
    public void Code_ExpiresAfterFiveMinutes_IsInvalidatedAfterTenFailures_AndGlobalLimitLocksEveryone()
    {
        var time = new ManualTime();
        var p = new PairingService(time);
        string c1 = p.Code;
        Assert.Equal(6, c1.Length);
        time.Advance(PairingService.Lifetime);
        Assert.Equal(PairingOutcome.Invalid, p.TryPair(c1, IPAddress.Parse("192.168.0.20")).Outcome);   // expired
        string c2 = p.Code;
        Assert.NotEqual(c1, c2);

        int changes = 0;
        p.CodeChanged += (_, _) => changes++;
        for (int i = 0; i < PairingService.MaxFailuresPerCode; i++)        // 10 failures from 10 different IPs
            Assert.Equal(PairingOutcome.Invalid, p.TryPair(c2 == "999999" ? "999998" : "999999", IPAddress.Parse($"192.168.0.{30 + i}")).Outcome);
        Assert.NotEqual(c2, p.Code);                                        // invalidated
        Assert.True(changes >= 1);
        Assert.Equal(PairingOutcome.Invalid, p.TryPair(c2, IPAddress.Parse("192.168.0.99")).Outcome);

        for (int i = 0; i < PairingService.MaxFailuresGlobal; i++) p.TryPair("x", IPAddress.Parse($"10.0.{i}.1"));
        var locked = p.TryPair(p.Code, IPAddress.Parse("10.1.1.1"));
        Assert.Equal(PairingOutcome.Locked, locked.Outcome);
        time.Advance(PairingService.Window);
        Assert.Equal(PairingOutcome.Paired, p.TryPair(p.Code, IPAddress.Parse("10.1.1.1")).Outcome);
        Assert.NotEqual("", p.Regenerate());
    }

    [Fact]
    public async Task ExpiredCode_OverHttp_Is401()
    {
        var time = new ManualTime();
        await using var h = await PanelTestHost.StartAsync(time: time);
        using var c = h.Client();
        string code = h.Server.Pairing.Code;
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PairAsync(code)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PairAsync(h.Server.Pairing.Code)).StatusCode);
    }

    [Theory]
    [InlineData("/api/state")]
    [InlineData("/api/samples")]
    [InlineData("/api/presets")]
    [InlineData("/api/events")]
    [InlineData("/api/operations/00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrInvalidCookie_Is401(string path)
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = h.Client();
        var r = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("not_paired", (await r.ProblemAsync()).Code);
        c.Cookie = DeviceRegistry.NewToken();                          // well-formed but unknown
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(path)).StatusCode);
        c.Cookie = "../../etc";
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(path)).StatusCode);
        // Commands too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SetSampleAsync("kick", "kick-a.wav", 0)).StatusCode);
    }

    [Fact]
    public async Task Revocation_ClosesThatDevicesSse_Then401_OtherDeviceUnaffected()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var a = await h.PairedClientAsync("A");
        using var b = await h.PairedClientAsync("B");
        await using var sseA = await a.EventsAsync();
        await using var sseB = await b.EventsAsync();
        Assert.Equal("state", (await sseA.NextAsync())!.Event);
        Assert.Equal("state", (await sseB.NextAsync())!.Event);

        var keyA = h.Devices.List().Single(d => d.Name == "A").Key;
        Assert.True(h.Devices.Remove(keyA));                           // "Remover" on the PC
        var revoked = await sseA.NextAsync("revoked");
        Assert.NotNull(revoked);
        Assert.Null(await sseA.NextAsync(5000));                       // stream closed by the server
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/state")).StatusCode);
        var reconnect = await a.EventsAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, reconnect.Response.StatusCode);
        await reconnect.DisposeAsync();

        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/state")).StatusCode);
        await h.Controller.SetPieceGain(Core.Kit.DrumPiece.Kick, -1).Completion;
        Assert.Equal("state", (await sseB.NextAsync("state"))!.Event);  // B still live

        Assert.Equal(1, h.Devices.RemoveAll());
        Assert.NotNull(await sseB.NextAsync("revoked"));
    }

    [Fact]
    public async Task Logout_ForgetsTheDevice_AndClearsTheCookie()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        string token = c.Cookie!;
        var r = await c.PostAsync("/api/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Null(c.Cookie);                                         // cookie emptied
        Assert.Contains("1970", c.SetCookieHeader!);                   // and expired
        Assert.Empty(h.Devices.List());
        c.Cookie = token;
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/state")).StatusCode);
    }

    [Theory]
    [InlineData("evil.example:8750")]
    [InlineData("192.168.0.10:8750")]       // not the selected interface (loopback-only options)
    [InlineData("127.0.0.1:9999")]
    [InlineData("localhost")]
    public async Task ForeignHost_Is403_EvenForThePublicPage(string host)
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = h.Client();
        var req = c.Request(HttpMethod.Get, "/");
        req.Headers.Host = host;
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("host_not_allowed", (await r.ProblemAsync()).Code);
    }

    [Theory]
    [InlineData("localhost:8750")]
    [InlineData("127.0.0.1:8750")]
    [InlineData("pc-igreja.local:8750")]
    public async Task AllowedHosts_ServeThePage(string host)
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = h.Client();
        var req = c.Request(HttpMethod.Get, "/");
        req.Headers.Host = host;
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/html", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("default-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://evil.example")]
    [InlineData("http://127.0.0.1:8750.evil.example")]
    [InlineData("https://127.0.0.1:8750")]
    [InlineData("null")]
    public async Task PostWithoutAllowedOrigin_Is403(string? origin)
    {
        await using var h = await PanelTestHost.StartAsync();
        using var good = await h.PairedClientAsync();
        using var c = h.Client(origin);
        c.Cookie = good.Cookie;
        var r = await c.SetSampleAsync("kick", "kick-a.wav", 0);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("origin_not_allowed", (await r.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PairAsync(h.Server.Pairing.Code)).StatusCode);
        Assert.Single(h.Devices.List());
        Assert.Equal(0, h.Controller.Version);
    }

    [Fact]
    public async Task OnlyJsonBodies_NoCors()
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var body = "{\"sampleId\":\"kick-a.wav\",\"clientOpId\":\"" + Guid.NewGuid() + "\",\"baseVersion\":0}";
        foreach (var type in new[] { "text/plain", "application/x-www-form-urlencoded", "multipart/form-data" })
        {
            var r = await c.SendAsync(c.Request(HttpMethod.Post, "/api/pieces/kick/sample", rawBody: body, contentType: type));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
            Assert.Equal("unsupported_media_type", (await r.ProblemAsync()).Code);
        }
        foreach (var bad in new[] { "", "[]", "\"x\"", "{", "{\"baseVersion\":\"abc\"}" })
        {
            var r = await c.SendAsync(c.Request(HttpMethod.Post, "/api/pieces/kick/sample", rawBody: bad));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        var big = await c.SendAsync(c.Request(HttpMethod.Post, "/api/pieces/kick/sample",
            rawBody: "{\"sampleId\":\"" + new string('a', 9000) + "\"}"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);

        var options = c.Request(HttpMethod.Options, "/api/kit/preset");
        options.Headers.Add("Origin", "http://evil.example");
        options.Headers.Add("Access-Control-Request-Method", "POST");
        var pre = await c.SendAsync(options);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, pre.StatusCode);
        Assert.DoesNotContain(pre.Headers, kv => kv.Key.StartsWith("Access-Control", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(c.Request(HttpMethod.Put, "/api/state"))).StatusCode);
        Assert.Equal(0, h.Controller.Version);
    }

    [Fact]
    public void Registry_LoadsHashes_ValidatesTokens_AndCleansNames()
    {
        var token = DeviceRegistry.NewToken();
        var reg = new DeviceRegistry([new Core.Kit.PairedDeviceConfig
            { Name = "  Antigo\u0007 ", TokenSha256Hex = DeviceRegistry.Hash(token).ToUpperInvariant() }]);
        var d = reg.Validate(token);
        Assert.NotNull(d);
        Assert.Equal("Antigo", d!.Name);
        Assert.Null(reg.Validate(token[..42] + (token[42] == 'A' ? 'B' : 'A')));
        Assert.Equal("Celular", DeviceRegistry.CleanName("   "));
        Assert.Equal(DeviceRegistry.MaxNameLength, DeviceRegistry.CleanName(new string('x', 100)).Length);
        for (int i = 1; i < DeviceRegistry.MaxDevices; i++) Assert.NotNull(reg.Add("x"));
        Assert.Null(reg.Add("demais"));
        Assert.All(reg.Export(), e => Assert.Matches("^[0-9a-f]{64}$", e.TokenSha256Hex));
    }

    [Fact]
    public void Guard_AcceptsOnlyTheSelectedSubnetAndLoopback()
    {
        var g = new AccessGuard(new PanelOptions { Address = IPAddress.Parse("192.168.10.5"), PrefixLength = 24, Port = 8750, MachineName = "PC" });
        Assert.True(g.IsRemoteAllowed(IPAddress.Parse("192.168.10.77")));
        Assert.True(g.IsRemoteAllowed(IPAddress.Loopback));
        Assert.True(g.IsRemoteAllowed(IPAddress.Parse("::ffff:192.168.10.9")));
        Assert.False(g.IsRemoteAllowed(IPAddress.Parse("192.168.11.7")));
        Assert.False(g.IsRemoteAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(g.IsRemoteAllowed(IPAddress.Parse("fe80::1")));
        Assert.True(g.IsHostAllowed("192.168.10.5:8750"));
        Assert.True(g.IsHostAllowed("PC.local:8750"));
        Assert.False(g.IsHostAllowed("192.168.10.5"));
        Assert.True(g.IsOriginAllowed("http://192.168.10.5:8750"));
        Assert.False(g.IsOriginAllowed("http://192.168.10.5:8750/"));
    }
}
