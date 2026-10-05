using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Runtime;

/// <summary>
/// Optional first-install of a deployment-supplied agent pack. Core ships no
/// built-in roster: an empty workspace can only talk once a pack is installed.
/// A deployment that wants new workspaces pre-provisioned points
/// <c>TinadecAgent:BootstrapPackPath</c> (or drops
/// <c>Configuration/bootstrap-agent-pack.json</c> next to the binary) at an
/// ordinary pack envelope; it is installed through the same preview/apply
/// pipeline as a user-initiated install, so ownership, integrity, and managed
/// resource rules all apply. Missing file or an already-populated pack
/// directory is a silent no-op.
/// </summary>
public static class BootstrapPack
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<bool> InstallIfConfiguredAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TinadecCore.Runtime.BootstrapPack");
        var configuration = services.GetRequiredService<IConfiguration>();
        var path = ResolvePath(configuration["TinadecAgent:BootstrapPackPath"]);
        if (!File.Exists(path)) return false;

        // The bootstrap install goes through the regular pack pipeline, so it is
        // subject to the same owner-only management gate; a non-owner host simply
        // has no roster until an owner installs a pack.
        var role = services.GetRequiredService<ITenantContextAccessor>().Current.Role;
        if (!string.Equals(role, "owner", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Bootstrap pack skipped: the current principal is not the workspace owner.");
            return false;
        }

        var factory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await db.AgentPackInstallations.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Bootstrap pack skipped: this workspace already has an agent pack installed.");
                return false;
            }
        }

        AgentPackManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AgentPackManifestDto>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Bootstrap agent pack manifest '{path}' is not valid JSON.", exception);
        }
        if (manifest is null) throw new InvalidDataException($"Bootstrap agent pack manifest '{path}' is empty.");
        var envelope = AgentPackService.CreateEnvelope(manifest);

        var service = services.GetRequiredService<IAgentPackService>();
        var preview = await service.PreviewAsync(envelope, cancellationToken).ConfigureAwait(false);
        var applied = await service.ApplyAsync(
            preview.PackId,
            new AgentPackApplyRequestDto { PreviewId = preview.PreviewId, Envelope = envelope },
            expectedRevision: null,
            idempotencyKey: $"bootstrap:{preview.PackId}:{preview.IntegrityDigest}",
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Bootstrap agent pack installed (pack_id={PackId}, version={Version}, status={Status}).",
            applied.PackId, applied.ActiveVersion, applied.Status);
        return true;
    }

    private static string ResolvePath(string? configured) => !string.IsNullOrWhiteSpace(configured)
        ? Path.GetFullPath(configured)
        : Path.Combine(AppContext.BaseDirectory, "Configuration", "bootstrap-agent-pack.json");
}
