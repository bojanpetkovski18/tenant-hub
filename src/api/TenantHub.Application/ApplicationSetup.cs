using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Accounts;
using TenantHub.Application.Organizations;

namespace TenantHub.Application;

public static class ApplicationSetup
{
    /// <summary>Handlers are registered directly; there is no mediator (D003).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<OrganizationCreator>()
        .AddScoped<SignUpHandler>()
        .AddScoped<GetSessionQuery>()
        .AddScoped<UpdateProfileHandler>()
        .AddScoped<ChangePasswordHandler>()
        .AddScoped<CreateOrganizationHandler>();
}
