using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

public sealed class Maf18RuntimeAdapterTests
{
    [Fact]
    public void UsageSince_MergesParallelDeltasWithoutCountingTheBaselineTwice()
    {
        var baseline = new ModelUsage(10, 2, null, AdditionalCounts: new Dictionary<string, long> { ["requests"] = 1 });
        var worker = new ModelUsage(3, 1, null, AdditionalCounts: new Dictionary<string, long> { ["requests"] = 2 });
        var local = Maf18RuntimeAdapter.AddUsage(baseline, worker);
        var delta = Maf18RuntimeAdapter.UsageSince(local, baseline);
        var combined = Maf18RuntimeAdapter.AddUsage(Maf18RuntimeAdapter.AddUsage(baseline, delta), delta);
        Assert.Equal(16, combined!.InputTokens);
        Assert.Equal(4, combined.OutputTokens);
        Assert.Null(combined.TotalTokens);
        Assert.Equal(5, combined.AdditionalCounts!["requests"]);
    }

    [Fact]
    public void FrameworkPackageFamily_IsLockedTo118()
    {
        Maf18RuntimeAdapter.EnsureCompatible();

        Assert.Equal(
            [
                "Microsoft.Agents.AI",
                "Microsoft.Agents.AI.Abstractions",
                "Microsoft.Agents.AI.OpenAI"
            ],
            Maf18RuntimeAdapter.FrameworkVersions.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(3, Maf18RuntimeAdapter.FrameworkVersions.Count);
        Assert.All(Maf18RuntimeAdapter.FrameworkVersions.Values, version =>
        {
            Assert.Equal(1, version.Major);
            Assert.Equal(18, version.Minor);
        });
    }

    [Fact]
    public void GovernanceAgent_HasStableIdentityAndCoreOwnedToolBoundary()
    {
        var wiring = Maf18RuntimeAdapter.InspectGovernanceWiring(
            new RecordingChatClient(),
            "operation.meeting",
            "meeting",
            "User-facing governance agent.",
            new ChatOptions { Instructions = "Reply from governed evidence." });

        Assert.Equal("operation.meeting", wiring.Id);
        Assert.Equal("meeting", wiring.Name);
        Assert.False(wiring.SensitiveDataEnabled);
        Assert.False(wiring.ConcurrentInvocationAllowed);
        Assert.False(wiring.ApprovalResponseBindingDisabled);
        Assert.True(wiring.FunctionInvokingClientAttached);
        Assert.False(wiring.InvokerConcurrentInvocationAllowed);
        // The provider's own default once truncated the planner's JSON mid-object, so the
        // ceiling is applied inside the adapter where no caller can forget it.
        Assert.Equal(Maf18RuntimeAdapter.DefaultMaxOutputTokens, wiring.MaxOutputTokens);

        var withTool = new ChatOptions
        {
            Tools = [AIFunctionFactory.CreateDeclaration(
                "write_file",
                "Writes a file",
                JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}"),
                null)]
        };
        // A DECLARATION is allowed now: the operation layer may hold a tool surface of its
        // own (a mode can arm its conversation identity to edit the workspace directly).
        // MAF cannot invoke a declaration, so the model's call still comes back to the
        // engine and Core still owns dispatch.
        var declared = Maf18RuntimeAdapter.InspectGovernanceWiring(
            new RecordingChatClient(), "operation.supervisor", "supervisor", "Reviews evidence.", withTool);
        Assert.Equal("operation.supervisor", declared.Id);
    }

