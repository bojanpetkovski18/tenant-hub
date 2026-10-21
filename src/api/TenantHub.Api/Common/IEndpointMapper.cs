namespace TenantHub.Api.Common;

/// <summary>
/// Extra endpoints contributed through DI. The app registers none; integration tests use it to add routes that
/// exercise the real pipeline (an endpoint that throws, one with no authorization metadata).
/// </summary>
public interface IEndpointMapper
{
    void Map(IEndpointRouteBuilder app);
}
