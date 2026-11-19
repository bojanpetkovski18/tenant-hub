using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Organizations;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Jobs;

public sealed record PhotoView(Guid Id, string ContentType, int Width, int Height, long Bytes, DateTimeOffset CreatedAt, string CreatedByName);

/// <summary>A job's photos with what the caller may do; <see cref="CannotAdd"/> says why not to someone who could otherwise.</summary>
public sealed record PhotosView(IReadOnlyList<PhotoView> Items, bool CanAdd, bool CanRemove, string? CannotAdd, int MaxPhotos);

/// <summary>A stored file on its way to the response: the caller disposes the stream.</summary>
public sealed record PhotoFile(Stream Content, string ContentType, string FileName);

/// <summary>
/// Files to remove once a change that dropped their rows has committed (SPEC-V2 5.6, D043): a photo's two files, every
/// file of a deleted job, or a deleted organization's whole folder. Not tied to an organization in the outbox, so the
/// message outlives the organization whose deletion wrote it.
/// </summary>
public sealed record FileCleanup(IReadOnlyList<string> Keys, Guid? Organization = null)
{
    public static readonly OutboxMessageType Type = new("files.delete", CarriesSecret: false, PerOrganization: false);
}

/// <summary>
/// Photos of a job (US-90, SPEC-V2 5.6, D043). Adding one:
/// <list type="number">
/// <item>the job and the photo count are checked first, so a refused upload costs no decoding;</item>
/// <item>the upload is read up to 10 MB, recognized by content and re-encoded (<see cref="IPhotoProcessor"/>);</item>
/// <item>both files are written <b>before</b> the transaction, under new random keys;</item>
/// <item>under the organization lock, the photo count and the storage limit are checked again and the row is saved.</item>
/// </list>
/// If anything after the write fails, the files are removed at once; a crash in between leaves files without a row,
/// which nothing serves and the weekly sweep removes. A row never points at a missing file (D043).
/// </summary>
public sealed class PhotoCommands(
    IAppDbContext db,
    ITenantContext tenant,
    IDatabaseLocks locks,
    JobViews views,
    PlanLimitsCheck limits,
    IFileStore files,
    IPhotoProcessor processor,
    IOutbox outbox,
    IIdentityService identity,
    TimeProvider time)
{
    public const string NotFoundCode = "photo-not-found";

    public async Task<PhotosView> ListAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await LoadJobAsync(jobId, cancellationToken);
        var photos = await db.Attachments.AsNoTracking().Where(a => a.JobId == jobId).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        var names = await identity.FindProfilesAsync([.. photos.Select(p => p.CreatedByUserId).Distinct()], cancellationToken);
        var actor = views.Actor();
        string? cannotAdd = null;
        var canEdit = job.CanEdit(actor);
        if (canEdit)
        {
            try
            {
                job.EnsureRoomForPhoto(photos.Count, actor);
            }
            catch (DomainException refused)
            {
                cannotAdd = refused.Message;
            }
        }

        return new PhotosView(
            [.. photos.Select(p => View(p, names.TryGetValue(p.CreatedByUserId, out var profile) ? profile.DisplayName : "A former member"))],
            CanAdd: canEdit && cannotAdd is null,
            CanRemove: canEdit && job.Status.IsOpen(),
            cannotAdd,
            Checklists.MaxPhotos);
    }

    public async Task<PhotoView> AddAsync(Guid jobId, Stream upload, CancellationToken cancellationToken)
    {
        var job = await LoadJobAsync(jobId, cancellationToken);
        var actor = views.Actor();
        job.EnsureRoomForPhoto(await CountAsync(jobId, cancellationToken), actor);

        var processed = processor.Process(await ReadLimitedAsync(upload, cancellationToken));
        var organization = tenant.OrganizationId!.Value;
        var extension = PhotoRules.Extension(processed.Format);
        var stored = new StoredPhoto(
            FileKeys.New(organization, extension),
            FileKeys.New(organization, extension),
            PhotoRules.ContentType(processed.Format),
            processed.Bytes,
            processed.Width,
            processed.Height);

        await files.WriteAsync(stored.StorageKey, processed.Image, cancellationToken);
        try
        {
            await files.WriteAsync(stored.ThumbnailKey, processed.Thumbnail, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await locks.LockOrganizationAsync(organization, cancellationToken);
            await limits.EnsureRoomForAsync(LimitKind.Storage, cancellationToken, adding: stored.Bytes);
            var tracked = await db.Jobs.SingleAsync(j => j.Id == jobId, cancellationToken);
            var attachment = tracked.AddPhoto(stored, await CountAsync(jobId, cancellationToken), actor, time.GetUtcNow());
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var name = (await identity.FindProfilesAsync([actor.UserId], cancellationToken)).GetValueOrDefault(actor.UserId)?.DisplayName ?? "A former member";
            return View(attachment, name);
        }
        catch
        {
            // Not saved, so nothing refers to the files: remove them now, whatever the request's cancellation says.
            await DeleteQuietlyAsync(stored.StorageKey);
            await DeleteQuietlyAsync(stored.ThumbnailKey);
            throw;
        }
    }

    public async Task<PhotoFile> OpenAsync(Guid jobId, Guid photoId, bool thumbnail, CancellationToken cancellationToken)
    {
        var photo = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == photoId && a.JobId == jobId, cancellationToken)
            ?? throw NotFound();
        var number = await db.Jobs.Where(j => j.Id == jobId).Select(j => j.Number).SingleAsync(cancellationToken);
        return await OpenAsync(photo, Job.FormatNumber(number), thumbnail, files, cancellationToken);
    }

    public async Task RemoveAsync(Guid jobId, Guid photoId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken) ?? throw JobErrors.NotFound();
        var photo = await db.Attachments.SingleOrDefaultAsync(a => a.Id == photoId && a.JobId == jobId, cancellationToken) ?? throw NotFound();
        job.RemovePhoto(photo, await CountAsync(jobId, cancellationToken), views.Actor());
        db.Attachments.Remove(photo);
        outbox.Add(FileCleanup.Type, new FileCleanup([photo.StorageKey, photo.ThumbnailKey]));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Opens a photo's file for the staff and the portal endpoints alike.</summary>
    public static async Task<PhotoFile> OpenAsync(Attachment photo, string jobLabel, bool thumbnail, IFileStore files, CancellationToken cancellationToken)
    {
        var key = thumbnail ? photo.ThumbnailKey : photo.StorageKey;
        var content = await files.OpenReadAsync(key, cancellationToken) ?? throw NotFound();
        var extension = key[(key.LastIndexOf('.') + 1)..];
        return new PhotoFile(content, photo.ContentType, $"{jobLabel}-photo{(thumbnail ? "-thumbnail" : "")}.{extension}");
    }

    public static DomainException NotFound() => new(NotFoundCode, "Photo not found.", DomainErrorKind.NotFound);

    public static PhotoView View(Attachment p, string createdByName) =>
        new(p.Id, p.ContentType, p.Width, p.Height, p.Bytes, p.CreatedAt, createdByName);

    /// <summary>Reads at most the upload limit and one byte more, so an oversized upload is refused without being read whole.</summary>
    private static async Task<ReadOnlyMemory<byte>> ReadLimitedAsync(Stream upload, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await upload.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > PhotoRules.MaxUploadBytes)
            {
                throw PhotoRejectedException.TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? throw PhotoRejectedException.NotAnImage() : buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    }

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await files.DeleteAsync(key, CancellationToken.None);
        }
        catch (IOException)
        {
            // The sweep removes what is left (D043).
        }
    }

    private Task<int> CountAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Attachments.CountAsync(a => a.JobId == jobId, cancellationToken);

    private async Task<Job> LoadJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken) ?? throw JobErrors.NotFound();
}
