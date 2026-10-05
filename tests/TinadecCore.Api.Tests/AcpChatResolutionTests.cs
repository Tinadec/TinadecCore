using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Models;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The resolution half of the ACP channel: a provider stored as a stdio harness has a
/// <c>binary_path</c> and no <c>server_url</c>, so every read path that still demands an endpoint
/// turns a correctly configured harness into an unavailable route. This is C2, and it is the failure
/// a user hits by binding an ACP harness to the chat route — the client layer can be perfect and the
/// run still dies before it is built.
/// </summary>
public sealed class AcpChatResolutionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-acp-resolution", Guid.NewGuid().ToString("N"));
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ChatRoute_OnAnAcpProvider_ResolvesWithoutAnyEndpoint()
    {
        var client = _factory!.CreateClient();
        var providerId = await AddProviderAsync(client, new
        {
            driver = "codebuddy",
            display_name = "CodeBuddy ACP",
            connection_kind = "cli",
            channel = "acp",
            binary_path = "/opt/codebuddy/codebuddy"
        });
        await BindChatRouteAsync(client, providerId);

        var resolution = await ResolveAsync();

        Assert.True(resolution.IsAvailable, $"resolution failed: {resolution.Error}");
        Assert.Equal(ChatProtocols.Acp, resolution.Protocol);
        Assert.Null(resolution.BaseUrl);
        Assert.Null(resolution.ServerUrl);
        Assert.Equal("/opt/codebuddy/codebuddy", resolution.BinaryPath);
        Assert.Equal(providerId, resolution.ProviderInstanceId);
        // The harness names its own model, so the route records the runtime and the model id falls
        // back to the driver rather than inventing a model the provider never listed.
        Assert.Equal("codebuddy/codebuddy", resolution.ModelId);
    }

    [Fact]
    public async Task ChatRoute_OnAnAcpProviderMissingItsBinary_NamesBinaryPath()
    {
        var client = _factory!.CreateClient();
        var providerId = await AddProviderAsync(client, new
        {
            driver = "kimi-code",
            display_name = "Kimi ACP without binary",
            connection_kind = "cli",
            channel = "acp"
        });
        await BindChatRouteAsync(client, providerId);

        var resolution = await ResolveAsync();

        Assert.False(resolution.IsAvailable);
        // An operator needs to know which key to fix. "endpoint is missing" sends them looking for a
        // URL this channel never has.
        Assert.Contains("binary_path", resolution.Error, StringComparison.Ordinal);
        Assert.Contains("acp", resolution.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other read path, and the one a run actually takes: <c>ModelInvocationChatFactory</c> turns a
    /// frozen plan's candidates into resolutions through <see cref="IAgentModelResolver"/>, which had
    /// its own copy of the "runtime owned means there is a URL" rule. Pinning both paths is the point —
    /// fixing only the route resolver would leave the run failing at the same place with the same
    /// message, and the earlier test would still pass.
    /// </summary>
    [Fact]
    public async Task InvocationCandidates_ForAnAcpProvider_BuildWithoutAnyEndpoint()
    {
        var client = _factory!.CreateClient();
        var providerId = await AddProviderAsync(client, new
        {
            driver = "dsh",
            display_name = "DeepSeek Harness ACP",
            connection_kind = "cli",
            channel = "acp",
            binary_path = "/opt/dsh/dsh"
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ModelControlDbContext>>();
        await using var context = await db.CreateDbContextAsync();
        var provider = await context.Providers.SingleAsync(x => x.Id == providerId);
        var plan = new FrozenModelPlan(ModelStrategyKinds.Route, "route",
            [new FrozenModelCandidate(0, provider.Id, provider.CurrentVersionId, null, ChatProtocols.Acp)]);

        var candidates = await scope.ServiceProvider.GetRequiredService<IAgentModelResolver>()
            .ResolveInvocationCandidatesAsync(plan, null);

        var resolution = Assert.Single(candidates);
        Assert.True(resolution.IsAvailable, $"resolution failed: {resolution.Error}");
        Assert.Equal(ChatProtocols.Acp, resolution.Protocol);
        Assert.Null(resolution.BaseUrl);
        Assert.Equal("/opt/dsh/dsh", resolution.BinaryPath);
        Assert.Equal(provider.Id, resolution.ProviderInstanceId);
    }

    private async Task<ChatResolution> ResolveAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IChatResolver>();
        return await resolver.ResolveChatAsync("chat");
    }

    private static async Task<Guid> AddProviderAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/api/v1/model-providers", payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"provider save failed: {response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Points the chat route at one provider with no model, as a harness binding looks like.</summary>
    private static async Task BindChatRouteAsync(HttpClient client, Guid providerId)
    {
        long? ifMatch = null;
        var routes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/model-routes");
        var chat = routes?.FirstOrDefault(r => r.GetProperty("purpose").GetString() == "chat");
        if (chat is not null && chat.Value.ValueKind == JsonValueKind.Object) ifMatch = chat.Value.GetProperty("revision").GetInt64();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/model-routes/chat")
        {
            Content = JsonContent.Create(new { candidates = new[] { new { provider_instance_id = providerId, model = (string?)null } } })
        };
        if (ifMatch is { } revision) request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"route write failed: {response.StatusCode} {body}");
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;

        public Factory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
