using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Application.Clients;
using TenantHub.Application.Invitations;
using TenantHub.Application.Organizations;
using TenantHub.Application.Tenancy;

namespace TenantHub.Application;

public static class ApplicationSetup
{
    /// <summary>Handlers are registered directly; there is no mediator (D003).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<TenantContext>()
        .AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>())
        .AddScoped<ResolveTenantQuery>()
        .AddScoped<OrganizationCreator>()
        .AddScoped<SignUpHandler>()
        .AddScoped<GetSessionQuery>()
        .AddScoped<UpdateProfileHandler>()
        .AddScoped<ChangePasswordHandler>()
        .AddScoped<CreateOrganizationHandler>()
        .AddScoped<GetOrganizationQuery>()
        .AddScoped<ListMembersQuery>()
        .AddScoped<MemberCommands>()
        .AddScoped<InvitationCommands>()
        .AddScoped<InvitationAcceptance>()
        .AddScoped<ListClientsQuery>()
        .AddScoped<GetClientQuery>()
        .AddScoped<ClientCommands>();
}
