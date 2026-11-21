using TenantHub.Domain;

namespace TenantHub.Application.Abstractions;

/// <summary>
/// Turns an upload into what is stored (SPEC-V2 5.6, D043): accepted by content, its size checked before the pixels are
/// decoded, re-encoded without any metadata at most <see cref="PhotoRules.MaxEdge"/> pixels on the long edge, plus a
/// thumbnail. Throws <see cref="PhotoRejectedException"/> for anything it will not store.
/// </summary>
public interface IPhotoProcessor
{
    ProcessedPhoto Process(ReadOnlyMemory<byte> upload);
}

/// <summary>The re-encoded photo and its thumbnail, in the format the upload had.</summary>
public sealed record ProcessedPhoto(PhotoFormat Format, byte[] Image, byte[] Thumbnail, int Width, int Height)
{
    public long Bytes => Image.LongLength + Thumbnail.LongLength;
}

public enum PhotoFormat
{
    Jpeg,
    Png,
    Webp,
}

/// <summary>The upload rules as numbers (SPEC-V2 5.6), and recognizing a format by its first bytes.</summary>
public static class PhotoRules
{
    public const int MaxUploadBytes = 10 * 1024 * 1024;

    public const int MaxEdge = 2560;

    public const int ThumbnailEdge = 320;

    /// <summary>The longest side an upload may have before it is decoded.</summary>
    public const int MaxSide = 12_000;

    /// <summary>
    /// The most pixels a JPEG may have: it decodes at a reduced scale, so 50 megapixels (a 48-megapixel phone camera)
    /// costs about 50 MB of memory. PNG and WebP decode at full size, so they get half (about 100 MB).
    /// </summary>
    public const long MaxJpegPixels = 50_000_000;

    public const long MaxOtherPixels = 25_000_000;

    public static string ContentType(PhotoFormat format) => format switch
    {
        PhotoFormat.Jpeg => "image/jpeg",
        PhotoFormat.Png => "image/png",
        _ => "image/webp",
    };

    public static string Extension(PhotoFormat format) => format switch
    {
        PhotoFormat.Jpeg => "jpg",
        PhotoFormat.Png => "png",
        _ => "webp",
    };

    /// <summary>JPEG, PNG or WebP by their signatures; null for anything else, whatever its name or declared type.</summary>
    public static PhotoFormat? Detect(ReadOnlySpan<byte> start)
    {
        if (start is [0xFF, 0xD8, 0xFF, ..])
        {
            return PhotoFormat.Jpeg;
        }

        if (start.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return PhotoFormat.Png;
        }

        // RIFF, four bytes of length, WEBP.
        if (start.Length >= 12 && start[..4].SequenceEqual("RIFF"u8) && start[8..12].SequenceEqual("WEBP"u8))
        {
            return PhotoFormat.Webp;
        }

        return null;
    }
}

/// <summary>An upload that is not stored: 400, or 413 when it is too large, with a stable code.</summary>
public sealed class PhotoRejectedException(string code, string message, DomainErrorKind kind = DomainErrorKind.Validation)
    : DomainException(code, message, kind)
{
    public static PhotoRejectedException NotAnImage() =>
        new("photo-not-accepted", "Only JPEG, PNG or WebP photos can be added.");

    public static PhotoRejectedException Unreadable() =>
        new("photo-not-accepted", "This photo could not be read. Try another one, or take it again.");

    public static PhotoRejectedException TooLarge() =>
        new("photo-too-large", "A photo can be at most 10 MB.", DomainErrorKind.TooLarge);

    public static PhotoRejectedException TooManyPixels() =>
        new("photo-too-large", $"This photo has too many pixels: at most {PhotoRules.MaxSide:N0} on a side, and 50 megapixels for a JPEG or 25 for a PNG or WebP.", DomainErrorKind.TooLarge);
}
