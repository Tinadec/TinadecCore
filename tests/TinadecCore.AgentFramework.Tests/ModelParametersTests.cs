using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness;

namespace TinadecCore.AgentFramework.Tests;

public sealed class ModelParametersTests
{
    [Theory]
    [InlineData("{\"m\":{\"temperature\":-1}}")]
    [InlineData("{\"m\":{\"temperature\":2.1}}")]
    [InlineData("{\"m\":{\"top_p\":1.1}}")]
    [InlineData("{\"m\":{\"max_output_tokens\":0}}")]
    [InlineData("{\"m\":{\"max_output_tokens\":2.5}}")]
    [InlineData("{\"m\":{\"reasoning_effort\":\"typo\"}}")]
    [InlineData("{\"m\":{\"temprature\":1}}")]
    [InlineData("{\"m\":null}")]
    [InlineData("{\" m\":{}}")]
    [InlineData("[]")]
    public void InvalidSettingsAreRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Record.Exception(() => ModelParameters.ReadMap(document.RootElement));
        Assert.True(error is ArgumentException or JsonException);
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh)]
    public void MapsEveryExposedEffort(string effort, ReasoningEffort expected)
    {
        var options = new ChatOptions();
        new ModelParameters { ReasoningEffort = effort }.ApplyTo(options, "test-model");
        Assert.Equal(expected, options.Reasoning?.Effort);
    }

    [Fact]
    public void ParametersBelongToTheirExactModel_AndEmptySettingsKeepRuntimeDefaults()
    {
        using var document = JsonDocument.Parse("""{"model_parameters":{"m":{"temperature":0,"max_output_tokens":8192}}}""");
        Assert.Null(ModelParameters.ForModel(document.RootElement, "other-model"));
        var options = new ChatOptions { MaxOutputTokens = 4096 };
        new ModelParameters().ApplyTo(options, "m");
        Assert.Equal(4096, options.MaxOutputTokens);
        ModelParameters.ForModel(document.RootElement, "m")!.ApplyTo(options, "m");
        Assert.Equal(0, options.Temperature);
        Assert.Equal(8192, options.MaxOutputTokens);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealSdkSendsSettings_InBothProtocolsAndBothInvocationPaths(bool responses, bool streaming)
    {
        using var capture = new CaptureHandler(responses);
        using var http = new HttpClient(capture);
        var sdk = new OpenAIClient(new ApiKeyCredential("test-only"), new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
#pragma warning disable OPENAI001
        IChatClient inner = responses ? sdk.GetResponsesClient().AsIChatClient("test-model") : sdk.GetChatClient("test-model").AsIChatClient();
#pragma warning restore OPENAI001
        using var client = AgentChatClientFactory.ConfigureParameters(inner, new ChatResolution
        {
            Model = "test-model", Parameters = new ModelParameters { ReasoningEffort = "high", Temperature = 0.4f, TopP = 0.8f, MaxOutputTokens = 8192 }
        });
        var original = new ChatOptions { MaxOutputTokens = 4096, Instructions = "Keep the task instructions." };
        if (streaming)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], original)) { } });
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], original));
        var body = capture.Body;
        Assert.Equal("high", responses ? body.GetProperty("reasoning").GetProperty("effort").GetString() : body.GetProperty("reasoning_effort").GetString());
        Assert.Equal(8192, body.GetProperty(responses ? "max_output_tokens" : "max_completion_tokens").GetInt32());
        Assert.Equal(0.4, body.GetProperty("temperature").GetDouble(), precision: 5);
        Assert.Equal(0.8, body.GetProperty("top_p").GetDouble(), precision: 5);
        Assert.Contains("Keep the task instructions.", body.ToString());
        Assert.Equal(4096, original.MaxOutputTokens);
        Assert.Null(original.Reasoning);
    }

    [Fact]
    public void ReasoningOnKnownOpenAiModelsOmitsUnsupportedSampling()
    {
        var options = new ChatOptions { Temperature = 0.6f, TopP = 0.9f };
        new ModelParameters { ReasoningEffort = "high" }.ApplyTo(options, "gpt-5.4");
        Assert.Null(options.Temperature);
        Assert.Null(options.TopP);
        new ModelParameters { ReasoningEffort = "none", Temperature = 0 }.ApplyTo(options, "gpt-5.4");
        Assert.Equal(0, options.Temperature);
    }

    [Theory]
    [InlineData(null, 8192)]
    [InlineData(4096, 8192)]
    [InlineData(1, 1)]
    [InlineData(1024, 1024)]
    [InlineData(16384, 8192)]
    public void AModelDefaultCannotInflateExplicitShortCallBudgets(int? original, int expected)
    {
        var options = new ChatOptions { MaxOutputTokens = original };
        new ModelParameters { MaxOutputTokens = 8192 }.ApplyTo(options, "m");
        Assert.Equal(expected, options.MaxOutputTokens);
    }

    private sealed class CaptureHandler(bool responses) : HttpMessageHandler
    {
        public JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith(responses ? "/responses" : "/chat/completions", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Body = document.RootElement.Clone();
            // The test ends at the real encoded request; no external model is contacted.
            throw new InvalidOperationException("Captured model request.");
        }
    }
}
