using System.Collections.Concurrent;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// One <see cref="ApiTenants"/> per test class, built on first use. Signing up a dozen users per theory row would
/// dominate the run; the matrix tests only read, and each checks that B is unchanged anyway.
/// </summary>
public static class SharedApiTenants
{
    private static readonly ConcurrentDictionary<Type, Lazy<Task<ApiTenants>>> ByTestClass = new();

    public static Task<ApiTenants> GetAsync<TTestClass>(PostgresFixture postgres) =>
        ByTestClass.GetOrAdd(typeof(TTestClass), _ => new Lazy<Task<ApiTenants>>(() => ApiTenants.CreateAsync(postgres))).Value;
}
