using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Tools;

/// <summary>
/// WS-8 per-tool resource-path extraction for the resource envelope.
///
/// The envelope authorizes a workspace-relative path PREFIX per tool call, so
/// Core has to know which argument names the target path. The table covers every
/// provider tool that has a single workspace target; a tool with no single path
/// (e.g. <c>mcp_search</c>, <c>mcp_invoke</c>) yields "no single path" and falls
/// back to the level-only decision, which never widens anything — the tool process
/// still refuses every path outside its own workspace root.
///
/// Parameter names come from the real TinadecTools definitions, never guessed:
/// <c>read_file</c>/<c>write_file</c> bind <c>filepath</c>, <c>ls</c>/<c>stat</c>/
/// <c>file_search</c> bind <c>path</c>, <c>shell</c> binds <c>cwd</c>,
/// <c>command_run</c> binds <c>working_directory</c>, and every <c>git_*</c> tool
/// binds <c>repository_path</c>.
/// </summary>
internal static class ToolResourcePathRegistry
{
    private const string GitPathParameter = "repository_path";

    /// <summary>Tool id -> the JSON parameter carrying the target path.</summary>
    private static readonly IReadOnlyDictionary<string, string> PathParameterByTool =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["read_file"] = "filepath",
            ["write_file"] = "filepath",
            // The line/byte mutation family binds the same "filepath" property
            // (FileWriter.cs): without them the WS-8 prefix narrowing silently
            // degraded to a level-only decision for every edit after the first write.
            ["replace_lines"] = "filepath",
            ["replace_bytes"] = "filepath",
            ["insert_line"] = "filepath",
            ["insert_bytes"] = "filepath",
            ["insert_byte"] = "filepath",
            ["delete_line"] = "filepath",
            ["delete_bytes"] = "filepath",
            ["ls"] = "path",
            ["stat"] = "path",
            ["file_search"] = "path",
            ["shell"] = "cwd",
            ["command_run"] = "working_directory",
        };

    /// <summary>The path parameter of a tool, or null when the tool has no single workspace target.</summary>
    private static string? PathParameter(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return null;
        if (PathParameterByTool.TryGetValue(toolId, out var parameter)) return parameter;
        // Every git tool targets the repository it was handed.
        return toolId.StartsWith("git_", StringComparison.OrdinalIgnoreCase) ? GitPathParameter : null;
    }

    /// <summary>Whether the tool declares a single target path enforced this phase.</summary>
    public static bool IsRegistered(string? toolId) => PathParameter(toolId) is not null;

    /// <summary>
    /// Extract the workspace-relative target path of a tool call.
    ///
    /// Returns null — meaning "level-only decision" — when the tool has no single
    /// path, the parameters are unusable, or the target escapes the workspace.
    /// The escaping case is still fail-closed overall: the tool process resolves
    /// the same path against its workspace root and refuses anything outside it,
    /// so falling back to the level decision cannot grant a path the prefix would
    /// have refused.
    /// </summary>
    public static string? TryExtractRelativePath(string? toolId, string? parametersJson, string? workspaceRoot)
    {
        var parameter = PathParameter(toolId);
        if (parameter is null || string.IsNullOrWhiteSpace(parametersJson)) return null;
        string? raw;
        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(parameter, out var element)) return null;
            if (element.ValueKind != JsonValueKind.String) return null;
            raw = element.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return NormalizeRelativePath(ToRelativePath(raw, workspaceRoot));
    }

    /// <summary>
    /// Build the resource claim for one tool call, or null when the call has no
    /// single target path. The claim action mirrors the tool's mutation class so
    /// the PDP resource boundary can apply the read/write level to the target.
    /// </summary>
    public static CapabilityClaim? TryBuildResourceClaim(
        string? toolId,
        string? parametersJson,
        string? workspaceRoot,
        bool mutating)
    {
        var relativePath = TryExtractRelativePath(toolId, parametersJson, workspaceRoot);
        if (relativePath is null) return null;
        return new CapabilityClaim("resource.access", mutating ? "mutate" : "read", $"path://{relativePath}");
    }

    /// <summary>
    /// Read the workspace-relative target a resource claim carries
    /// (<c>resource.access</c> / <c>path://&lt;workspace-relative&gt;</c>).
    /// Returns null for an absent claim or any other resource shape — "no path
    /// dimension", so the level decision stands alone.
    /// </summary>
    public static string? TryReadResourceClaimPath(CapabilityClaim? resourceClaim)
    {
        if (resourceClaim is null) return null;
        var resource = resourceClaim.Resource;
        if (string.IsNullOrWhiteSpace(resource)) return null;
        if (!resource.StartsWith("path://", StringComparison.OrdinalIgnoreCase)) return null;
        return NormalizeRelativePath(resource[7..]);
    }

    /// <summary>
    /// Normalize a target into a forward-slash workspace-relative path. Returns
    /// null when the target cannot be expressed relative to the workspace —
    /// including any ".." that would walk out of it.
    /// </summary>
    public static string? NormalizeRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var segments = new List<string>();
        foreach (var segment in raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") return null;
            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string? ToRelativePath(string? raw, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!Path.IsPathRooted(trimmed)) return trimmed;
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), trimmed);
            // Across volumes GetRelativePath returns a rooted path instead of a ".." walk, and a
            // rooted answer is not a workspace-relative claim: the grant would name a path the tool
            // process itself refuses to touch, so the two layers would disagree about who is covered.
            return Path.IsPathRooted(relative) ? null : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }
}
