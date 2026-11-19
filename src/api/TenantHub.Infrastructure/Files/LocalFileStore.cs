using System.Runtime.CompilerServices;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Files;

public sealed class FileStoreOptions
{
    public const string Section = "Files";

    /// <summary>The directory photos are stored under: a Compose volume in production, a temporary directory in tests.</summary>
    public string? Root { get; init; }
}

/// <summary>
/// <see cref="IFileStore"/> on a local directory (D043): <c>{root}/{organization}/{name}</c>. Every method checks the key's
/// shape first and resolves it under the root, so even a key that slipped past <see cref="FileKeys"/> cannot leave it.
/// A file is written with <see cref="FileMode.CreateNew"/>: a key is never overwritten. A crash mid-write leaves a
/// partial file without a row, which nothing serves and the sweep removes.
/// </summary>
internal sealed class LocalFileStore : IFileStore
{
    private readonly string _root;

    public LocalFileStore(FileStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Root))
        {
            throw new InvalidOperationException("Set Files__Root to the directory photos are stored in.");
        }

        _root = Path.GetFullPath(options.Root);
        Directory.CreateDirectory(_root);
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
        await file.WriteAsync(content, cancellationToken);
        await file.FlushAsync(cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        try
        {
            return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81_920, useAsync: true));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(key)); // no error when it is already gone
        return Task.CompletedTask;
    }

    public Task DeleteOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_root, organizationId.ToString("N"));
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Guid> OrganizationsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var organization))
            {
                yield return organization;
            }
        }

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredFile> FilesOfAsync(Guid organizationId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_root, organizationId.ToString("N"));
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{organizationId:N}/{file.Name}";
            if (FileKeys.IsValid(key))
            {
                yield return new StoredFile(key, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            }
        }

        await Task.CompletedTask;
    }

    private string PathOf(string key)
    {
        if (!FileKeys.IsValid(key))
        {
            throw new ArgumentException("Not a storage key.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(_root, key[..32], key[33..]));
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException("Not a storage key.", nameof(key));
    }
}
