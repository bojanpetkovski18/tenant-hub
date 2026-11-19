using SkiaSharp;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Files;

/// <summary>
/// Re-encodes uploads with SkiaSharp (D043). Order matters for untrusted input:
/// <list type="number">
/// <item>the first bytes must be a JPEG, PNG or WebP signature, and the decoder must agree;</item>
/// <item>the size in pixels is read from the header and checked before any pixel is decoded (decompression bombs);</item>
/// <item>a JPEG decodes at a reduced scale when it is large; the camera's orientation is applied;</item>
/// <item>the pixels are encoded again, in sRGB with no colour profile, EXIF, XMP or text: nothing of the original
/// file but its pixels reaches the store, so location data and anything appended to the file are gone.</item>
/// </list>
/// At most two uploads decode at once, which bounds the memory a burst of uploads can take.
/// </summary>
internal sealed class SkiaPhotoProcessor : IPhotoProcessor
{
    private const int JpegQuality = 85;
    private const int ThumbnailQuality = 80;
    private static readonly SemaphoreSlim Decoding = new(2, 2);

    public ProcessedPhoto Process(ReadOnlyMemory<byte> upload)
    {
        var format = PhotoRules.Detect(upload.Span) ?? throw PhotoRejectedException.NotAnImage();
        Decoding.Wait();
        try
        {
            using var data = SKData.CreateCopy(upload.Span);
            using var codec = SKCodec.Create(data) ?? throw PhotoRejectedException.Unreadable();
            if (codec.EncodedFormat != Expected(format))
            {
                throw PhotoRejectedException.NotAnImage();
            }

            var (width, height) = (codec.Info.Width, codec.Info.Height);
            var maxPixels = format == PhotoFormat.Jpeg ? PhotoRules.MaxJpegPixels : PhotoRules.MaxOtherPixels;
            if (width <= 0 || height <= 0 || width > PhotoRules.MaxSide || height > PhotoRules.MaxSide || (long)width * height > maxPixels)
            {
                throw PhotoRejectedException.TooManyPixels();
            }

            using var decoded = Decode(codec, format);
            using var oriented = Orient(decoded, codec.EncodedOrigin);
            using var image = Fit(oriented, PhotoRules.MaxEdge);
            using var thumbnail = Fit(image, PhotoRules.ThumbnailEdge);
            return new ProcessedPhoto(
                format,
                Encode(image, format, JpegQuality),
                Encode(thumbnail, format, ThumbnailQuality),
                image.Width,
                image.Height);
        }
        finally
        {
            Decoding.Release();
        }
    }

    private static SKEncodedImageFormat Expected(PhotoFormat format) => format switch
    {
        PhotoFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        PhotoFormat.Png => SKEncodedImageFormat.Png,
        _ => SKEncodedImageFormat.Webp,
    };

    /// <summary>
    /// Decodes the first frame into sRGB. A large JPEG decodes at the smallest supported scale that still covers the
    /// stored size; PNG and WebP decode whole (their pixel limit is lower for that reason). A truncated or damaged file is
    /// refused rather than stored half grey.
    /// </summary>
    private static SKBitmap Decode(SKCodec codec, PhotoFormat format)
    {
        var size = codec.Info.Size;
        var longEdge = Math.Max(size.Width, size.Height);
        if (format == PhotoFormat.Jpeg && longEdge > PhotoRules.MaxEdge)
        {
            // The decoder scales by eighths and rounds down: take the first eighth that still covers the stored size, so the
            // final resize only ever shrinks (a straight 2560/4000 request decodes at 5/8, which is 2,500 pixels).
            size = Enumerable.Range(1, 8)
                .Select(eighths => codec.GetScaledDimensions(eighths / 8f))
                .First(scaled => Math.Max(scaled.Width, scaled.Height) >= PhotoRules.MaxEdge);
        }

        var alpha = format == PhotoFormat.Jpeg ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, alpha, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(info);
        try
        {
            if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
            {
                throw PhotoRejectedException.Unreadable();
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies the EXIF orientation to the pixels: the tag itself is not kept, so without this a phone photo taken upright
    /// would be stored on its side. Each case maps a source pixel (x, y) to where it belongs.
    /// </summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null,
        };
        if (matrix is not { } transform)
        {
            return source.Copy();
        }

        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var target = new SKBitmap(turned ? source.Info.WithSize(source.Height, source.Width) : source.Info);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(transform);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        canvas.Flush();
        return target;
    }

    /// <summary>A copy at most <paramref name="edge"/> pixels on the long side, scaled with mipmaps (good for large reductions).</summary>
    private static SKBitmap Fit(SKBitmap source, int edge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= edge)
        {
            return source.Copy();
        }

        var scale = (double)edge / longEdge;
        var size = new SKSizeI(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        return source.Resize(source.Info.WithSize(size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw PhotoRejectedException.Unreadable();
    }

    /// <summary>The pixels as a new file. Without a colour space on the pixmap, no ICC profile is written.</summary>
    private static byte[] Encode(SKBitmap bitmap, PhotoFormat format, int quality)
    {
        using var pixels = bitmap.PeekPixels();
        using var plain = pixels.WithColorSpace(null!);
        using var encoded = format switch
        {
            PhotoFormat.Jpeg => plain.Encode(new SKJpegEncoderOptions(quality)),
            PhotoFormat.Png => plain.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)),
            _ => plain.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality)),
        };
        return encoded?.ToArray() ?? throw PhotoRejectedException.Unreadable();
    }
}
