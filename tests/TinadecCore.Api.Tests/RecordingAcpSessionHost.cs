using System.Text.Json;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.Api.Tests;

/// <summary>
/// An <see cref="IAcpSessionHost"/> that records what it was asked for instead of spawning anything.
/// The control plane's job at the ACP seam is to request the right handshake and let go of the process
/// afterwards, and both halves are observable here — a real harness binary is not in CI, so a test that
/// needed one would prove nothing about Core.
/// </summary>
internal sealed class RecordingAcpSessionHost : IAcpSessionHost
{
    private readonly Dictionary<Guid, string> _scratch = new();

    public List<AcpSessionRequest> Requests { get; } = [];

    public List<Guid> Dropped { get; } = [];

    public int TurnLeases { get; private set; }

    public Exception? FailWith { get; set; }

    public string ScratchDirectoryFor(Guid providerInstanceId) => _scratch.TryGetValue(providerInstanceId, out var existing)
        ? existing
        : _scratch[providerInstanceId] = Path.Combine(
            Path.GetTempPath(), "tinadec-acp-recorded-host", providerInstanceId.ToString("N"));

    public Task<IAcpAgentSession> AcquireAsync(AcpSessionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (FailWith is { } failure) return Task.FromException<IAcpAgentSession>(failure);
        return Task.FromResult<IAcpAgentSession>(new RecordedSession(request));
    }

    public Task<IDisposable> AcquireTurnLeaseAsync(Guid providerInstanceId, CancellationToken cancellationToken = default)
    {
        TurnLeases++;
        return Task.FromResult<IDisposable>(new NoopLease());
    }

    public Task DropAsync(Guid providerInstanceId, CancellationToken cancellationToken = default)
    {
        Dropped.Add(providerInstanceId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    internal sealed class RecordedSession : IAcpAgentSession
    {
        public int Prompts { get; private set; }

        public string Answer { get; set; } = "recorded answer";

        public RecordedSession(AcpSessionRequest request)
        {
            SessionId = $"recorded-{request.HarnessId}";
            ScratchDirectory = request.ScratchDirectory;
        }

        public string SessionId { get; }

        public int Generation => 1;

        public string ScratchDirectory { get; }

        public AcpAgentCapabilitiesSnapshot Capabilities { get; } = new(true, true, false);

        public JsonElement? Configuration => null;

        public bool IsFaulted => false;

        public Exception? Fault => null;

        public Task<AcpTurnResult> PromptAsync(string prompt, IAcpTurnObserver observer, CancellationToken cancellationToken)
        {
            Prompts++;
            observer.TextDelta(Answer);
            return Task.FromResult(new AcpTurnResult(Answer, "end_turn", AcpTurnCompletion.Completed, new AcpUsageSnapshot(12, 100, null)));
        }

        public Task CancelTurnAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetModeAsync(string modeId, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
