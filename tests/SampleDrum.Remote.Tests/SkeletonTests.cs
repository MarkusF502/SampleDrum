using Microsoft.AspNetCore.TestHost;
using SampleDrum.Core.Kit;
using SampleDrum.Remote;

namespace SampleDrum.Remote.Tests;

public class SkeletonTests
{
    [Fact]
    public async Task Server_starts_in_memory_and_answers()
    {
        var dir = Directory.CreateTempSubdirectory("sd-remote-");
        try
        {
            var engine = new KitEngine(48000, KitSettings.Default);
            await using var controller = new KitController(engine, new SampleLibrary(dir.FullName));
            await using var server = new PanelServer(new PanelBackend
            {
                Controller = controller, Engine = engine, PresetsFolder = () => dir.FullName, Devices = new DeviceRegistry(),
            }) { ConfigureBuilder = b => b.WebHost.UseTestServer() };
            await server.StartAsync(new PanelOptions { Port = 8750 });
            Assert.True(server.IsRunning);
            using var client = server.App!.GetTestClient();
            var r = await client.GetAsync("/api/state");
            Assert.NotEqual(0, (int)r.StatusCode);
            await server.StopAsync();
            Assert.False(server.IsRunning);
        }
        finally { dir.Delete(true); }
    }
}
