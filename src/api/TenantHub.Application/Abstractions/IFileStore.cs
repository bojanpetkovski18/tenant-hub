using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TenantHub.Application.Abstractions;

/// <summary>
/// Where photo files live (D043): outside the database, under keys this application makes. A key is the organization's
/// id and a random name (<see cref="FileKeys"/>); nothing a user sends becomes part of one, so no request can name a path.
/// Implementations refuse any key that is not of that form.
/// </summary>
public interface IFileStore
{
    /// <summary>Writes the file whole; a key is written once (a second write of the same key is a bug).</summary>
    Task WriteAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>The file for reading, or null when there is none.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes the file; a file already gone is fine (clean-up runs again after a crash).</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes every file of the organization (it was deleted).</summary>
    Task DeleteOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>The organizations that have files, for the orphan sweep.</summary>
    IAsyncEnumerable<Guid> OrganizationsAsync(CancellationToken cancellationToken);

    /// <summary>An organization's files and when each was written, for the orphan sweep.</summary>
    IAsyncEnumerable<StoredFile> FilesOfAsync(Guid organizationId, CancellationToken cancellationToken);
}

public sealed record StoredFile(string Key, DateTimeOffset WrittenAt);

/// <summary>
/// Storage keys: <c>{organization id, 32 hex}/{128 random bits, 32 hex}.{jpg|png|webp}</c>. The organization prefix
/// lets a deleted organization's files go in one step and the sweep work one organization at a time.
/// </summary>
public static partial class FileKeys
{
    public static string New(Guid organizationId, string extension)
    {
        if (!Extensions.Contains(extension))
        {
            throw new ArgumentOutOfRangeException(nameof(extension), extension, "Not a stored photo type.");
        }

        return $"{organizationId:N}/{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.{extension}";
    }

    public static bool IsValid(string key) => Shape().IsMatch(key);

    public static Guid OrganizationOf(string key) =>
        IsValid(key) ? Guid.ParseExact(key[..32], "N") : throw new ArgumentException("Not a storage key.", nameof(key));

    private static readonly HashSet<string> Extensions = ["jpg", "png", "webp"];

    [GeneratedRegex("^[0-9a-f]{32}/[0-9a-f]{32}\\.(jpg|png|webp)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();
}
