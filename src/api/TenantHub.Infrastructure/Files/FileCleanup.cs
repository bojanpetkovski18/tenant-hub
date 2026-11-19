using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
using TenantHub.Application.Tenancy;
using TenantHub.Infrastructure.Background;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Files;

/// <summary>
/// Removes files whose rows a committed change dropped (D043): a photo, a deleted job's photos, or a deleted
/// organization's folder. Deleting what is already gone is fine, so delivering the message twice is harmless.
/// </summary>
internal sealed class FileCleanupHandler(IFileStore files) : IOutboxHandler
{
    public async Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        var cleanup = message.Read<FileCleanup>();
        if (cleanup.Keys.Any(key => !FileKeys.IsValid(key)))
        {
            throw new OutboxDeliveryException("invalid-key", permanent: true);
        }

        foreach (var key in cleanup.Keys)
        {
            await files.DeleteAsync(key, cancellationToken);
        }

        if (cleanup.Organization is { } organization)
        {
            await files.DeleteOrganizationAsync(organization, cancellationToken);
        }
    }
}

/// <summary>
/// The weekly orphan sweep (SPEC-V2 5.6, D043): files no row refers to. They come from an upload whose transaction
/// failed after the write and whose immediate clean-up failed too, a crash in between, or an organization deleted
/// without the outbox (the demo reset). A file younger than a day is left alone, so an upload still on its way to
/// its commit is never taken. Each organization is read in its own scope, as the system, inside row-level security.
/// </summary>
internal sealed partial class FileSweepTask(
    IServiceScopeFactory scopes,
    IFileStore files,
    AppDbContext db,
    ILogger<FileSweepTask> logger) : IScheduledTask
{
    public const string Name = "files.sweep";

    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    public async Task<DateTimeOffset> RunAsync(ScheduledRun run, CancellationToken cancellationToken)
    {
        var removed = 0;
        await foreach (var organization in files.OrganizationsAsync(cancellationToken))
        {
            if (!await db.Organizations.AnyAsync(o => o.Id == organization, cancellationToken))
            {
                await files.DeleteOrganizationAsync(organization, cancellationToken);
                removed++;
                continue;
            }

            removed += await SweepAsync(organization, run.Now - Grace, cancellationToken);
        }

        LogSwept(logger, removed);
        return run.Now + Interval;
    }

    private async Task<int> SweepAsync(Guid organization, DateTimeOffset writtenBefore, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().EnterAsSystem(organization);
        var tenantDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var kept = (await tenantDb.Attachments.AsNoTracking()
                .Select(a => new { a.StorageKey, a.ThumbnailKey })
                .ToListAsync(cancellationToken))
            .SelectMany(a => new[] { a.StorageKey, a.ThumbnailKey })
            .ToHashSet(StringComparer.Ordinal);

        var removed = 0;
        await foreach (var file in files.FilesOfAsync(organization, cancellationToken))
        {
            if (file.WrittenAt < writtenBefore && !kept.Contains(file.Key))
            {
                await files.DeleteAsync(file.Key, cancellationToken);
                removed++;
            }
        }

        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "File sweep: {Removed} orphaned files or folders removed.")]
    private static partial void LogSwept(ILogger logger, int removed);
}
