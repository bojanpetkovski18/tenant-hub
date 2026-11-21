using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TenantHub.Api.Common.Auth;

/// <summary>
/// The OpenAPI document describes the bearer scheme (SPEC-V2 4.7) and names it on exactly the operations that accept a
/// key; the others carry no security requirement for it, since a key there is refused with 403 <c>api-key-not-allowed</c>.
/// </summary>
public static class ApiKeyOpenApi
{
    public static OpenApiOptions AddApiKeyScheme(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[ApiKeyAuth.Scheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "An API key from Settings → Integrations (Business plan): `Authorization: Bearer thk_…`. " +
                    "Accepted only by the operations that list this scheme, and never together with a session cookie.",
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ApiKeyAllowedMetadata>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(ApiKeyAuth.Scheme, context.Document)] = [],
                });
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
