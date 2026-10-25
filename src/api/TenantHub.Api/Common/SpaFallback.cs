namespace TenantHub.Api.Common;

/// <summary>
/// What answers when no endpoint matches (M11). A page of the SPA (a GET outside <c>/api</c> without a file
/// extension, such as <c>/o/brightline/jobs</c>) gets <c>index.html</c> from the web root, so deep links and reloads
/// work. Everything else gets a 404 problem: unknown API routes, missing files, and any page when the build is absent
/// (development and tests, where the SPA runs from its own server). Without this endpoint the fallback
/// authorization policy would answer 401 for any unknown URL.
/// </summary>
public static class SpaFallback
{
    public static IResult Handle(HttpContext context, IWebHostEnvironment environment)
    {
        var request = context.Request;
        var isPage = (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && !request.Path.StartsWithSegments("/api")
            && !Path.HasExtension(request.Path.Value);

        if (isPage && environment.WebRootFileProvider.GetFileInfo("index.html") is { Exists: true } index)
        {
            // The page must be revalidated, so a new release's script names reach the browser at once.
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Stream(index.CreateReadStream(), "text/html; charset=utf-8");
        }

        return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
    }
}
