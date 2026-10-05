using System.Collections.Concurrent;
using TinadecCore.Persistence;

namespace TinadecCore.Models.Harness;

/// <summary>
/// The governed working directory one harness provider runs in — the same directory whichever channel
/// it is reached over.
/// <para>
/// This is the boundary that matters for a local agent: a harness reads and writes its own
/// <c>cwd</c> with its own tools no matter what capability bits were declared in a handshake, so
/// choosing the directory is choosing the blast radius. It lives here rather than on the ACP session
/// host because the one-shot headless channel needs exactly the same decision; two owners would mean
/// the same provider editing files in two different places depending on which channel a run picked.
/// </para>
/// </summary>
internal interface IHarnessWorkspaceRoots
{
    string ForProvider(Guid providerInstanceId);
}

internal sealed class HarnessWorkspaceRoots(StoragePaths paths) : IHarnessWorkspaceRoots
{
    private readonly ConcurrentDictionary<Guid, string> _directories = new();

    public string ForProvider(Guid providerInstanceId) => _directories.GetOrAdd(providerInstanceId, id =>
    {
        // The second GUID is minted here rather than derived from the provider, so two providers that
        // ever share an identity token still cannot collide on a directory.
        var path = paths.HarnessWorkspace(id, Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    });
}
