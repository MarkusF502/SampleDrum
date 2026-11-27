using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SampleDrum.Core.Kit;

namespace SampleDrum.Remote.Tests;

/// <summary>Nothing outside the catalog (absolute paths, PC folders, the user profile) ever reaches the phone.</summary>
public class PrivacyTests
{
    private static readonly Regex DrivePath = new(@"[A-Za-z]:(\\\\|\\|/)", RegexOptions.Compiled);

    private static void AssertClean(string text, PanelTestHost h, string where)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var secret in new[] { h.Dir, h.Samples, h.Presets, profile, Path.GetTempPath().TrimEnd('\\') })
        {
            Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{where}: contains {secret}");
            Assert.False(text.Contains(secret.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), $"{where}: contains escaped {secret}");
            Assert.False(text.Contains(secret.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), $"{where}: contains {secret} with /");
        }
        Assert.False(DrivePath.IsMatch(text), $"{where}: drive-letter path in {text}");
        Assert.DoesNotContain("\\\\", text);                                          // no backslash paths (JSON-escaped)
        foreach (var p in DrumPieces.All)
            Assert.False(text.Contains($"{p.Label()}: ", StringComparison.Ordinal), $"{where}: piece label prefix in {text}");
    }

    [Fact]
    public async Task ErrorScenarios_NeverLeakPcPathsOrLabelsToThePhone()
    {
        await using var h = await PanelTestHost.StartAsync(audio: true);
        // A preset that is listed as available, then loses its kick file; one with an absolute path outside the library.
        h.Wav("kick-c.wav", 0.2f);
        h.Preset("fragil", "Frágil", "kick-c.wav", "caixas/caixa-1.wav");
        string outside = Path.Combine(h.Dir, "fora", "externo.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        Core.WavFile.WriteMono(outside, Enumerable.Repeat(0.3f, 480).ToArray(), PanelTestHost.Rate);
        h.Preset("externo", "Externo", outside, "caixas/caixa-1.wav");
        // The PC picks a file outside the library for the tom (allowed on the PC, must not be exposed).
        await h.Controller.SetPieceSamplePath(DrumPiece.Tom, outside).Completion;

        using var c = await h.PairedClientAsync();
        await using var sse = await c.EventsAsync();
        await sse.NextAsync("state");
        var bodies = new List<(string Where, string Body)>();
        async Task<string> Capture(string where, HttpResponseMessage r)
        {
            string body = await r.Content.ReadAsStringAsync();
            bodies.Add((where, body));
            return body;
        }

        var presetsBody = await Capture("presets", await c.GetAsync("/api/presets"));
        Assert.Contains("\"externo\"", presetsBody);
        await Capture("samples", await c.GetAsync("/api/samples"));
        var state = await c.StateAsync();
        Assert.Equal("externo", state.Pieces[2].Sample!.Name);
        Assert.Null(state.Pieces[2].Sample!.Id);
        await Capture("state", await c.GetAsync("/api/state"));

        File.Delete(Path.Combine(h.Samples, "kick-c.wav"));
        var r1 = await c.PresetAsync("fragil", state.Version);
        var op1 = (await r1.Content.ReadFromJsonAsyncSafe<OperationAccepted>())!.OpId;
        var failed = await c.WaitOperationAsync(op1, o => o.Status is "failed" or "applied");
        Assert.Equal("failed", failed.Status);
        var err = Assert.Single(failed.Errors);
        Assert.Equal("kick", err.Piece);
        Assert.Equal("file_not_found", err.Code);
        Assert.Contains("kick-c.wav", err.Message);                                   // the file NAME is fine
        await Capture("op fragil", await c.GetAsync($"/api/operations/{op1}"));

        var r2 = await c.SetSampleAsync("snare", "corrompido.wav", state.Version);
        var op2 = (await r2.Content.ReadFromJsonAsyncSafe<OperationAccepted>())!.OpId;
        await c.WaitOperationAsync(op2, o => o.Status is "failed" or "applied");
        await Capture("op corrupt", await c.GetAsync($"/api/operations/{op2}"));

        await Capture("stale", await c.SetSampleAsync("tom", "kick-a.wav", 9999));
        await Capture("outside id", await c.SetSampleAsync("kick", outside, state.Version));
        await Capture("traversal", await c.SetSampleAsync("kick", "../fora/externo.wav", state.Version));
        await Capture("missing", await c.SetSampleAsync("kick", "nao-existe.wav", state.Version));
        await Capture("preset 404", await c.PresetAsync("nao-existe", state.Version));
        await Capture("test 403", await c.PostAsync("/api/pieces/kick/test", new { clientOpId = Guid.NewGuid() }));
        await Capture("bad json", await c.SendAsync(c.Request(HttpMethod.Post, "/api/kit/preset", rawBody: "{")));
        await Capture("op 404", await c.GetAsync($"/api/operations/{Guid.NewGuid()}"));
        await Capture("route 404", await c.GetAsync("/api/C:/Windows"));

        // Collect whatever the SSE stream sent meanwhile (state, op, meters, hit).
        var sseText = new StringBuilder();
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var e = await sse.NextAsync(500);
                if (e is null) break;
                sseText.Append(e.Event).Append(' ').Append(e.Data).Append('\n');
            }
        }
        catch (TimeoutException) { }
        Assert.Contains("event", string.Join("\n", sse.RawLines));
        bodies.Add(("sse", string.Join("\n", sse.RawLines)));

        foreach (var (where, body) in bodies) AssertClean(body, h, where);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/assets/app.js")]
    [InlineData("/api/state")]
    [InlineData("/api/nada")]
    public async Task Head_Is405_LikeEveryUnlistedMethod(string path)
    {
        await using var h = await PanelTestHost.StartAsync();
        using var c = await h.PairedClientAsync();
        var r = await c.SendAsync(c.Request(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.SendAsync(c.Request(m, path))).StatusCode);
        Assert.Equal("method_not_allowed", (await (await c.SendAsync(c.Request(HttpMethod.Delete, path))).ProblemAsync()).Code);
    }

    [Theory]
    [InlineData(@"C:\Users\x\Samples\kick.wav", "kick.wav")]
    [InlineData("/home/x/kick.wav", "kick.wav")]
    [InlineData(@"\\server\share\a b.wav", "a b.wav")]
    [InlineData("C:kick.wav", "kick.wav")]
    [InlineData(null, null)]
    public void SafeFileName_KeepsOnlyTheName(string? path, string? expected) =>
        Assert.Equal(expected, Wire.SafeFileName(path));
}
