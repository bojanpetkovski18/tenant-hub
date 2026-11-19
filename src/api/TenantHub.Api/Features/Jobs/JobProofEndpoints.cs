using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Jobs;

// Shape only; the checklist rules (SPEC-V2 5.6) are the domain's.
public sealed record ChecklistItemRequest([property: MaxLength(1000)] string? Text, bool? Required);

public sealed record ChecklistChangeRequest([property: MaxLength(1000)] string? Text, bool? Required, bool? Done, int? Position);

/// <summary>
/// The proof of the work on a job (SPEC-V2 4.5, 7): its checklist and its photos. Reading needs <c>jobs.read</c>;
/// changing needs <c>jobs.work</c>, and the domain decides whether the job is the caller's to change.
/// </summary>
public static class JobProofEndpoints
{
    public static IEndpointRouteBuilder MapJobProofEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapTenantGroup().MapGroup("/jobs").WithTags("Jobs");

        jobs.MapGet("/{id:guid}/checklist", async (Guid id, ChecklistCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ListAsync(id, ct)))
            .RequirePermission(Permissions.JobsRead);

        jobs.MapPost("/{id:guid}/checklist", async (Guid id, ChecklistItemRequest request, ChecklistCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.AddAsync(id, new ChecklistItemInput(request.Text, request.Required), ct)))
            .RequirePermission(Permissions.JobsWork);

        jobs.MapPut("/{id:guid}/checklist/{itemId:guid}", async (Guid id, Guid itemId, ChecklistChangeRequest request, ChecklistCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ChangeItemAsync(id, itemId, new ChecklistChangeInput(request.Text, request.Required, request.Done, request.Position), ct)))
            .RequirePermission(Permissions.JobsWork);

        jobs.MapDelete("/{id:guid}/checklist/{itemId:guid}", async (Guid id, Guid itemId, ChecklistCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.RemoveAsync(id, itemId, ct)))
            .RequirePermission(Permissions.JobsWork);

        jobs.MapGet("/{id:guid}/photos", async (Guid id, PhotoCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ListAsync(id, ct)))
            .RequirePermission(Permissions.JobsRead);

        jobs.MapPost("/{id:guid}/photos", async (Guid id, HttpRequest request, PhotoCommands commands, CancellationToken ct) =>
            {
                var photo = await commands.AddAsync(id, await PhotoUpload.OpenFileAsync(request, ct), ct);
                return TypedResults.Created($"{request.Path.Value!.TrimEnd('/')}/{photo.Id}", photo);
            })
            .RequirePermission(Permissions.JobsWork)
            .Accepts<IFormFile>("multipart/form-data")
            .WithMetadata(new RequestSizeLimitAttribute(PhotoUpload.MaxRequestBytes));

        jobs.MapGet("/{id:guid}/photos/{photoId:guid}", async (Guid id, Guid photoId, HttpContext context, PhotoCommands commands, CancellationToken ct) =>
                PhotoUpload.Serve(context, await commands.OpenAsync(id, photoId, thumbnail: false, ct)))
            .RequirePermission(Permissions.JobsRead);

        jobs.MapGet("/{id:guid}/photos/{photoId:guid}/thumbnail", async (Guid id, Guid photoId, HttpContext context, PhotoCommands commands, CancellationToken ct) =>
                PhotoUpload.Serve(context, await commands.OpenAsync(id, photoId, thumbnail: true, ct)))
            .RequirePermission(Permissions.JobsRead);

        jobs.MapDelete("/{id:guid}/photos/{photoId:guid}", async (Guid id, Guid photoId, PhotoCommands commands, CancellationToken ct) =>
            {
                await commands.RemoveAsync(id, photoId, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.JobsWork);

        return app;
    }
}

/// <summary>Reading an upload and serving a stored photo, for the staff and the portal endpoints.</summary>
public static class PhotoUpload
{
    /// <summary>The photo's 10 MB and room for the multipart framing; the handler enforces the 10 MB itself.</summary>
    public const long MaxRequestBytes = PhotoRules.MaxUploadBytes + (64 * 1024);

    /// <summary>
    /// The body of the request's <c>file</c> part, streamed: nothing is buffered here, so the handler's 10 MB limit and
    /// its checks before decoding apply to what is read. The part's file name and declared type are ignored (SPEC-V2 5.6).
    /// </summary>
    public static async Task<Stream> OpenFileAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            throw PhotoRejectedException.NotAnImage();
        }

        var reader = new MultipartReader(boundary, request.Body);
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                && disposition.IsFileDisposition()
                && disposition.Name.Equals("file", StringComparison.Ordinal))
            {
                return section.Body;
            }
        }

        throw PhotoRejectedException.NotAnImage();
    }

    /// <summary>
    /// A stored photo as a response (SPEC-V2 5.6): its stored type with <c>nosniff</c> (set for every response),
    /// shown inline under a file name of ours, and cached by the browser only.
    /// </summary>
    public static IResult Serve(HttpContext context, PhotoFile photo)
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "private, max-age=3600";
        headers.XContentTypeOptions = "nosniff";
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(photo.FileName);
        headers.ContentDisposition = disposition.ToString();
        return Results.Stream(photo.Content, photo.ContentType);
    }
}
