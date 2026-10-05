using System.Text.Json;
using TinadecCore.DmaEA.Orchestration;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// WS-9 pinning: the lane model moved to <c>TinadecCore.DmaEA.Orchestration</c>,
/// but its JSON shapes are the persisted checkpoint contract. These tests lock the
/// wire keys and the lane status values so a later rename cannot silently break
/// replay of existing checkpoints.
/// </summary>
public sealed class LaneModelContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LaneWait_RoundTripsTheStructuredShape()
    {
        var wait = new LaneWait("main", LaneWait.LaneTasksCompleted, ["c1", "c2"], "hash-1");
        var json = JsonSerializer.Serialize(wait, Web);
        Assert.Contains("\"lane_key\":\"main\"", json, StringComparison.Ordinal);
        Assert.Contains("\"predicate\":\"lane_tasks_completed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"required_criteria\"", json, StringComparison.Ordinal);
        Assert.Contains("\"observed_facts_hash\":\"hash-1\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<LaneWait>(json, Web);
        Assert.NotNull(back);
        Assert.Equal(wait.LaneKey, back!.LaneKey);
        Assert.Equal(wait.Predicate, back.Predicate);
        Assert.Equal(wait.RequiredCriteria, back.RequiredCriteria);
        Assert.Equal(wait.ObservedFactsHash, back.ObservedFactsHash);
    }

    [Fact]
    public void LaneWait_ReadsTheM2PlainStringAsLaneDone()
    {
        var back = JsonSerializer.Deserialize<LaneWait>("\"main\"", Web);
        Assert.NotNull(back);
        Assert.Equal("main", back!.LaneKey);
        Assert.Equal(LaneWait.LaneDone, back.Predicate);
        Assert.Empty(back.RequiredCriteria);
        Assert.Null(back.ObservedFactsHash);
    }

    [Fact]
    public void LaneStatus_ValuesAreThePersistedContract()
    {
        Assert.Equal("pending", LaneStatus.Pending);
        Assert.Equal("planning", LaneStatus.Planning);
        Assert.Equal("executing", LaneStatus.Executing);
        Assert.Equal("waiting", LaneStatus.Waiting);
        Assert.Equal("gate_review", LaneStatus.GateReview);
        Assert.Equal("reviewing", LaneStatus.Reviewing);
        Assert.Equal("finalizing", LaneStatus.Finalizing);
        Assert.Equal("done", LaneStatus.Done);
        Assert.Equal("failed", LaneStatus.Failed);
        Assert.Equal("completed", LaneStatus.Completed);

        // The durable lane starts in the pending phase.
        Assert.Equal(LaneStatus.Pending, new DurableLane().Status);
    }

    [Fact]
    public void DurableLane_SerializesTheCheckpointKeys()
    {
        var json = JsonSerializer.Serialize(new DurableLane { LaneKey = "l1", Status = LaneStatus.Waiting }, Web);
        Assert.Contains("\"laneKey\":\"l1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"waiting\"", json, StringComparison.Ordinal);
    }
}
