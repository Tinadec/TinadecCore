using System.Text.Json;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// Round 1's permission posture, tested directly. The point of these assertions is that a refusal is
/// a recorded decision, not an absence: the agent must get an answer, the turn must survive, and the
/// reason has to be readable later.
/// </summary>
public sealed class AcpInteractionRouterTests
{
    private static JsonElement PermissionParams(params (string OptionId, string Kind)[] options)
    {
        var payload = new
        {
            sessionId = "s1",
            toolCall = new { toolCallId = "t1", title = "run shell", kind = "execute" },
            options = options.Select(option => new { optionId = option.OptionId, name = option.OptionId, kind = option.Kind }).ToArray()
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    [Fact]
    public void RejectOnce_IsChosenBeforeRejectAlways()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var answer = router.RouteAsync("session/request_permission",
            PermissionParams(("allow-once", "allow_once"), ("reject-always", "reject_always"), ("reject-once", "reject_once")),
            observer, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("selected", answer.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("reject-once", answer.GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public void RejectAlways_IsUsedWhenNoSingleUseRejectExists()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var answer = router.RouteAsync("session/request_permission",
            PermissionParams(("allow-always", "allow_always"), ("reject-always", "reject_always")),
            observer, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("reject-always", answer.GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public void WhenTheAgentOfferedNoWayToRefuse_TheAnswerIsCancelledNotGuessed()
    {
        // Answering "selected" with an allow option would be the harness approving its own write.
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var answer = router.RouteAsync("session/request_permission",
            PermissionParams(("allow-once", "allow_once")),
            observer, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("cancelled", answer.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.False(answer.GetProperty("outcome").TryGetProperty("optionId", out var missing) && missing.ValueKind != JsonValueKind.Null);
        Assert.Single(observer.Refusals);
        Assert.Null(observer.Refusals[0].OptionId);
    }

    [Fact]
    public void RefusalIsRecordedWithTheToolAndTheReason_RatherThanBeingSilent()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        router.RouteAsync("session/request_permission", PermissionParams(("reject-once", "reject_once")), observer, CancellationToken.None)
            .GetAwaiter().GetResult();

        var refusal = Assert.Single(observer.Refusals);
        Assert.Equal("t1", refusal.ToolCallId);
        Assert.Equal("run shell", refusal.Title);
        Assert.Equal(RefusingAcpInteractionRouter.NoApprovalBridge, refusal.Reason);
    }

    [Fact]
    public async Task UnknownVendorMethod_IsDeclinedNotGuessed_AndItsNameIsRemembered()
    {
        var router = new RefusingAcpInteractionRouter("s1");
        var observer = new RecordingAcpTurnObserver();

        var error = await Assert.ThrowsAsync<AcpMethodNotFoundException>(() =>
            router.RouteAsync("_codebuddy.ai/question", JsonSerializer.SerializeToElement(new { schema = new { } }), observer, CancellationToken.None));

        Assert.Equal("_codebuddy.ai/question", error.Method);
        // Batch 2 needs the list of what real harnesses actually ask, which cannot be written from a
        // specification. Declining must therefore also record.
        Assert.Contains("_codebuddy.ai/question", RefusingAcpInteractionRouter.ObservedUnsupportedMethods, StringComparer.Ordinal);
    }

    [Fact]
    public void RoundOneRouterHoldsNothingOpen_SoTheDrainWindowCanClose()
    {
        var router = new RefusingAcpInteractionRouter("s1");

        Assert.Equal(0, router.PendingCount);
        router.CloseAll();
        Assert.Equal(0, router.PendingCount);
    }

    [Fact]
    public void FactoryGivesEachSessionItsOwnRouter()
    {
        var factory = new RefusingAcpInteractionRouterFactory();

        var first = factory.Create("session-a");
        var second = factory.Create("session-b");

        Assert.NotSame(first, second);
    }
}
