namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// Marks a test that spawns a real <c>node</c> child process. Node is present on the CI runners and
/// on a normal development machine, but not on a machine with no toolchain; those runs skip instead
/// of reporting a transport failure they did not observe.
/// </summary>
public sealed class RequiresNodeFactAttribute : FactAttribute
{
    public RequiresNodeFactAttribute()
    {
        if (FindNode() is null) Skip = "Requires a node executable on PATH to spawn a real stdio ACP peer.";
    }

    internal static string? FindNode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node.cmd", "node" } : new[] { "node" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
