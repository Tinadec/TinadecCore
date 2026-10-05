using Microsoft.Extensions.Logging;

namespace TinadecCore.Models.Harness;

/// <summary>
/// Shuts down a stdout/stderr drainer without disposing its <see cref="Task"/>.
/// <c>Task.Dispose()</c> throws while the task is still running ("A task may only be disposed if
/// it is in a completion state"), which is exactly its state during process shutdown — the drain
/// only ends once the killed child closes its pipes. Disposal exists to release a rare event
/// allocation, so awaiting with a bound is the honest replacement; it also keeps the log tail.
/// </summary>
internal static class CliDrainShutdown
{
    public static async Task AwaitQuietlyAsync(Task drainer, TimeSpan timeout, ILogger logger)
    {
        try
        {
            await drainer.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogDebug("Child process output drain did not finish within {TimeoutSeconds}s; abandoning it.", timeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Child process output drain ended with an expected stream error.");
        }
    }
}
