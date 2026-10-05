using System.Text.Json;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// ACP turn accounting. These are the behaviours the protocol reference documents as measured rather
/// than specified — the drain window, which events may extend a turn's life, and what must never be
/// concatenated into an answer — because each one fails in a way that looks like a model problem
/// instead of a transport problem.
/// </summary>
public sealed class AcpSessionTests
{
    private static (AcpSession Session, FakeAcpGenerationPool Pool, FakeAcpAgent Agent, RecordingAcpTurnObserver Observer) Open(
        Action<FakeAcpAgent>? configure = null,
        bool open = true)
    {
        var pool = new FakeAcpGenerationPool();
        if (configure is not null) pool.Configure = configure;
        var request = AcpSessionTestSupport.Request(AcpSessionTestSupport.ScratchRoot("session"));
        var session = AcpSessionTestSupport.Session(request, pool);
        if (open) session.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (session, pool, pool.Latest, new RecordingAcpTurnObserver());
    }

    [Fact]
    public async Task Turn_CollectsMessageChunks_IntoTheAnswer_AndReportsTheRestSeparately()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Hel" } });
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "thinking aloud" } });
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new[] { new { type = "text", text = "lo" } } });
            await self.SendSessionUpdateAsync(new { sessionUpdate = "tool_call", toolCallId = "t1", title = "read", kind = "read", status = "running" });
            return new { stopReason = "end_turn" };
        };

        var result = await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Equal("Hello", result.Answer);
        Assert.Equal("Hello", observer.Answer);
        Assert.Equal(["thinking aloud"], observer.Thoughts);
        Assert.Single(observer.ToolCalls);
        Assert.Equal("t1", observer.ToolCalls[0].ToolCallId);
        Assert.Equal(AcpTurnCompletion.Completed, result.Completion);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task AnswerChunk_ArrivingAfterThePromptReceipt_IsStillDelivered()
    {
        var (session, pool, agent, observer) = Open();
        // Measured behaviour: notifications ride a separate queue, so at least one real harness
        // returns the prompt receipt before its final session/update line.
        agent.QueueSessionUpdateAfter(TimeSpan.FromMilliseconds(150),
            new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = " late chunk" } });

        var result = await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Equal(" late chunk", result.Answer);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task SubagentChunk_NeverEntersTheParentAnswer_ButIsStillReported()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "parent says " } });
            await self.SendSessionUpdateAsync(new
            {
                sessionUpdate = "agent_message_chunk",
                content = new { type = "text", text = "child transcript " },
                _meta = new Dictionary<string, object> { ["codebuddy.ai/parentToolCallId"] = "t1" }
            });
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "again parent" } });
            return new { stopReason = "end_turn" };
        };

        var result = await session.PromptAsync("hi", observer, CancellationToken.None);

        // Both real harnesses that fan out subagents would otherwise interleave the child's
        // transcript into the answer the user reads.
        Assert.Equal("parent says again parent", result.Answer);
        Assert.Single(observer.SubagentChunks);
        Assert.Equal(("t1", "agent_message_chunk"), observer.SubagentChunks[0]);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task UsageUpdate_ReachesTheTurnResult()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new { sessionUpdate = "usage_update", used = 120, size = 8192 });
            return new { stopReason = "end_turn" };
        };

        var result = await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Equal(120, result.Usage.Used);
        Assert.Equal(8192, result.Usage.ContextWindow);
        Assert.Null(result.Usage.CostUsd);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task UpdateForAnotherSession_IsIgnored()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendNotificationAsync("session/update", new
            {
                sessionId = "someone-elses-session",
                update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "leak" } }
            });
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "mine" } });
            return new { stopReason = "end_turn" };
        };

        var result = await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Equal("mine", result.Answer);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Theory]
    [InlineData("end_turn", "Completed")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("max_tokens", "Truncated")]
    [InlineData("max_turn_requests", "Truncated")]
    public async Task StopReasons_MapToCompletion(string stopReason, string expectedCompletion)
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = (_, _) => Task.FromResult<object?>(new { stopReason });

        var result = await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Equal(expectedCompletion, result.Completion.ToString());
        Assert.Equal(stopReason, result.StopReason);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task Refusal_IsReportedAsARefusal_AndNotAsAnEmptyAnswer()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = (_, _) => Task.FromResult<object?>(new { stopReason = "refusal" });

        await Assert.ThrowsAsync<AcpTurnRefusedException>(() => session.PromptAsync("hi", observer, CancellationToken.None));

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task NoProgressWithinIdleWindow_FaultsTheTurn_AndTheNextSendReconnects()
    {
        var (session, pool, agent, observer) = Open(configure: a => a.SuspendPrompts = true);

        var error = await Assert.ThrowsAsync<AcpTurnIdleException>(() => session.PromptAsync("hi", observer, CancellationToken.None));
        Assert.Contains("no user-visible progress", error.Message, StringComparison.Ordinal);
        Assert.True(session.IsFaulted);

        // The same session object survives: the next send reconnects instead of rethrowing forever.
        pool.Configure = a => a.SuspendPrompts = false;
        var recovered = await session.PromptAsync("again", new RecordingAcpTurnObserver(), CancellationToken.None);

        Assert.Equal(AcpTurnCompletion.Completed, recovered.Completion);
        Assert.Equal(2, pool.Count);
        Assert.Equal(2, session.Generation);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task HeartbeatsAndUsage_DoNotResetTheIdleTimer()
    {
        // A wedged model API still emits keepalives. Counting them as progress would let a hung agent
        // live forever on its own heartbeat traffic and the run would never be reaped, so the
        // keepalives here have to outlast several idle budgets without extending the turn.
        var (session, pool, agent, observer) = Open(configure: a =>
        {
            a.SuspendPrompts = true;
            for (var index = 1; index <= 10; index++)
            {
                a.QueueSessionUpdateAfter(TimeSpan.FromMilliseconds(index * 400),
                    new { sessionUpdate = "usage_update", used = index, size = 100 });
            }
        });

        var error = await Assert.ThrowsAsync<AcpTurnIdleException>(() => session.PromptAsync("hi", observer, CancellationToken.None));

        // Faulted well before the keepalives finished: none of them bought the turn any time.
        Assert.Contains("1.2 seconds", error.Message, StringComparison.Ordinal);
        Assert.InRange(observer.Usages.Count, 1, 3);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task ProgressChunks_DoResetTheIdleTimer_AndTheTurnCompletesNormally()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            // Long enough to pass the idle budget three times over; only the frames keep it alive.
            for (var index = 0; index < 25; index++)
            {
                await Task.Delay(60);
                await self.SendSessionUpdateAsync(new { sessionUpdate = "tool_call_update", toolCallId = "t1", status = "running" });
            }

            return new { stopReason = "end_turn" };
        };

        // 12 x 60ms is three times the 400ms idle bound; progress frames must be what keeps it alive.
        var result = await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Equal(AcpTurnCompletion.Completed, result.Completion);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task PermissionRequest_IsRefused_AndTheTurnKeepsItsAnswer()
    {
        var (session, pool, agent, observer) = Open();
        JsonElement? answered = null;
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "partial work" } });
            answered = await self.SendRequestAsync("session/request_permission", new
            {
                sessionId = self.SessionId,
                toolCall = new { toolCallId = "t9", title = "apply patch", kind = "edit" },
                options = new[]
                {
                    new { optionId = "allow-once", name = "Allow once", kind = "allow_once" },
                    new { optionId = "reject-once", name = "Reject once", kind = "reject_once" },
                    new { optionId = "reject-always", name = "Reject always", kind = "reject_always" }
                }
            });
            return new { stopReason = "end_turn" };
        };

        var result = await session.PromptAsync("do the thing", observer, CancellationToken.None);

        Assert.Equal("partial work", result.Answer);
        Assert.Equal("reject-once", answered?.GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Single(observer.Refusals);
        Assert.Equal("t9", observer.Refusals[0].ToolCallId);
        Assert.Equal(RefusingAcpInteractionRouter.NoApprovalBridge, observer.Refusals[0].Reason);
        // The peer's own handler errors are recorded, not swallowed: a swallowed one would let a
        // stall read like a pass.
        Assert.Empty(agent.HandlerFailures);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task Cancel_SendsANotification_AndReturnsThePartialAnswer()
    {
        var (session, pool, agent, observer) = Open();
        var release = new TaskCompletionSource();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "some answer" } });
            await release.Task;
            return new { stopReason = "cancelled" };
        };

        var turn = session.PromptAsync("hi", observer, CancellationToken.None);
        await AcpSessionTestSupport.WaitAsync(() => agent.Prompts == 1);
        await session.CancelTurnAsync(CancellationToken.None);
        // The peer's read loop is its own task: wait for the frame to land before releasing the turn,
        // or the assertion below races the thing it is checking.
        await AcpSessionTestSupport.WaitAsync(() => agent.Cancels == 1);
        release.SetResult();

        var result = await turn;

        Assert.Equal("some answer", result.Answer);
        Assert.Equal(AcpTurnCompletion.Cancelled, result.Completion);
        Assert.Equal(1, agent.Cancels);
        // The cancel frame must carry no id: Core answers its own cancellation otherwise.
        Assert.DoesNotContain("\"id\"", agent.ReceivedFrames.Single(frame => frame.Contains("session/cancel", StringComparison.Ordinal)), StringComparison.Ordinal);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task CancelIgnoredByTheHarness_KillsTheTree_AfterTheGraceWindow()
    {
        var (session, pool, agent, observer) = Open();
        var release = new TaskCompletionSource();
        agent.OnPrompt = (_, _) => release.Task.ContinueWith(_ => (object?)new { stopReason = "cancelled" });

        var turn = session.PromptAsync("hi", observer, CancellationToken.None);
        await AcpSessionTestSupport.WaitAsync(() => agent.Prompts == 1);
        await session.CancelTurnAsync(CancellationToken.None);

        // The harness never settles, so the grace period has to terminate it; the text already
        // produced is still the honest result rather than a failure the user caused on purpose.
        var result = await turn;

        Assert.Equal(AcpTurnCompletion.Cancelled, result.Completion);
        Assert.True(pool.TransportAt(0).WasKilled);
        Assert.NotNull(session.Fault);

        release.TrySetResult();
        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task SessionNew_ReceivesTheGovernedScratchDirectory_AndNeverTheCallersTree()
    {
        var scratch = AcpSessionTestSupport.ScratchRoot("cwd-guard");
        var pool = new FakeAcpGenerationPool();
        var request = AcpSessionTestSupport.Request(scratch);
        var session = AcpSessionTestSupport.Session(request, pool);

        await session.OpenAsync(CancellationToken.None);

        var setupFrame = pool.Latest.ReceivedFrames.Single(frame => frame.Contains("session/new", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(setupFrame);
        var cwd = document.RootElement.GetProperty("params").GetProperty("cwd").GetString();

        // The scratch root is the boundary. A harness writes inside its cwd with its own tools no
        // matter what capabilities were declared, so this value is what limits its blast radius.
        Assert.Equal(Path.GetFullPath(scratch), Path.GetFullPath(cwd!));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tinadec-acp-session-tests")), Path.GetFullPath(cwd!), StringComparison.OrdinalIgnoreCase);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task BetweenTurnDeath_ReconnectsAndReplaysTheConfirmedModel()
    {
        var (session, pool, agent, _) = Open();
        await session.SetModelAsync("model-beta", CancellationToken.None);
        Assert.Equal(["model-beta"], pool.Latest.SetModelCalls);

        // Crash-on-exit and idle reaping both kill the process between turns. Previously this
        // poisoned the thread until the host restarted.
        pool.TransportAt(0).SimulatePeerExit();

        var observer = new RecordingAcpTurnObserver();
        await session.PromptAsync("after death", observer, CancellationToken.None);

        Assert.Equal(2, pool.Count);
        Assert.Equal(2, session.Generation);
        Assert.Equal(["model-beta"], pool.Latest.SetModelCalls);
        Assert.Contains("session/load", string.Join('\n', pool.Latest.ReceivedFrames), StringComparison.Ordinal);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task RestoreWithoutAdvertisedLoadSession_IsRefused()
    {
        var pool = new FakeAcpGenerationPool();
        pool.Configure = agent => agent.LoadSession = false;
        var request = AcpSessionTestSupport.Request(AcpSessionTestSupport.ScratchRoot("no-load"), restoreSessionId: "native-42");
        var session = AcpSessionTestSupport.Session(request, pool);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync(CancellationToken.None));

        Assert.Contains("session/load", error.Message, StringComparison.Ordinal);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task ReconnectRetiresTheDeadTransport_InsteadOfTalkingToItAgain()
    {
        var (session, pool, agent, observer) = Open(configure: a => a.SuspendPrompts = true);
        await Assert.ThrowsAsync<AcpTurnIdleException>(() => session.PromptAsync("hi", observer, CancellationToken.None));

        // Generation 1 is gone: the idle fault terminates it, and the replacement is a new transport.
        Assert.True(pool.TransportAt(0).WasKilled);

        pool.Configure = a => a.SuspendPrompts = false;
        await session.PromptAsync("next", new RecordingAcpTurnObserver(), CancellationToken.None);

        Assert.Equal(2, pool.Count);
        Assert.Equal(2, session.Generation);
        Assert.False(pool.TransportAt(1).WasKilled);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task CompletedToolCall_IsNotReopened_ByALateUpdate_AndItsDiffIsHarvestedOnce()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "t1",
                title = "write file",
                kind = "edit",
                status = "completed",
                content = new[] { new { type = "diff", path = "src/a.cs", oldText = "one", newText = "two" } }
            });
            await self.SendSessionUpdateAsync(new { sessionUpdate = "tool_call_update", toolCallId = "t1", status = "running", title = "write file" });
            return new { stopReason = "end_turn" };
        };

        await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Single(observer.ToolCalls);
        Assert.Single(observer.FileChanges);
        Assert.Equal("src/a.cs", observer.FileChanges[0].Path);
        Assert.False(observer.FileChanges[0].Added);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task ToolCallFields_AreMergedFromEarlierFrames_RatherThanReplaced()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new { sessionUpdate = "tool_call", toolCallId = "t1", title = "search", kind = "search", status = "pending" });
            await self.SendSessionUpdateAsync(new { sessionUpdate = "tool_call_update", toolCallId = "t1", status = "completed" });
            return new { stopReason = "end_turn" };
        };

        await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Equal(2, observer.ToolCalls.Count);
        var last = observer.ToolCalls[^1];
        Assert.Equal("search", last.Title);
        Assert.Equal("search", last.Kind);
        Assert.Equal("completed", last.Status);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Theory]
    [InlineData("oversized", "x", "y")]
    [InlineData("unchanged", "same", "same")]
    public async Task UnusableDiffBodies_AreNotCarried(string caseName, string oldText, string newText)
    {
        var (session, pool, agent, observer) = Open();
        var before = caseName == "oversized" ? new string('a', AcpDiffLimitsForTest.MaxBody + 1) : oldText;
        var after = caseName == "oversized" ? new string('b', AcpDiffLimitsForTest.MaxBody + 1) : newText;
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "t1",
                status = "completed",
                content = new[] { new { type = "diff", path = "big.cs", oldText = before, newText = after } }
            });
            return new { stopReason = "end_turn" };
        };

        await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Empty(observer.FileChanges);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task CorruptCursorDiffHeader_DoesNotBecomeAFabricatedFileBody()
    {
        var (session, pool, agent, observer) = Open();
        agent.OnPrompt = async (self, _) =>
        {
            await self.SendSessionUpdateAsync(new
            {
                sessionUpdate = "tool_call",
                toolCallId = "t1",
                status = "completed",
                content = new[] { new { type = "diff", path = "a.cs", oldText = "-- /dev/null", newText = "++ b/a.cs\nreal" } }
            });
            return new { stopReason = "end_turn" };
        };

        await session.PromptAsync("hi", observer, CancellationToken.None);

        Assert.Empty(observer.FileChanges);

        await session.DisposeAsync();
        pool.KillAll();
    }

    [Fact]
    public async Task ConfigOptionUpdate_RefreshesTheAdvertisedSurface_EvenWhileIdle()
    {
        var (session, pool, agent, observer) = Open();

        await agent.SendSessionUpdateAsync(new { sessionUpdate = "config_option_update", configOptions = new[] { new { id = "model", currentValue = "m1" } } });
        await AcpSessionTestSupport.WaitAsync(() => session.Configuration is not null);

        Assert.NotNull(session.Configuration);

        await session.DisposeAsync();
        pool.KillAll();
    }

    /// <summary>Mirrors the session's private diff-body ceiling so the oversized case stays honest.</summary>
    private static class AcpDiffLimitsForTest
    {
        public const int MaxBody = 100_000;
    }
}
