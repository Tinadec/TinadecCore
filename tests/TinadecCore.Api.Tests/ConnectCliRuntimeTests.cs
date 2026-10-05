using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

/// <summary>
/// HTTP tests for POST /api/v1/model-providers/harnesses/connect.
/// <para>
/// These replaced a set that asserted the opposite claim: that a <c>claude-cli</c> provider was
/// connected because an HTTP URL answered, with <c>protocol: "acp"</c> inferred from the driver
/// string. That coupling is what made the ACP channel unconnectable — none of those binaries serve
/// HTTP — so the surviving tests pin the refusal instead, and pin the one HTTP-backed harness shape
/// (<c>opencode serve</c>) that genuinely works.
/// </para>
/// </summary>
public sealed class ConnectCliRuntimeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-cli-connect", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;
    private FakeOpenCodeServer? _server;
    private string _bin = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_bin);
        _factory = new Factory(_root);
        _server = await FakeOpenCodeServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>
    /// A driver the harness catalog does not recognize must be refused before Core touches the
    /// binary. The path points at an existing but non-executable file on purpose: had connect tried
    /// to spawn it, the response would be 502 CLI_CONNECT_FAILED rather than this 400, so the status
    /// code itself is the evidence that no process was started.
    /// </summary>
    [Theory]
    [InlineData("claude-cli")]
    [InlineData("codex-cli")]
    [InlineData("cursor-acp")]
    public async Task Connect_UnrecognizedDriver_IsRefusedBeforeSpawning(string driver)
    {
        var binary = Path.Combine(_bin, $"{driver}-not-executable");
        File.WriteAllText(binary, "not a program\n");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver,
            binary_path = binary,
            server_url = _server!.Url
        }));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("CLI_CONNECT_INVALID", body.GetProperty("code").GetString());
        Assert.Contains(driver, body.GetProperty("message").GetString(), StringComparison.Ordinal);

        var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers")).RootElement;
        Assert.Empty(list.EnumerateArray().Where(x => x.GetProperty("driver").GetString() == driver));
    }

    [Fact]
    public async Task Connect_ReachableHttpServer_PersistsEnabledProviderWithoutAFabricatedPortFlag()
    {
        var binary = Path.Combine(_bin, "opencode.exe");
        File.WriteAllText(binary, "");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "opencode",
            protocol = "opencode-serve",
            display_name = "OpenCode",
            binary_path = binary,
            server_url = _server!.Url
        }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("opencode", body.GetProperty("driver").GetString());
        Assert.Equal("cli", body.GetProperty("connection_kind").GetString());
        Assert.Equal("opencode-serve", body.GetProperty("protocol").GetString());
        Assert.Equal(_server.Url, body.GetProperty("server_url").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.True(body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out _));

        // The old fallback invented a port flag for anything that was not opencode serve. No stored
        // launch args may carry a flag the harness never mentioned.
        var launchArgs = body.TryGetProperty("launch_args", out var args) && args.ValueKind == JsonValueKind.String
            ? args.GetString()
            : null;
        Assert.DoesNotContain("acp-port", launchArgs, StringComparison.Ordinal);

        var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers")).RootElement;
        var row = list.EnumerateArray().Single(x => x.GetProperty("driver").GetString() == "opencode");
        Assert.Equal(_server.Url, row.GetProperty("server_url").GetString());

        var again = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "opencode",
            protocol = "opencode-serve",
            binary_path = binary,
            server_url = _server.Url
        }));
        var againBody = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(body.GetProperty("id").GetString(), againBody.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    [InlineData("zcode")]
    [InlineData("codebuddy")]
    [InlineData("dsh")]
    public async Task Connect_HeadlessChannel_PersistsTheSelectedCliRoute(string driver)
    {
        var binary = WriteRunnableStub(driver);
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver,
            channel = "cli",
            protocol = "headless-cli",
            binary_path = binary,
            display_name = driver
        }));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(driver, body.GetProperty("driver").GetString());
        Assert.Equal("cli", body.GetProperty("channel").GetString());
        Assert.Equal("headless-cli", body.GetProperty("protocol").GetString());
        Assert.Equal(Path.GetFullPath(binary), Path.GetFullPath(body.GetProperty("binary_path").GetString()!));
        Assert.True(body.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Connect_SameHarnessOnAnotherChannel_PreservesBothProviderRows()
    {
        var binary = WriteRunnableStub("codebuddy");
        var client = _factory!.CreateClient();
        var acp = await client.PostAsync("/api/v1/model-providers", JsonContent(new
        {
            driver = "codebuddy",
            channel = "acp",
            protocol = "acp",
            connection_kind = "cli",
            binary_path = binary,
            enabled = true
        }));
        Assert.True(acp.IsSuccessStatusCode, await acp.Content.ReadAsStringAsync());

        var cli = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "codebuddy",
            channel = "cli",
            protocol = "headless-cli",
            binary_path = binary
        }));
        Assert.True(cli.IsSuccessStatusCode, await cli.Content.ReadAsStringAsync());

        var rows = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers")).RootElement
            .EnumerateArray().Where(row => row.GetProperty("driver").GetString() == "codebuddy").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.GetProperty("channel").GetString() == "acp");
        Assert.Contains(rows, row => row.GetProperty("channel").GetString() == "cli");

        var discovery = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers/harnesses/discover")).RootElement;
        var candidate = discovery.GetProperty("cli_runtimes").EnumerateArray().Single(row => row.GetProperty("driver").GetString() == "codebuddy");
        Assert.Equal(new[] { "acp", "cli" }, candidate.GetProperty("configured_channels").EnumerateArray().Select(value => value.GetString()).OrderBy(value => value).ToArray());
    }

    [Fact]
    public async Task Connect_HttpServerShape_UnreachableRuntime_FailsWith502()
    {
        var binary = Path.Combine(_bin, "opencode.cmd");
        File.WriteAllText(binary, "@exit 1\r\n");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "opencode",
            protocol = "opencode-serve",
            binary_path = binary,
            server_url = "http://127.0.0.1:59999"
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("CLI_CONNECT_FAILED", body.GetProperty("code").GetString());
    }

    /// <summary>
    /// The routes used to be named `…/cli/discover` and `…/cli/connect` while serving all three harness
    /// channels. They are renamed, not aliased: this product has no compatibility routes, so the old
    /// paths must answer 404 rather than quietly keep working and let two spellings drift apart.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/model-providers/cli/discover")]
    [InlineData("/api/v1/model-providers/cli/connect")]
    public async Task RetiredCliNamedRoutes_Return404(string path)
    {
        var client = _factory!.CreateClient();
        var response = await client.PostAsync(path, JsonContent(new { driver = "opencode" }));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Connect_InvalidInput_Returns400()
    {
        var client = _factory!.CreateClient();

        var missing = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "opencode",
            protocol = "opencode-serve",
            binary_path = Path.Combine(_bin, "does-not-exist.exe")
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("CLI_CONNECT_INVALID", JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        var notCli = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "openai",
            binary_path = Path.Combine(_bin, "openai.exe")
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, notCli.StatusCode);
    }

    [Fact]
    public async Task Connect_PersistedProviderShowsAsConfiguredInDiscovery()
    {
        var binary = Path.Combine(_bin, "opencode.cmd");
        File.WriteAllText(binary, "@echo ok\r\n");
        var client = _factory!.CreateClient();

        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "opencode",
            protocol = "opencode-serve",
            binary_path = binary,
            server_url = _server!.Url
        }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var discovery = JsonDocument.Parse(await client.GetStringAsync("/api/v1/model-providers/harnesses/discover")).RootElement;
        var opencode = discovery.GetProperty("cli_runtimes").EnumerateArray().Single(x => x.GetProperty("driver").GetString() == "opencode");
        Assert.Equal("configured", opencode.GetProperty("status").GetString());
    }

    /// <summary>
    /// Discovery used to advertise a fabricated port flag as cursor's launch args, which connect
    /// then persisted. No detected candidate may be reported with a flag its harness does not
    /// implement, so this runs against a stub that actually resolves — a candidate Core never
    /// reaches is a candidate whose launch args would have been omitted and proved nothing.
    /// </summary>
    [Fact]
    public async Task Connect_CataloguedHarnessWithoutChannelOrProtocol_IsRefusedByName()
    {
        // A harness can be reached several ways. Defaulting one of them silently is how a connect for
        // `opencode` could end up stored as an ACP session, or the reverse, without anyone choosing.
        var binary = WriteRunnableStub("dsh");
        var client = _factory!.CreateClient();
        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "dsh",
            binary_path = binary
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("harness_channel_required", body.GetProperty("code").GetString());
        Assert.Contains("acp", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_ProtocolTheHarnessDoesNotSpeak_IsRefusedByName()
    {
        // `claude --acp` was the original fabrication: a dialect this product never implemented for
        // that binary, stored as if it worked. Refusing it here is what keeps it out of the row.
        var binary = WriteRunnableStub("claude");
        var client = _factory!.CreateClient();
        var response = await client.PostAsync("/api/v1/model-providers/harnesses/connect", JsonContent(new
        {
            driver = "claude-code",
            binary_path = binary,
            protocol = "acp"
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("harness_protocol_unsupported", body.GetProperty("code").GetString());
        Assert.Contains("headless-cli", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_ReportsNoFabricatedLaunchArgsForDetectedHarnesses()
    {
        WriteRunnableStub("opencode");
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, [_bin]), scope.ServiceProvider);

        var detected = payload
            .GetProperty("cli_runtimes")
            .EnumerateArray()
            .Where(cli => cli.GetProperty("status").GetString() == "found")
            .ToList();
        Assert.NotEmpty(detected);
        foreach (var cli in detected)
        {
            Assert.True(cli.TryGetProperty("launch_args", out var args) && args.ValueKind == JsonValueKind.String,
                $"detected {cli.GetProperty("driver").GetString()} should report its launch args");
            Assert.DoesNotContain("acp-port", args.GetString(), StringComparison.Ordinal);
        }
    }

    /// <summary>Writes a stub the <c>--version</c> probe actually passes: a .cmd shim on Windows, an
    /// executable shell script elsewhere.</summary>
    private string WriteRunnableStub(string name)
    {
        var binary = Path.Combine(_bin, OperatingSystem.IsWindows() ? $"{name}.cmd" : name);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(binary, "@echo ok\r\n");
        }
        else
        {
            File.WriteAllText(binary, "#!/bin/sh\necho ok\n");
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return binary;
    }

    private static async Task<JsonElement> ReadBodyAsync(IResult result, IServiceProvider services)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

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
