using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace TinadecCore.Abstractions.Ports;

/// <summary>Per-model inference settings stored in the immutable provider version, never in credentials.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ModelParameters
{
    public const int DefaultMaxOutputTokens = 4096;
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; init; }
    [JsonPropertyName("temperature")]
    public float? Temperature { get; init; }
    [JsonPropertyName("top_p")]
    public float? TopP { get; init; }
    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonIgnore]
    public bool IsEmpty => ReasoningEffort is null && Temperature is null && TopP is null && MaxOutputTokens is null;

    public static IReadOnlyDictionary<string, ModelParameters> ReadMap(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new Dictionary<string, ModelParameters>();
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("model_parameters must be an object keyed by model id.");
        var result = new Dictionary<string, ModelParameters>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name != entry.Name.Trim() || entry.Name.Length > 256)
                throw new ArgumentException("model_parameters requires a non-empty model id of at most 256 characters.");
            var settings = entry.Value.Deserialize<ModelParameters>()
                ?? throw new ArgumentException($"model_parameters.{entry.Name} must be an object.");
            if (settings.ReasoningEffort is not (null or "none" or "low" or "medium" or "high" or "xhigh"))
                throw new ArgumentException("reasoning_effort must be none|low|medium|high|xhigh or null for the model default.");
            if (settings.Temperature is { } temperature && (!float.IsFinite(temperature) || temperature is < 0 or > 2))
                throw new ArgumentException("temperature must be between 0 and 2.");
            if (settings.TopP is { } topP && (!float.IsFinite(topP) || topP is < 0 or > 1))
                throw new ArgumentException("top_p must be between 0 and 1.");
            if (settings.MaxOutputTokens is <= 0)
                throw new ArgumentException("max_output_tokens must be a positive integer.");
            if (!result.TryAdd(entry.Name, settings)) throw new ArgumentException("model_parameters cannot contain duplicate model ids.");
        }
        return result;
    }

    public static ModelParameters? ForModel(JsonElement providerConfig, string? model)
        => model is not null && providerConfig.TryGetProperty("model_parameters", out var map)
            && ReadMap(map).TryGetValue(model, out var settings) ? settings : null;

    public static bool SupportsReasoning(string? protocol) => ChatProtocols.Normalize(protocol) is ChatProtocols.OpenAiChat or ChatProtocols.OpenAiResponses;

    public static bool OmitsSampling(string model, string? effort) => effort is not (null or "none")
        && (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase));

    public void ApplyTo(ChatOptions options, string model)
    {
        if (Temperature is { } temperature) options.Temperature = temperature;
        if (TopP is { } topP) options.TopP = topP;
        // Replace the ordinary runtime default. Explicit budgets on short calls
        // (readiness probes, approval reviewers, structured interpretation) stay
        // ceilings, so increasing a model default cannot inflate those calls.
        if (MaxOutputTokens is { } limit)
            options.MaxOutputTokens = options.MaxOutputTokens is { } ceiling && ceiling != DefaultMaxOutputTokens
                ? Math.Min(limit, ceiling) : limit;
        if (ReasoningEffort is { } effort)
        {
            var reasoning = new ReasoningOptions { Output = options.Reasoning?.Output };
            reasoning.Effort = effort switch
            {
                "none" => Microsoft.Extensions.AI.ReasoningEffort.None,
                "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
                "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
                "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
                "xhigh" => Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh,
                _ => throw new ArgumentException("Unsupported reasoning effort.")
            };
            options.Reasoning = reasoning;
        }
        if (OmitsSampling(model, ReasoningEffort)) { options.Temperature = null; options.TopP = null; }
    }
}
