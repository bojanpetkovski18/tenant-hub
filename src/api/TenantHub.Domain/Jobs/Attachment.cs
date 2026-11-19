namespace TenantHub.Domain.Jobs;

/// <summary>
/// A photo of a job (SPEC-V2 4.5, 5.6), as stored: already re-encoded without metadata, its bytes outside the database
/// under random keys (D043). The job adds and removes it, so the job's rules and audit apply; the row is what serves
/// the file, so a photo without a row is never shown and the weekly sweep removes its files.
/// </summary>
public sealed class Attachment : ITenantOwned
{
    private Attachment()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid JobId { get; private set; }

    public string StorageKey { get; private set; } = "";

    public string ThumbnailKey { get; private set; } = "";

    public string ContentType { get; private set; } = "";

    /// <summary>What this photo takes of the plan's storage (SPEC-V2 4.8): the image and its thumbnail together.</summary>
    public long Bytes { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    internal static Attachment Create(Job job, StoredPhoto photo, Guid userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OrganizationId = job.OrganizationId,
        JobId = job.Id,
        StorageKey = photo.StorageKey,
        ThumbnailKey = photo.ThumbnailKey,
        ContentType = photo.ContentType,
        Bytes = photo.Bytes,
        Width = photo.Width,
        Height = photo.Height,
        CreatedAt = now,
        CreatedByUserId = userId,
    };
}

/// <summary>A processed photo whose files are written: the keys, its type, the bytes of both files and its size in pixels.</summary>
public sealed record StoredPhoto(string StorageKey, string ThumbnailKey, string ContentType, long Bytes, int Width, int Height);
