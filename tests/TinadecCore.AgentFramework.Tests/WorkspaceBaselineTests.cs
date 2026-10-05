using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Workspace baseline (DmaEA workspace binding): the root a run works in is
/// resolved once at admission from Core-owned records, frozen into the body, and
/// never re-resolved on recovery. An agent that holds a provider tool face gets
/// the whole-root read level unless its envelope already narrowed the grants;
/// write access is never implied, and a projectless run keeps the fail-closed
/// empty grant list.
/// </summary>
public sealed class WorkspaceBaselineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-ws-" + Guid.NewGuid().ToString("N"));

    public WorkspaceBaselineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── filesystem probe ──────────────────────────────────────────────────────

    [Fact]
    public void ProbeGit_ReadsBranch_FromRepositoryAndFromLinkedWorktree()
    {
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        File.WriteAllText(Path.Combine(repository, ".git", "HEAD"), "ref: refs/heads/Astra\n");
        var (isGit, branch) = WorkspaceProbe.ProbeGit(repository);
        Assert.True(isGit);
        Assert.Equal("Astra", branch);

        // A linked worktree carries a .git FILE pointing at the real gitdir.
        var gitDir = Directory.CreateDirectory(Path.Combine(_root, "gitdir")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/x\n");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktree")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + gitDir + "\n");
        var (worktreeIsGit, worktreeBranch) = WorkspaceProbe.ProbeGit(worktree);
        Assert.True(worktreeIsGit);
        Assert.Equal("feature/x", worktreeBranch);

        // Detached HEAD is still a repository, just without a branch name.
        File.WriteAllText(Path.Combine(repository, ".git", "HEAD"), new string('a', 40) + "\n");
        var (detachedIsGit, detachedBranch) = WorkspaceProbe.ProbeGit(repository);
        Assert.True(detachedIsGit);
        Assert.Null(detachedBranch);
    }

    [Fact]
    public void ProbeGit_WithoutGitDirectory_IsNotARepository()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        Assert.Equal((false, (string?)null), WorkspaceProbe.ProbeGit(plain));
        Assert.Equal((false, (string?)null), WorkspaceProbe.ProbeGit(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void ListTopLevel_ListsDirectoriesWithSlash_AndFlagsTruncation()
    {
        var small = Directory.CreateDirectory(Path.Combine(_root, "small")).FullName;
        Directory.CreateDirectory(Path.Combine(small, "src"));
        File.WriteAllText(Path.Combine(small, "README.md"), "x");
        var (entries, truncated) = WorkspaceProbe.ListTopLevel(small);
        Assert.False(truncated);
        Assert.Equal(["README.md", "src/"], entries);

        var big = Directory.CreateDirectory(Path.Combine(_root, "big")).FullName;
        for (var index = 0; index < WorkspaceProbe.TopLevelEntryLimit + 5; index++)
        {
            File.WriteAllText(Path.Combine(big, $"file-{index:D3}.txt"), "x");
        }
        var (capped, cappedTruncated) = WorkspaceProbe.ListTopLevel(big);
        Assert.True(cappedTruncated);
        Assert.Equal(WorkspaceProbe.TopLevelEntryLimit, capped.Count);

        var (missingEntries, missingTruncated) = WorkspaceProbe.ListTopLevel(Path.Combine(_root, "missing"));
        Assert.Empty(missingEntries);
        Assert.False(missingTruncated);
    }

    // ── default grants ────────────────────────────────────────────────────────

    [Fact]
    public void DefaultGrants_DeclaredEnvelopeWins_EvenWhenItIsEmptyForAProviderFace()
    {
        FrozenResourceGrant[] declared = [new("src", "write")];
        var resolved = WorkspaceGrantDefaults.Resolve(Binding(), declared, ["read_file"]);
        Assert.Same(declared, resolved);

        // No binding at all (projectless) and a Core-virtual-only face both stay
        // fail-closed: the historical empty grant list is preserved.
        Assert.Empty(WorkspaceGrantDefaults.Resolve(null, [], ["read_file"]));
        Assert.Empty(WorkspaceGrantDefaults.Resolve(Binding(), [], [CoreVirtualToolPolicy.CreateWorkspaceToolId]));
        Assert.Empty(WorkspaceGrantDefaults.Resolve(Binding(), [], []));
    }

    [Fact]
    public void DefaultGrants_ProviderFace_ReadsWholeWorkspace()
    {
        var wildcard = WorkspaceGrantDefaults.Resolve(Binding(), [], ["*"]);
        Assert.Single(wildcard);
        Assert.Equal("read", wildcard[0].Level);
        Assert.Equal(string.Empty, wildcard[0].PathPrefix);

        // The grant string the engine seeds into the instance is the WS-4 form.
        Assert.Equal(["read:"], wildcard.Select(grant => $"{grant.Level}:{grant.PathPrefix}"));

        var mixed = WorkspaceGrantDefaults.Resolve(
            Binding(),
            [],
            [CoreVirtualToolPolicy.CreateWorkspaceToolId, "git_status"]);
        Assert.Single(mixed);
        Assert.Equal("read", mixed[0].Level);
    }

    // ── frozen section ────────────────────────────────────────────────────────

    [Fact]
    public void WorkspaceSection_RoundTrips_AndIsOmittedWhenNull()
    {
        var binding = Binding();
        var body = JsonSerializer.Serialize(MinimalConfiguration(binding), FrozenRunConfigurationV1.JsonOptions);
        Assert.Contains("\"workspace\":", body, StringComparison.Ordinal);
        Assert.Contains("\"pathContract\":\"absolute-in-root\"", body, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(body, FrozenRunConfigurationV1.JsonOptions);
        Assert.NotNull(roundTripped?.Workspace);
        Assert.Equal(binding.RootPath, roundTripped.Workspace.RootPath);
        Assert.True(roundTripped.Workspace.IsGitRepository);
        Assert.Equal("Astra", roundTripped.Workspace.GitBranch);

        // Projectless bodies keep the byte-shape they had before the section existed.
        var projectless = JsonSerializer.Serialize(MinimalConfiguration(null), FrozenRunConfigurationV1.JsonOptions);
        Assert.DoesNotContain("\"workspace\":", projectless, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyV2Body_WithoutWorkspaceSection_ReadsAsNoWorkspace_AndIsSuperseded()
    {
        // A body frozen before the section existed still *deserializes* — retired
        // members (applicationMode/agentMode/workspaceOverride) are ignored — and
        // reads as "no workspace". It is not migrated: the body keeps its own
        // schema literal, and the engine's resume gate fails it closed with
        // run_schema_superseded rather than re-interpreting v3 semantics onto it.
        const string legacy = """
        {"schemaVersion":"frozen-run-configuration/v2","baselineHash":"b","baselineVersion":1,
         "applicationMode":"conversation","agentMode":"vibe","runtimeProfileId":"p","permissionMode":"ask",
         "spawn":{"maxDepth":1,"maxAgentsPerRun":2,"maxParallelWorkers":1},
         "scheduling":{"maxActiveRunsPerSession":1,"workerRetryLimit":1,"preservePartialResults":true},
         "supervision":{"requiredBeforeFinal":false,"maxRevisionRounds":1},
         "context":{"defaultTokenBudget":8192,"recentMessageLimit":10,"optimisticRevision":true},
         "memory":{"candidateOnly":true,"retrievalLimit":4,"allowedScopes":[],"allowedKinds":[]},
         "tools":{"provider":"tinadec-tools-process","mutationRequiresApproval":true,"serializeWorkspaceWrites":true,"defaultTimeoutSeconds":120,"maxToolRounds":4},
         "operationAgents":[],"executionAgents":[],"workspaceOverride":null,"bindings":[],
         "graph":{"tier":"free_form","conversationTemplateSlug":null,"conversationNodeKey":null,"nodes":[],"edges":[]}}
        """;
        var parsed = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(legacy, FrozenRunConfigurationV1.JsonOptions);
        Assert.NotNull(parsed);
        Assert.Null(parsed.Workspace);
        Assert.Equal("frozen-run-configuration/v2", parsed.SchemaVersion);
        Assert.NotEqual(FrozenRunConfigurationV1.CurrentSchemaVersion, parsed.SchemaVersion);
    }

    // ── admission factory ─────────────────────────────────────────────────────

    [Fact]
    public async Task Factory_FreezesRootAndFacts_ForTheSessionProject()
    {
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        File.WriteAllText(Path.Combine(repository, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repository, "app.sln"), "x");
        var locator = new FakeLocator(repository);

        var binding = await WorkspaceBindingFactory.TryCreateAsync(
            locator, locator.Session, CancellationToken.None);

        Assert.NotNull(binding);
        Assert.Equal(repository, binding.RootPath);
        Assert.True(binding.IsGitRepository);
        Assert.Equal("main", binding.GitBranch);
        Assert.Contains("app.sln", binding.TopLevelEntries);
        Assert.Equal(WorkspacePathContracts.AbsoluteInRoot, binding.PathContract);
        Assert.Empty(binding.ReadOnlyRoots);
    }

    [Fact]
    public async Task Factory_RejectsProjectlessSessions_AndForeignProjects()
    {
        var locator = new FakeLocator(_root);
        var projectless = locator.Session with { ProjectId = null };
        Assert.Null(await WorkspaceBindingFactory.TryCreateAsync(locator, projectless, CancellationToken.None));

        // A project owned by another tenant must never leak its root into the body.
        locator.ForeignTenant = true;
        Assert.Null(await WorkspaceBindingFactory.TryCreateAsync(locator, locator.Session, CancellationToken.None));
    }

    private static FrozenWorkspaceBinding Binding() =>
        new(Guid.NewGuid(), @"C:\work\app", [], IsGitRepository: true, GitBranch: "Astra",
            TopLevelEntries: ["src/", "app.sln"], TopLevelTruncated: false,
            PathContract: WorkspacePathContracts.AbsoluteInRoot);

    private static FrozenRunConfigurationV1 MinimalConfiguration(FrozenWorkspaceBinding? workspace) => new(
        FrozenRunConfigurationV1.CurrentSchemaVersion, "baseline-hash", 1,
        Guid.Parse("00000000-0000-0000-0000-0000000000bb"), "profile-id", "ask",
        new SpawnPolicy(2, 16, 4),
        new SchedulingPolicy(2, 2, true),
        new SupervisionPolicy(true, 2),
        new ContextPolicy(8192, 24, true),
        new MemoryPolicy(true, 8, ["workspace"], ["fact"]),
        new ToolRuntimePolicy("tinadec-tools-process", true, true, 120, 4),
        [],
        [],
        [],
        "")
    { Workspace = workspace };

    private sealed class FakeLocator : ISessionLocator
    {
        private readonly SessionReference _session;

        public FakeLocator(string rootPath)
        {
            RootPath = rootPath;
            _session = new SessionReference(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        }

        public string RootPath { get; }
        public bool ForeignTenant { get; set; }

        public SessionReference Session => _session;

        public Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionReference?>(_session);

        public Task<ProjectReference?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectReference?>(new ProjectReference(
                projectId,
                ForeignTenant ? Guid.NewGuid() : _session.TenantId,
                _session.WorkspaceId,
                RootPath));
    }
}
