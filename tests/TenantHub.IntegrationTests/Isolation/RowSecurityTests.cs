using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Data;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// Layer 6 (SPEC-V2 3.1, criterion 12; D038): PostgreSQL itself refuses another organization's rows to the API's
/// database user, whatever SQL it runs. Raw SQL here deliberately skips every application layer (query filters,
/// the guard), so only the policies decide.
/// </summary>
public sealed class RowSecurityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_tenant_owned_table_has_row_security_and_the_tenant_policy()
    {
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        var tables = TenantTables(tenants.ConnectionString);

        await using var connection = await OpenAsOwnerAsync(tenants.ConnectionString);
        var missing = new List<string>();
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT c.relrowsecurity
                    AND EXISTS (SELECT 1 FROM pg_policies p
                        WHERE p.schemaname = 'public' AND p.tablename = c.relname AND p.policyname = @policy
                            AND p.cmd = 'ALL' AND p.roles = '{public}'
                            AND p.qual LIKE '%organization_id%app.organization_id%'
                            AND p.with_check LIKE '%organization_id%app.organization_id%')
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = @table
                """,
                connection);
            command.Parameters.AddWithValue("policy", RowSecuritySql.TenantPolicy);
            command.Parameters.AddWithValue("table", table);
            if (await command.ExecuteScalarAsync(Ct) is not true)
            {
                missing.Add(table);
            }
        }

        Assert.NotEmpty(tables);
        Assert.True(missing.Count == 0, $"Tenant-owned tables without row-level security and the {RowSecuritySql.TenantPolicy} policy " +
            $"(call EnableTenantIsolation in the migration that creates them): {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Every_client_owned_table_and_clients_have_the_restrictive_client_policy()
    {
        // SPEC-V2 3.2: a new client-owned entity is covered by its migration or this fails.
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        var tables = ClientTables(tenants.ConnectionString);

        await using var connection = await OpenAsOwnerAsync(tenants.ConnectionString);
        var missing = new List<string>();
        foreach (var (table, column) in tables)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT EXISTS (SELECT 1 FROM pg_policies p
                    WHERE p.schemaname = 'public' AND p.tablename = @table AND p.policyname = @policy
                        AND p.permissive = 'RESTRICTIVE' AND p.cmd = 'ALL' AND p.roles = '{public}'
                        AND p.qual LIKE '%' || @column || '%app.client_id%'
                        AND p.with_check LIKE '%' || @column || '%app.client_id%')
                """,
                connection);
            command.Parameters.AddWithValue("policy", RowSecuritySql.ClientPolicy);
            command.Parameters.AddWithValue("table", table);
            command.Parameters.AddWithValue("column", column);
            if (await command.ExecuteScalarAsync(Ct) is not true)
            {
                missing.Add(table);
            }
        }

        Assert.Contains(("jobs", "client_id"), tables);
        Assert.True(missing.Count == 0, $"Client-owned tables without the restrictive {RowSecuritySql.ClientPolicy} policy " +
            $"(call EnableClientIsolation in the migration that creates them): {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task With_a_client_set_each_client_owned_table_shows_and_takes_only_that_clients_rows()
    {
        // Two clients of A, each with a job, an agreement and a contact; the portal session is the second one's.
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        var mine = await tenants.CreateClientAsync(tenants.OrgA, $"Portal Client {Guid.NewGuid():N}");
        await tenants.CreateContactAsync(tenants.OrgA, mine);
        await tenants.CreateJobAsync(tenants.OrgA, clientId: mine);
        await tenants.CreateAgreementAsync(tenants.OrgA, clientId: mine);
        await tenants.CreateContactAsync(tenants.OrgA, tenants.ClientA);
        await using var portal = await OpenAsApiAsync(tenants.ConnectionString, organization: tenants.OrgA, client: mine);
        await using var staff = await OpenAsApiAsync(tenants.ConnectionString, organization: tenants.OrgA);

        foreach (var (table, column) in ClientTables(tenants.ConnectionString))
        {
            var others = await CountAsync(staff, $"SELECT count(*) FROM \"{table}\" WHERE {column} <> '{mine}'");
            Assert.True(others > 0, $"{table}: no other client's rows to hide");
            // No WHERE clause: the forgotten client filter.
            Assert.Equal(
                await CountAsync(staff, $"SELECT count(*) FROM \"{table}\" WHERE {column} = '{mine}'"),
                await CountAsync(portal, $"SELECT count(*) FROM \"{table}\""));
            Assert.True(await CountAsync(portal, $"SELECT count(*) FROM \"{table}\"") > 0, $"{table}: the client's own rows are hidden");
        }

        // Writes: another client's rows match nothing, and a row cannot be moved or added to another client.
        Assert.Equal(0, await ExecuteAsync(portal, $"UPDATE jobs SET title = title WHERE client_id = '{tenants.ClientA}'"));
        Assert.Equal(0, await ExecuteAsync(portal, $"DELETE FROM client_contacts WHERE client_id = '{tenants.ClientA}'"));
        var moved = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(portal, $"UPDATE jobs SET client_id = '{tenants.ClientA}' WHERE client_id = '{mine}'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, moved.SqlState);
    }

    [Fact]
    public async Task The_only_way_past_the_policies_is_the_reviewed_definer_function()
    {
        // A SECURITY DEFINER function runs as the owner, whom the policies do not bind: each one is a bypass and
        // needs the owner's approval, like an AcrossTenants() call site.
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        await using var connection = await OpenAsOwnerAsync(tenants.ConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.prosecdef ORDER BY 1
            """,
            connection);
        var definers = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                definers.Add(reader.GetString(0));
            }
        }

        Assert.Equal([DatabaseFunctions.UserHasTenantReferencesName], definers);
    }

    [Fact]
    public async Task Each_table_shows_the_api_only_the_entered_organizations_rows()
    {
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        await using var asA = await OpenAsApiAsync(tenants.ConnectionString, organization: tenants.OrgA);

        foreach (var table in TenantTables(tenants.ConnectionString))
        {
            var ownA = await OwnerCountAsync(tenants.ConnectionString, table, tenants.OrgA);
            Assert.True(await OwnerCountAsync(tenants.ConnectionString, table, tenants.OrgB) > 0, $"{table}: B has no rows to hide");
            // No WHERE clause at all: the forgotten filter.
            Assert.Equal(ownA, await CountAsync(asA, $"SELECT count(*) FROM \"{table}\""));
            Assert.Equal(0L, await CountAsync(asA, $"SELECT count(*) FROM \"{table}\" WHERE organization_id = '{tenants.OrgB}'"));
        }
    }

    [Fact]
    public async Task Without_a_tenant_the_api_sees_and_changes_nothing_tenant_owned()
    {
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        await using var none = await OpenAsApiAsync(tenants.ConnectionString);

        foreach (var table in TenantTables(tenants.ConnectionString))
        {
            Assert.Equal(0L, await CountAsync(none, $"SELECT count(*) FROM \"{table}\""));
        }

        Assert.Equal(0, await ExecuteAsync(none, "UPDATE clients SET notes = notes"));
        Assert.Equal(0, await ExecuteAsync(none, "DELETE FROM jobs"));
    }

    [Fact]
    public async Task Writes_into_another_organization_are_refused_or_reach_no_row()
    {
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        var clientsOfB = await OwnerCountAsync(tenants.ConnectionString, "clients", tenants.OrgB);
        var jobsOfB = await OwnerCountAsync(tenants.ConnectionString, "jobs", tenants.OrgB);
        await using var asA = await OpenAsApiAsync(tenants.ConnectionString, organization: tenants.OrgA);

        // Inserting a row for B, or moving one of A's rows to B, breaks WITH CHECK.
        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asA,
            $"""
            INSERT INTO audit_entries (organization_id, occurred_at, actor_name, action, entity_type, entity_id, summary, changes)
            VALUES ('{tenants.OrgB}', now(), 'Smuggler', 'client.updated', 'client', gen_random_uuid(), 'Smuggled', '[]')
            """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState);
        var move = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(asA,
            $"UPDATE clients SET organization_id = '{tenants.OrgB}' WHERE id = '{tenants.ClientA}'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, move.SqlState);

        // B's rows are invisible to UPDATE and DELETE, so nothing happens to them.
        Assert.Equal(0, await ExecuteAsync(asA, $"UPDATE clients SET name = 'Taken over' WHERE organization_id = '{tenants.OrgB}'"));
        Assert.Equal(0, await ExecuteAsync(asA, $"DELETE FROM jobs WHERE organization_id = '{tenants.OrgB}'"));
        Assert.Equal(clientsOfB, await OwnerCountAsync(tenants.ConnectionString, "clients", tenants.OrgB));
        Assert.Equal(jobsOfB, await OwnerCountAsync(tenants.ConnectionString, "jobs", tenants.OrgB));
        await using var owner = await OpenAsOwnerAsync(tenants.ConnectionString);
        Assert.Equal(0L, await CountAsync(owner, $"SELECT count(*) FROM clients WHERE name = 'Taken over'"));
    }

    [Fact]
    public async Task A_user_may_read_their_own_memberships_everywhere_and_nothing_else()
    {
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        await using var asMemberOfB = await OpenAsApiAsync(tenants.ConnectionString, user: tenants.MemberB);

        Assert.Equal(1L, await CountAsync(asMemberOfB, "SELECT count(*) FROM memberships"));
        Assert.Equal(1L, await CountAsync(asMemberOfB, $"SELECT count(*) FROM memberships WHERE user_id = '{tenants.MemberB}'"));
        Assert.Equal(0L, await CountAsync(asMemberOfB, "SELECT count(*) FROM clients"));
        // Reading is all it allows: the membership cannot be changed without entering its organization.
        Assert.Equal(0, await ExecuteAsync(asMemberOfB, $"UPDATE memberships SET role = 'Owner' WHERE user_id = '{tenants.MemberB}'"));
    }

    [Fact]
    public async Task Presenting_an_invitation_token_shows_that_one_invitation_only()
    {
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        byte[] tokenHash;
        await using (var owner = await OpenAsOwnerAsync(tenants.ConnectionString))
        await using (var command = new NpgsqlCommand($"SELECT token_hash FROM invitations WHERE id = '{tenants.InvitationB}'", owner))
        {
            tokenHash = (byte[])(await command.ExecuteScalarAsync(Ct))!;
        }

        await using var holder = await OpenAsApiAsync(tenants.ConnectionString, invitationTokenHash: tokenHash);

        Assert.Equal(1L, await CountAsync(holder, "SELECT count(*) FROM invitations"));
        Assert.Equal(1L, await CountAsync(holder, $"SELECT count(*) FROM invitations WHERE id = '{tenants.InvitationB}'"));
        Assert.Equal(0, await ExecuteAsync(holder, "UPDATE invitations SET revoked_at = now()"));
    }

    [Fact]
    public async Task The_definer_function_answers_yes_or_no_across_organizations()
    {
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        await using var none = await OpenAsApiAsync(tenants.ConnectionString);

        Assert.True(await ScalarAsync<bool>(none, $"SELECT {DatabaseFunctions.UserHasTenantReferencesName}('{tenants.MemberB}')"));
        Assert.False(await ScalarAsync<bool>(none, $"SELECT {DatabaseFunctions.UserHasTenantReferencesName}('{Guid.NewGuid()}')"));
    }

    [Fact]
    public async Task One_pooled_connection_never_carries_a_tenant_into_the_next_scope()
    {
        // Pool size 1: every scope below gets the same physical connection (checked by the backend process id).
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        var singleConnection = new NpgsqlConnectionStringBuilder(tenants.ConnectionString) { MaxPoolSize = 1, ApplicationName = "rls-leak-test" }.ConnectionString;
        var jobsOfA = await OwnerCountAsync(tenants.ConnectionString, "jobs", tenants.OrgA);
        var jobsOfB = await OwnerCountAsync(tenants.ConnectionString, "jobs", tenants.OrgB);
        var backends = new HashSet<int>();

        async Task<long> JobsSeenAsync(TenantContext tenant)
        {
            await using var db = CreateApiDbContext(singleConnection, tenant);
            backends.Add(await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(Ct));
            // Raw SQL: the query filter does not apply, only the policies do.
            return await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM jobs").SingleAsync(Ct);
        }

        Assert.Equal(jobsOfA, await JobsSeenAsync(Entered(tenants.OrgA)));
        Assert.Equal(jobsOfB, await JobsSeenAsync(Entered(tenants.OrgB)));
        Assert.Equal(0L, await JobsSeenAsync(new TenantContext()));
        Assert.Equal(jobsOfA, await JobsSeenAsync(Entered(tenants.OrgA)));
        Assert.Single(backends);
    }

    [Fact]
    public async Task A_scope_that_enters_its_tenant_midway_sees_it_from_then_on()
    {
        // As in tenant resolution and organization creation: the connection is already open when the tenant arrives.
        var tenants = await SharedApiTenants.GetAsync<RowSecurityTests>(postgres);
        var tenant = new TenantContext();
        await using var db = CreateApiDbContext(tenants.ConnectionString, tenant);
        await db.Database.OpenConnectionAsync(Ct);

        Assert.Equal(0L, await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM clients").SingleAsync(Ct));
        tenant.Enter(tenants.OrgA, Guid.NewGuid(), Role.Viewer);
        Assert.Equal(
            await OwnerCountAsync(tenants.ConnectionString, "clients", tenants.OrgA),
            await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM clients").SingleAsync(Ct));
    }

    private static TenantContext Entered(Guid organizationId)
    {
        var tenant = new TenantContext();
        tenant.Enter(organizationId, Guid.NewGuid(), Role.Viewer);
        return tenant;
    }

    /// <summary>The API's context: its user, the row-security interceptor and the given tenant.</summary>
    private static AppDbContext CreateApiDbContext(string connectionString, TenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseSetup.Configure(options, connectionString);
        return new AppDbContext(options.Options, tenant, rowSecurity: new RowSecurity());
    }

    /// <summary>Every client-owned table with its client column, and clients itself (keyed by id).</summary>
    private static List<(string Table, string Column)> ClientTables(string connectionString)
    {
        using var db = PostgresFixture.CreateDbContext(connectionString);
        return [.. db.Model.GetEntityTypes()
            .Where(t => typeof(IClientOwned).IsAssignableFrom(t.ClrType))
            .Select(t => (t.GetTableName()!, "client_id"))
            .Append(("clients", "id"))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)];
    }

    private static List<string> TenantTables(string connectionString)
    {
        using var db = PostgresFixture.CreateDbContext(connectionString);
        return [.. db.Model.GetEntityTypes()
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .Select(t => t.GetTableName()!)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The API's database user with the given session settings, as the interceptor would set them.
    /// Unpooled, so no test inherits another's session.</summary>
    private static async Task<NpgsqlConnection> OpenAsApiAsync(
        string connectionString, Guid? organization = null, Guid? user = null, byte[]? invitationTokenHash = null, Guid? client = null)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var set = new NpgsqlCommand(
            "SELECT set_config('app.organization_id', @org, false), set_config('app.user_id', @user, false), " +
            "set_config('app.invitation_token_hash', @token, false), set_config('app.client_id', @client, false)",
            connection);
        set.Parameters.AddWithValue("org", organization?.ToString() ?? "");
        set.Parameters.AddWithValue("user", user?.ToString() ?? "");
        set.Parameters.AddWithValue("token", invitationTokenHash is null ? "" : Convert.ToHexStringLower(invitationTokenHash));
        set.Parameters.AddWithValue("client", client?.ToString() ?? "");
        await set.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    private static async Task<NpgsqlConnection> OpenAsOwnerAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(connectionString));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<long> OwnerCountAsync(string connectionString, string table, Guid organizationId)
    {
        await using var owner = await OpenAsOwnerAsync(connectionString);
        return await CountAsync(owner, $"SELECT count(*) FROM \"{table}\" WHERE organization_id = '{organizationId}'");
    }

    // Table names come from the EF model and ids from the fixture, never from input.
    private static Task<long> CountAsync(NpgsqlConnection connection, string sql) => ScalarAsync<long>(connection, sql);

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(Ct);
    }
}
