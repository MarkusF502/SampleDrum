using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SampleDrum.App.Harness;
using SampleDrum.Core.Kit;
using SampleDrum.Remote;

// MainForm and the harness tests share process-wide statics (StartupOptions.SimulationMode, SimulationLibraryRoot,
// the simulation library, MainForm.SettingsPath): run this assembly's tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SampleDrum.App.Tests;

/// <summary>
/// A whole --simulation session (PC sample + preset changes, phone pairing and a preset from the phone, panel on
/// loopback only) never writes the operator's settings.json: an existing file stays byte-identical, a missing one is
/// not created.
/// </summary>
[Collection("MainForm statics")]
public class SimulationIsolationTests
{
    [Fact]
    public void SimulationSession_LeavesExistingSettingsByteIdentical() => RunSession(createSettings: true);

    [Fact]
    public void SimulationSession_CreatesNoSettingsFile() => RunSession(createSettings: false);

    private static void RunSession(bool createSettings)
    {
        string dir = Directory.CreateTempSubdirectory("sd-sim-iso-").FullName;
        string settings = Path.Combine(dir, "settings.json");
        byte[]? before = null;
        if (createSettings)
        {
            var cfg = AppConfig.CreateDefault();
            cfg.SamplesFolder = Path.Combine(dir, "MeusSamples");
            cfg.PresetsFolder = Path.Combine(dir, "MeusPresets");
            cfg[DrumPiece.Kick].SamplePath = Path.Combine(dir, "MeusSamples", "bumbo-igreja.wav");
            cfg.ActivePresetId = "culto";
            cfg.RemotePanel.Enabled = true;
            cfg.RemotePanel.PairedDevices.Add(new PairedDeviceConfig
                { Name = "Celular da igreja", TokenSha256Hex = new string('a', 64), PairedAtUtc = DateTimeOffset.UnixEpoch });
            cfg.Save(settings);
            before = File.ReadAllBytes(settings);
        }

        string previousPath = MainForm.SettingsPath;
        bool previousSim = StartupOptions.SimulationMode;
        string previousRoot = StartupOptions.SimulationLibraryRoot;
        string libraryRoot = Path.Combine(dir, "biblioteca-simulacao");
        MainForm.SettingsPath = settings;
        StartupOptions.SimulationMode = true;
        StartupOptions.SimulationLibraryRoot = libraryRoot;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var form = new MainForm
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false, Opacity = 0,
                };
                form.Shown += async (_, _) =>
                {
                    try { await SessionAsync(form); }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { failure ??= ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "simulation session did not finish");
        }
        finally
        {
            MainForm.SettingsPath = previousPath;
            StartupOptions.SimulationMode = previousSim;
            StartupOptions.SimulationLibraryRoot = previousRoot;
        }
        if (failure is not null) throw new Xunit.Sdk.XunitException("session failed: " + failure);

        if (createSettings) Assert.Equal(before, File.ReadAllBytes(settings));
        else Assert.False(File.Exists(settings), "a --simulation run created settings.json");
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        Assert.False(Directory.Exists(libraryRoot), "simulation library not cleaned up");
        Directory.Delete(dir, true);
    }

    private static async Task SessionAsync(MainForm form)
    {
        await form.InitialLoad;
        var lib = StartupOptions.SimulationLibrary ?? throw new InvalidOperationException("no simulation library");
        var controller = form.Controller;
        Assert.Equal(Path.GetFullPath(lib.SamplesFolder), controller.Library.Folder);

        // PC paths (the same handlers the buttons use).
        var setSample = typeof(MainForm).GetMethod("SetPieceSampleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var applyPreset = typeof(MainForm).GetMethod("ApplyPresetAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var save = typeof(MainForm).GetMethod("SaveConfig", BindingFlags.NonPublic | BindingFlags.Instance)!;
        controller.Library.TryResolveInLibrary(lib.Samples.First(s => s.Piece == DrumPiece.Snare).Id, out var snarePath);
        await (Task)setSample.Invoke(form, [DrumPiece.Snare, snarePath])!;
        var okPreset = lib.Presets.First(p => p.Id != Core.Simulation.SimulationSampleLibrary.BrokenPresetId);
        await (Task)applyPreset.Invoke(form, [okPreset])!;
        Assert.Equal(okPreset.Id, controller.Snapshot.ActivePresetId);
        save.Invoke(form, null);

        // Phone: loopback-only panel (no firewall prompt), pairing, a preset from the phone.
        int port = FreePort();
        await form.PanelServer.StartAsync(new PanelOptions { Port = port });
        string origin = $"http://127.0.0.1:{port}";
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(origin + "/") };
        http.DefaultRequestHeaders.Add("Origin", origin);
        var pair = await http.PostAsync("api/pair", Json(new { code = form.PanelServer.Pairing.Code, deviceName = "celular de teste" }));
        Assert.Equal(HttpStatusCode.NoContent, pair.StatusCode);
        var state = (await http.GetFromJsonAsync<StateDto>("api/state", Wire.Json))!;
        Assert.True(state.Simulation);
        var other = lib.Presets.Last(p => p.Id != okPreset.Id && p.Id != Core.Simulation.SimulationSampleLibrary.BrokenPresetId);
        var accepted = await http.PostAsync("api/kit/preset",
            Json(new { presetId = other.Id, clientOpId = Guid.NewGuid(), baseVersion = state.Version }));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var opId = (await accepted.Content.ReadFromJsonAsync<OperationAccepted>(Wire.Json))!.OpId;
        for (int i = 0; i < 200; i++)
        {
            var op = (await http.GetFromJsonAsync<OperationDto>($"api/operations/{opId}", Wire.Json))!;
            if (op.Status == "applied") break;
            Assert.True(op.Status is "queued" or "preparing", op.Status);
            await Task.Delay(25);
        }
        Assert.Equal(other.Id, controller.Snapshot.ActivePresetId);
        await Task.Delay(800);                                    // debounced save timer would have fired by now
        save.Invoke(form, null);
    }

    private static StringContent Json(object o) =>
        new(System.Text.Json.JsonSerializer.Serialize(o, Wire.Json), Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