    /// <summary>
    /// The line the adapter draws is INVOKABILITY, not layer membership. A tool MAF can
    /// call itself must still be refused, because it would execute the side effect
    /// directly and bypass Core's authorization, approval, audit and checkpoint path —
    /// which is the whole reason the guard exists.
    /// </summary>
    [Fact]
    public async Task GovernanceAgent_RejectsInvokableTools_KeepsCoreOwnedDispatch()
    {
        var invokable = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(
                new Func<string, string>(_ => "wrote"),
                "write_file",
                "Writes a file")]
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            new RecordingChatClient(),
            "operation.supervisor",
            "supervisor",
            "Reviews evidence.",
            invokable,
            "Review the evidence.",
            CancellationToken.None));

        Assert.Contains("declarative tools", error.Message, StringComparison.Ordinal);
        Assert.Contains("write_file", error.Message, StringComparison.Ordinal);
        Assert.Contains("owns authorization and dispatch", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the nine governance call sites get back: usage already normalized, the raw text
    /// the JSON readers still need, and an answer that never carries thinking markup.
    /// </summary>
    [Fact]
    public async Task RunGovernanceTurn_HandsBackTinadecOwnedTextAndUsage()
    {
        var client = new ScriptedGovernanceClient(
            "<think>weighing the evidence</think>\nThe verdict is proceed.",
            new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5, TotalTokenCount = 17 });

        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            client,
            "operation.meeting",
            "meeting",
            "User-facing governance agent.",
            new ChatOptions { Instructions = "Reply from governed evidence." },
            "Current user goal:\nfinish the run",
            CancellationToken.None);

        Assert.Contains("<think>", turn.RawText, StringComparison.Ordinal);
        Assert.Equal("The verdict is proceed.", turn.AnswerText);
        Assert.True(turn.HasAnswer);
        Assert.Equal(12, turn.Usage!.InputTokens);
        Assert.Equal(5, turn.Usage.OutputTokens);
        Assert.Equal(17, turn.Usage.TotalTokens);
        Assert.DoesNotContain("Microsoft.Agents", typeof(Maf18RuntimeAdapter.GovernanceTurn).AssemblyQualifiedName);
        // The ceiling reaches the wire, not just the caller's options object.
        Assert.Equal(Maf18RuntimeAdapter.DefaultMaxOutputTokens, client.LastOptions?.MaxOutputTokens);
    }

    /// <summary>
    /// "The model answered nothing" is decided once, in the turn: reasoning-only output is
    /// an empty answer, and each caller keeps its own failure policy on top of that fact.
    /// </summary>
    [Fact]
    public async Task RunGovernanceTurn_ReasoningOnlyOutputIsAnEmptyAnswer()
    {
        var client = new ScriptedGovernanceClient("<thinking>weighing, never answering</thinking>", usage: null);

        var turn = await Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            client,
            "operation.meeting",
            "meeting",
            "User-facing governance agent.",
            new ChatOptions(),
            "goal",
            CancellationToken.None);

        Assert.False(turn.HasAnswer);
        Assert.Equal(string.Empty, turn.AnswerText);
        Assert.Null(turn.Usage);
        Assert.Equal("No git steward suggestion produced.", turn.AnswerOr("No git steward suggestion produced."));
    }

    /// <summary>
    /// The adapter must never re-type a failure. <c>RunInterruptedException</c> derives from
    /// <see cref="OperationCanceledException"/> and drives the interrupt path, while the
    /// engine's error classifier only surfaces a controlled message for the original
    /// <see cref="InvalidOperationException"/> family — wrapping either here would break both.
    /// </summary>
    [Fact]
    public async Task RunGovernanceTurn_PropagatesCancellationAndProviderFailuresUnchanged()
    {
        var cancelling = new ScriptedGovernanceClient(
            "unused", null, new OperationCanceledException(new CancellationTokenSource().Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            cancelling, "operation.meeting", "meeting", "User-facing governance agent.",
            new ChatOptions(), "goal", CancellationToken.None));

        var failing = new ScriptedGovernanceClient(
            "unused", null, new InvalidOperationException("The model provider reached its rate limit."));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Maf18RuntimeAdapter.RunGovernanceTurnAsync(
            failing, "operation.meeting", "meeting", "User-facing governance agent.",
            new ChatOptions(), "goal", CancellationToken.None));
        Assert.Equal("The model provider reached its rate limit.", error.Message);
    }

    [Fact]
    public void Usage_IsNormalizedAndAggregatedWithoutMafTypes()
    {
        var first = Maf18RuntimeAdapter.NormalizeUsage(new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 4,
            TotalTokenCount = 14,
            CachedInputTokenCount = 3,
            AdditionalCounts = new() { ["accepted_prediction_tokens"] = 2 }
        });
        var second = Maf18RuntimeAdapter.NormalizeUsage(new UsageDetails
        {
            InputTokenCount = 7,
            OutputTokenCount = 5,
            TotalTokenCount = 12,
            ReasoningTokenCount = 2,
            AdditionalCounts = new() { ["accepted_prediction_tokens"] = 1 }
        });

        var total = Maf18RuntimeAdapter.AddUsage(first, second);

        Assert.NotNull(total);
        Assert.Equal(17, total.InputTokens);
        Assert.Equal(9, total.OutputTokens);
        Assert.Equal(26, total.TotalTokens);
        Assert.Equal(3, total.CachedInputTokens);
        Assert.Equal(2, total.ReasoningTokens);
        Assert.Equal(3, total.AdditionalCounts?["accepted_prediction_tokens"]);
        Assert.DoesNotContain("Microsoft.Agents", typeof(ModelUsage).AssemblyQualifiedName);
    }

    [Fact]
    public async Task WorkerCompaction_KeepsToolCallsAndResultsAtomic()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 5; i++)
        {
            var callId = $"call-{i}";
            history.Add(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(callId, "search", new Dictionary<string, object?> { ["query"] = i })
            ]));
            history.Add(new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent(callId, new { value = i })
            ]));
        }

        var compacted = await Maf18RuntimeAdapter.CompactWorkerConversationAsync(
            new ChatMessage(ChatRole.User, "goal"), history, maxHistoryMessages: 5, CancellationToken.None);

        Assert.Equal("goal", compacted[0].Text);
        Assert.True(compacted.Count <= 5);
        var calls = compacted.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(content => content.CallId).ToHashSet(StringComparer.Ordinal);
        var results = compacted.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(content => content.CallId).ToHashSet(StringComparer.Ordinal);
        Assert.True(calls.SetEquals(results), "Compaction must not split a tool call from its result.");
    }

    [Fact]
    public void ToolRounds_RemainACoreOwnedLimit()
    {
        ToolRuntimePolicy.Validate(new ToolRuntimePolicy("provider", true, true, 120, ToolRuntimePolicy.MaximumRounds));

        var error = Assert.Throws<InvalidDataException>(() => ToolRuntimePolicy.Validate(
            new ToolRuntimePolicy("provider", true, true, 120, ToolRuntimePolicy.MaximumRounds + 1)));

        Assert.Contains("Core safety ceiling", error.Message, StringComparison.Ordinal);
        Assert.NotEqual(ToolApprovalAgent.DefaultMaxAutoApprovalIterations, ToolRuntimePolicy.MaximumRounds);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
    }

    /// <summary>Answers one governance turn with a scripted body, usage, or failure.</summary>
    private sealed class ScriptedGovernanceClient(string text, UsageDetails? usage, Exception? failure = null) : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            if (failure is not null) throw failure;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (failure is not null) throw failure;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }
    }
}
