using System.Text.RegularExpressions;

namespace TenantHub.ArchitectureTests;

/// <summary>
/// SPEC 3.3, point 5: the tenant query filter is bypassed only through <c>AcrossTenants()</c>, only in the files
/// below, each with its reason. <c>IgnoreQueryFilters</c> appears only inside <c>AcrossTenants</c> itself. Source
/// is scanned rather than IL: a call site is a line a reviewer can read, and the scan needs no extra library.
/// Adding an entry needs the owner's approval (CLAUDE.md).
/// </summary>
public sealed partial class TenantBypassAllowListTests
{
    private static readonly Dictionary<string, string> AllowedBypasses = new(StringComparer.Ordinal)
    {
        ["src/api/TenantHub.Application/Accounts/GetSessionQuery.cs"] = "the session lists the user's memberships in every organization",
        ["src/api/TenantHub.Application/Organizations/CreateOrganizationHandler.cs"] = "the five-owned limit counts the user's organizations everywhere",
        ["src/api/TenantHub.Application/Tenancy/ResolveTenantQuery.cs"] = "tenant resolution reads the caller's membership before a tenant is entered",
    };

    private const string Definition = "src/api/TenantHub.Application/Tenancy/TenantQueries.cs";

    [Fact]
    public void IgnoreQueryFilters_is_called_only_by_the_named_bypass()
    {
        var offenders = SourceFiles().Where(f => f.Path != Definition && IgnoreQueryFilters().IsMatch(f.Text)).Select(f => f.Path).ToList();

        Assert.True(offenders.Count == 0, $"Use AcrossTenants() from an allow-listed file instead: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void AcrossTenants_is_used_only_in_allow_listed_files()
    {
        var users = SourceFiles().Where(f => f.Path != Definition && AcrossTenants().IsMatch(f.Text)).Select(f => f.Path).ToList();

        var unexpected = users.Except(AllowedBypasses.Keys).ToList();
        Assert.True(unexpected.Count == 0, $"Tenant bypass outside the allow-list: {string.Join(", ", unexpected)}");
    }

    [Fact]
    public void Every_allow_list_entry_still_uses_the_bypass()
    {
        // A stale entry would silently pre-approve a future bypass in that file.
        var users = SourceFiles().Where(f => AcrossTenants().IsMatch(f.Text)).Select(f => f.Path).ToHashSet();

        Assert.DoesNotContain(AllowedBypasses.Keys, path => !users.Contains(path));
    }

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        var root = RepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "src", "api"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TenantHub.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("TenantHub.slnx not found above the test output directory.");
    }

    [GeneratedRegex(@"\.IgnoreQueryFilters\s*\(")]
    private static partial Regex IgnoreQueryFilters();

    [GeneratedRegex(@"\.AcrossTenants\s*\(")]
    private static partial Regex AcrossTenants();
}
