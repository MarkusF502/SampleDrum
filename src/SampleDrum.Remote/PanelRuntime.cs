using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SampleDrum.Remote;

/// <summary>State of one running server (created by StartAsync, disposed by StopAsync).</summary>
internal sealed class PanelRuntime : IDisposable
{
    public const long MaxBodyBytes = 8 * 1024;

    public PanelRuntime(PanelServer server, PanelOptions options)
    {
        Server = server;
        Options = options;
        Hub = new PanelHub(this);
    }

    public PanelServer Server { get; }
    public PanelOptions Options { get; }
    public PanelHub Hub { get; }

    public void Start() => Hub.Start();

    public void Map(WebApplication app)
    {
        app.MapGet("/", () => NotImplemented());
        app.MapGet("/assets/{**path}", (string path) => NotImplemented());
        app.MapPost("/api/pair", () => NotImplemented());
        app.MapPost("/api/logout", () => NotImplemented());
        app.MapGet("/api/state", () => NotImplemented());
        app.MapGet("/api/events", () => NotImplemented());
        app.MapGet("/api/samples", () => NotImplemented());
        app.MapGet("/api/presets", () => NotImplemented());
        app.MapPost("/api/pieces/{piece}/sample", (string piece) => NotImplemented());
        app.MapPost("/api/kit/preset", () => NotImplemented());
        app.MapPost("/api/pieces/{piece}/test", (string piece) => NotImplemented());
        app.MapGet("/api/operations/{opId}", (string opId) => NotImplemented());
    }

    private static IResult NotImplemented() =>
        Results.Json(new ProblemDto("urn:sampledrum:not_implemented", "Ainda não implementado", 501, "Em construção.", "not_implemented"),
            Wire.Json, "application/problem+json", 501);

    public void Dispose() => Hub.Dispose();
}
