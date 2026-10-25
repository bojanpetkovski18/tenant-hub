namespace TenantHub.Api.Common;

/// <summary>
/// In Development only, reads the repository-root <c>.env</c> (the same file Docker Compose uses), so local
/// settings live in one place. Real environment variables always win. Keys use the environment-variable form
/// (<c>ConnectionStrings__Database</c>). Other environments get configuration from real environment variables.
/// </summary>
public static class DevelopmentDotEnv
{
    public static WebApplicationBuilder AddDevelopmentDotEnv(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            return builder;
        }

        var path = FindUpwards(builder.Environment.ContentRootPath, ".env");
        if (path is null)
        {
            return builder;
        }

        builder.Configuration.AddInMemoryCollection(Parse(File.ReadAllLines(path)));
        return builder;
    }

    internal static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line.StartsWith('#') || separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (Environment.GetEnvironmentVariable(key) is not null)
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
            {
                value = value[1..^1];
            }

            values[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        return values;
    }

    private static string? FindUpwards(string startDirectory, string fileName)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Stop at the repository root.
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }
}
