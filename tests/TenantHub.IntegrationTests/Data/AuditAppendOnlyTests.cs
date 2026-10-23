using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantHub.Application.Tenancy;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Data;

/// <summary>D012: the database itself refuses to edit an audit entry, whoever asks; deleting stays possible.</summary>
public sealed class AuditAppendOnlyTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> AnEntryOfAAsync(ApiTenants tenants)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        return await db.AuditEntries.AcrossTenants().Where(a => a.OrganizationId == tenants.OrgA).Select(a => a.Id).FirstAsync(Ct);
    }

    [Theory]
    [InlineData("UPDATE audit_entries SET summary = 'Nothing happened' WHERE id = @id")]
    [InlineData("UPDATE audit_entries SET actor_name = 'Someone else', occurred_at = now() WHERE id = @id")]
    [InlineData("UPDATE audit_entries SET organization_id = organization_id WHERE id = @id")]
    public async Task Raw_SQL_cannot_edit_an_entry(string sql)
    {
        var tenants = await SharedApiTenants.GetAsync<AuditAppendOnlyTests>(postgres);
        var id = await AnEntryOfAAsync(tenants);
        await using var connection = new NpgsqlConnection(tenants.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, refused.SqlState);
        Assert.Contains("append-only", refused.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bulk_updates_are_refused_too_and_change_nothing()
    {
        var tenants = await SharedApiTenants.GetAsync<AuditAppendOnlyTests>(postgres);
        await using var connection = new NpgsqlConnection(tenants.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var before = new NpgsqlCommand("SELECT md5(string_agg(summary, '|' ORDER BY id)) FROM audit_entries", connection);
        var hash = await before.ExecuteScalarAsync(Ct);

        await using var update = new NpgsqlCommand("UPDATE audit_entries SET summary = upper(summary)", connection);
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct));

        Assert.Equal(hash, await before.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Entries_can_still_be_deleted_for_organization_deletion_and_the_demo_reset()
    {
        var tenants = await SharedApiTenants.GetAsync<AuditAppendOnlyTests>(postgres);
        await using var connection = new NpgsqlConnection(tenants.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var delete = new NpgsqlCommand("DELETE FROM audit_entries WHERE organization_id = @org", connection);
        delete.Parameters.AddWithValue("org", tenants.OrgB);

        var deleted = await delete.ExecuteNonQueryAsync(Ct);

        Assert.True(deleted > 0);
    }
}
