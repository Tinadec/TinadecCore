using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.DmaEA.Orchestration;

// Lane model + checkpoint serialization contract. Extracted from
// FullDuplexRunEngine (WS-9 organization split): the shapes below are persisted
// in run checkpoints and projected to clients, so their field names, JSON keys
// and values must not change.

/// <summary>
/// Per-criterion supervised verdict for a lane-gated task. ReviewedBy and
/// Round attribute the verdict to a supervision pass; a gate may only
/// proceed when every recorded verdict is satisfied with evidence.
/// </summary>
internal sealed record CriterionVerdict(string Criterion, bool Satisfied, string? Evidence, string? ReviewedBy = null, int Round = 0);

/// <summary>
/// A structured cross-lane wait: the owning task is docked behind
/// <paramref name="LaneKey"/> until the predicate holds over code facts.
/// ObservedFactsHash freezes the target lane's observable state at park time
/// so a gate cannot proceed against facts that moved underneath it.
/// </summary>
[JsonConverter(typeof(LaneWaitJsonConverter))]
internal sealed record LaneWait(string LaneKey, string Predicate, IReadOnlyList<string> RequiredCriteria, string? ObservedFactsHash)
{
    public const string LaneDone = "lane_done";
    public const string LaneTasksCompleted = "lane_tasks_completed";
    public const string LaneSupervisionPass = "lane_supervision_pass";

    public static LaneWait UntilLaneDone(string laneKey) => new(laneKey, LaneDone, [], null);
}

/// <summary>
/// Reads M2-era waits serialized as plain lane-key strings ("main") as
/// lane_done waits; writes the full structured shape.
/// </summary>
internal sealed class LaneWaitJsonConverter : JsonConverter<LaneWait>
{
    public override LaneWait? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var key = reader.GetString();
            return string.IsNullOrWhiteSpace(key) ? null : new LaneWait(key.Trim(), LaneWait.LaneDone, [], null);
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("LaneWait must be a string or an object.");
        string? laneKey = null, predicate = null, hash = null;
        var criteria = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "lane_key" when reader.TokenType == JsonTokenType.String: laneKey = reader.GetString(); break;
                case "predicate" when reader.TokenType == JsonTokenType.String: predicate = reader.GetString(); break;
                case "observed_facts_hash" when reader.TokenType == JsonTokenType.String: hash = reader.GetString(); break;
                case "required_criteria" when reader.TokenType == JsonTokenType.StartArray:
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String) criteria.Add(reader.GetString() ?? string.Empty);
                    }
                    break;
                default: reader.Skip(); break;
            }
        }
        if (string.IsNullOrWhiteSpace(laneKey)) throw new JsonException("LaneWait requires lane_key.");
        return new LaneWait(laneKey.Trim(), string.IsNullOrWhiteSpace(predicate) ? LaneWait.LaneDone : predicate!, criteria, hash);
    }

    public override void Write(Utf8JsonWriter writer, LaneWait value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("lane_key", value.LaneKey);
        writer.WriteString("predicate", value.Predicate);
        writer.WriteStartArray("required_criteria");
        foreach (var criterion in value.RequiredCriteria) writer.WriteStringValue(criterion);
        writer.WriteEndArray();
        if (value.ObservedFactsHash is { } hash) writer.WriteString("observed_facts_hash", hash);
        writer.WriteEndObject();
    }
}

/// <summary>A named execution lane recorded in the run checkpoint.</summary>
internal sealed class DurableLane
{
    public string LaneKey { get; set; } = string.Empty;

    /// <summary>
    /// Lane phase: planning | executing | waiting | gate_review | reviewing |
    /// finalizing | done | failed. "completed" is the M2 spelling of "done"
    /// and is still written/read for checkpoint compatibility. See
    /// <see cref="LaneStatus"/> for the value constants.
    /// </summary>
    public string Status { get; set; } = LaneStatus.Pending;

    /// <summary>Supervision attribution copied from the run-level verdict.</summary>
    public int SupervisionRound { get; set; }
    public string? SupervisionDecision { get; set; }
    public List<string> SupervisionReasons { get; set; } = [];

    /// <summary>
    /// Set when the lane's gate escalates (model decision or a rejected stale
    /// proceed). Only this lane freezes; the run status becomes awaiting_user
    /// while other lanes keep advancing.
    /// </summary>
    public bool Escalated { get; set; }

    /// <summary>Facts hash observed at this lane's most recent gate review.</summary>
    public string? LastGateFactsHash { get; set; }
}

/// <summary>
/// Lane lifecycle values recorded in <see cref="DurableLane.Status"/>. These are
/// persisted checkpoint strings (and appear in projected payloads), so the values
/// are a contract: rename the constant, never the value.
/// </summary>
internal static class LaneStatus
{
    public const string Pending = "pending";
    public const string Planning = "planning";
    public const string Executing = "executing";
    public const string Waiting = "waiting";
    public const string GateReview = "gate_review";
    public const string Reviewing = "reviewing";
    public const string Finalizing = "finalizing";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>M2 spelling of <see cref="Done"/>, still read from old checkpoints.</summary>
    public const string Completed = "completed";
}
