using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Api.Tests;

/// <summary>opencode serve protocol fake: POST /session, POST /session/{id}/message (SSE), POST abort.</summary>
internal sealed class FakeOpenCodeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; private set; } = "";
    public int Sessions;
    public int Messages;

    private FakeOpenCodeServer(WebApplication app) => _app = app;

    public static async Task<FakeOpenCodeServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeOpenCodeServer(app);
        app.MapPost("/session", (HttpContext context) => server.CreateSessionAsync(context));
        app.MapPost("/session/{id}/message", (string id, HttpContext context) => server.SendMessageAsync(id, context));
        app.MapPost("/session/{id}/abort", () => Results.Ok());
        await app.StartAsync();
        server.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(a => a.StartsWith("http://127.0.0.1:"));
        return server;
    }

    private async Task CreateSessionAsync(HttpContext context)
    {
        Sessions++;
        await context.Response.WriteAsJsonAsync(new { id = "os-1" });
    }

    private async Task SendMessageAsync(string id, HttpContext context)
    {
        Messages++;
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync("event: message.part.updated\ndata: {\"part\":{\"type\":\"text\",\"text\":\"Hello from fake opencode\"}}\n\n");
        await context.Response.WriteAsync("event: message.completed\ndata: {}\n\n");
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await _app.StopAsync(stopCts.Token);
        }
        await _app.DisposeAsync();
    }
}