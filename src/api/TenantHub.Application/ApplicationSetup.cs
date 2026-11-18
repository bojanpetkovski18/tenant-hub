using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Application.Agreements;
using TenantHub.Application.Audit;
using TenantHub.Application.Board;
using TenantHub.Application.Clients;
using TenantHub.Application.Dashboard;
using TenantHub.Application.Demo;
using TenantHub.Application.Invitations;
using TenantHub.Application.Jobs;
using TenantHub.Application.Organizations;
using TenantHub.Application.Tenancy;

namespace TenantHub.Application;

public static class ApplicationSetup
{
    /// <summary>Handlers are registered directly; there is no mediator (D003).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<TenantContext>()
        .AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>())
        .AddScoped<RowSecurity>()
        .AddScoped<ResolveTenantQuery>()
        .AddScoped<OrganizationCreator>()
        .AddScoped<PlanLimitsCheck>()
        .AddScoped<SignUpHandler>()
        .AddScoped<GetSessionQuery>()
        .AddScoped<UpdateProfileHandler>()
        .AddScoped<ChangePasswordHandler>()
        .AddScoped<RequestPasswordResetHandler>()
        .AddScoped<ResetPasswordHandler>()
        .AddScoped<EmailVerification>()
        .AddScoped<CreateOrganizationHandler>()
        .AddScoped<GetOrganizationQuery>()
        .AddScoped<OrganizationCommands>()
        .AddScoped<ListMembersQuery>()
        .AddScoped<MemberCommands>()
        .AddScoped<InvitationCommands>()
        .AddScoped<InvitationAcceptance>()
        .AddScoped<ListClientsQuery>()
        .AddScoped<GetClientQuery>()
        .AddScoped<ClientCommands>()
        .AddScoped<ContactCommands>()
        .AddScoped<Portal.PortalSignIn>()
        .AddScoped<Portal.PortalSessions>()
        .AddScoped<Portal.PortalContext>()
        .AddScoped<Portal.PortalVisits>()
        .AddScoped<JobViews>()
        .AddScoped<ListJobsQuery>()
        .AddScoped<GetJobQuery>()
        .AddScoped<JobHistoryQuery>()
        .AddScoped<JobCommands>()
        .AddScoped<BoardQuery>()
        .AddScoped<VisitGenerator>()
        .AddScoped<AgreementCommands>()
        .AddScoped<AgreementQueries>()
        .AddScoped<DailyVisitGeneration>()
        .AddScoped<ListAuditEntriesQuery>()
        .AddScoped<DashboardQuery>()
        .AddScoped<DemoProtection>();
}
