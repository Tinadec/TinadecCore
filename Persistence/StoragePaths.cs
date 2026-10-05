namespace TinadecCore.Persistence;

/// <summary>Resolves Core-owned files from identifiers below the configured data root.</summary>
public sealed class StoragePaths
{
    public StoragePaths(string contentRootPath, Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions> options)
    {
        var configuredRoot = options.Value.DataRoot;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException("TinadecPersistence:DataRoot must be configured.");
        }

        Root = Path.GetFullPath(Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(contentRootPath, configuredRoot));
    }

    public string Root { get; }

    public string SessionHistory(Guid sessionId) => Under("sessions", sessionId + ".json");
    public string TaskSnapshot(Guid runId) => Under("tasks", runId + ".tasks.json");
    public string EventLog(Guid runId) => Under("events", runId + ".events.jsonl");
    public string Artifacts(Guid runId) => Under("artifacts", runId.ToString());

    /// <summary>
    /// The governed working directory for one harness provider, on every channel that spawns it. Both
    /// segments are Core-minted GUIDs: the identity a harness reports for its own session arrives from
    /// an external process and would need sanitizing to be used as a path, so it never is.
    /// <para>
    /// This was <c>acp-sessions</c> while only the ACP channel needed it. Directories left under that
    /// name hold a previous build's harness scratch files and are not reused.
    /// </para>
    /// </summary>
    public string HarnessWorkspace(Guid providerInstanceId, Guid workspaceId) =>
        Under("harness-workspaces", Path.Combine(providerInstanceId.ToString("N"), workspaceId.ToString("N")));
    public string ProjectVectorDatabase(Guid tenantId, Guid? workspaceId, Guid projectId) =>
        Under("vectors", Path.Combine("tenants", tenantId.ToString("N"), workspaceId?.ToString("N") ?? "tenant", projectId + ".db"));

    public string ContentTemporary(Guid tenantId, Guid? workspaceId, string kind) =>
        Under("content", Path.Combine("tenants", tenantId.ToString("N"), workspaceId?.ToString("N") ?? "tenant", SanitizeSegment(kind), ".tmp", Guid.NewGuid().ToString("N")));

    public string ContentReference(Guid tenantId, Guid? workspaceId, string kind, string sha256) =>
        $"content/tenants/{tenantId:N}/{(workspaceId?.ToString("N") ?? "tenant")}/{SanitizeSegment(kind)}/{sha256}";

    public string ResolveContentReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference))
        {
            throw new InvalidOperationException("Content reference must be a relative Core-owned reference.");
        }
        return Under("", reference.Replace('/', Path.DirectorySeparatorChar));
    }

    private string Under(string directory, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(Root, directory, fileName));
        var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        // Case-insensitive only where the file system is. On Linux `/data` and `/DATA` are
        // different directories, so ignoring case here would let a reference that resolves
        // outside the root still pass the containment check.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException("Core storage path escaped the configured data root.");
        }

        return path;
    }

    private static string SanitizeSegment(string value)
    {
        if (value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new ArgumentException("Storage content kind contains invalid characters.", nameof(value));
        }
        return value;
    }
}
