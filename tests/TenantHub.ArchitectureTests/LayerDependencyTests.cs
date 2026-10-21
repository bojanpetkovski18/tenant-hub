using System.Reflection;
using System.Xml.Linq;

namespace TenantHub.ArchitectureTests;

/// <summary>
/// Enforces the dependency direction from D003: Domain ← Application ← Infrastructure ← Api.
/// Each rule is checked twice: against what the project file declares (catches a reference before any type is
/// used) and against what the compiled assembly references (catches dependencies that arrive another way,
/// such as a framework reference).
/// </summary>
public sealed class LayerDependencyTests
{
    public static TheoryData<string, string[]> Rules => new()
    {
        { "TenantHub.Domain", ["TenantHub.", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql"] },
        // Application may use the EF Core abstractions (D003), but no HTTP types and no database provider.
        { "TenantHub.Application", ["TenantHub.Infrastructure", "TenantHub.Api", "Microsoft.AspNetCore", "Npgsql"] },
        { "TenantHub.Infrastructure", ["TenantHub.Api"] },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Project_file_declares_no_forbidden_reference(string project, string[] forbiddenPrefixes)
    {
        var declared = DeclaredReferences(project);

        var violations = declared.Where(reference => IsForbidden(project, reference, forbiddenPrefixes)).ToList();

        Assert.True(violations.Count == 0, $"{project}.csproj must not reference: {string.Join(", ", violations)}");
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void Compiled_assembly_has_no_forbidden_reference(string project, string[] forbiddenPrefixes)
    {
        var referenced = Assembly.Load(project).GetReferencedAssemblies().Select(name => name.Name ?? "");

        var violations = referenced.Where(reference => IsForbidden(project, reference, forbiddenPrefixes)).ToList();

        Assert.True(violations.Count == 0, $"{project}.dll must not reference: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Every_source_project_is_covered_by_a_rule_or_is_the_composition_root()
    {
        var projects = Directory.GetDirectories(Path.Combine(RepositoryRoot(), "src", "api"))
            .Select(directory => Path.GetFileName(directory) ?? "")
            .Order()
            .ToList();
        var ruled = Rules.Select(row => (string)row.Data.Item1).Append("TenantHub.Api").Order().ToList();

        Assert.Equal(ruled, projects);
    }

    private static bool IsForbidden(string project, string reference, string[] forbiddenPrefixes) =>
        reference != project && forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal));

    private static List<string> DeclaredReferences(string project)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "api", project, $"{project}.csproj");
        var document = XDocument.Load(path);

        return document.Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "FrameworkReference")
            .Select(element => (string?)element.Attribute("Include") ?? "")
            .Select(include => include.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(include.Replace('\\', '/'))
                : include)
            .ToList();
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
}
