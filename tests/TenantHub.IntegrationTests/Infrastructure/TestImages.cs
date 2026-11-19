using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// Images for the upload tests, drawn by code (plain patterns, never real photos), and the hostile files criterion 17
/// names: a JPEG carrying EXIF with GPS, a decompression bomb, a polyglot, an SVG and a script.
/// </summary>
public static class TestImages
{
    /// <summary>Upper-left quarter red, the rest blue: enough to see that orientation was applied.</summary>
    public static byte[] Jpeg(int width = 64, int height = 48) => Draw(width, height, SKEncodedImageFormat.Jpeg);

    public static byte[] Png(int width = 64, int height = 48) => Draw(width, height, SKEncodedImageFormat.Png);

    public static byte[] Webp(int width = 64, int height = 48) => Draw(width, height, SKEncodedImageFormat.Webp);

    /// <summary>
    /// <paramref name="jpeg"/> with an EXIF block right after its start marker: an orientation (6: the camera was turned
    /// a quarter clockwise) and a GPS position, as phones write them.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, ushort orientation = 6)
    {
        // TIFF, little-endian: IFD0 (orientation, pointer to the GPS IFD), then the GPS IFD (latitude 52°22'N).
        var tiff = new List<byte>();
        void U16(int v) => tiff.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(uint v) => tiff.AddRange(BitConverter.GetBytes(v));
        tiff.AddRange("II"u8.ToArray());
        U16(42);
        U32(8);
        U16(2);                         // IFD0: two entries
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U16(0x8825); U16(4); U32(1); U32(38); // GPS IFD at offset 38
        U32(0);                         // no next IFD (offset 34..37)
        U16(2);                         // GPS IFD: two entries
        U16(0x0001); U16(2); U32(2); tiff.AddRange("N\0\0\0"u8.ToArray());
        U16(0x0002); U16(5); U32(3); U32(38 + 2 + 24 + 4);
        U32(0);
        foreach (var (numerator, denominator) in new[] { (52u, 1u), (22u, 1u), (1234u, 100u) })
        {
            U32(numerator);
            U32(denominator);
        }

        var app1 = new List<byte> { 0xFF, 0xE1 };
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        app1.Add((byte)((payload.Length + 2) >> 8));
        app1.Add((byte)((payload.Length + 2) & 0xFF));
        app1.AddRange(payload);
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    /// <summary>A real PNG of 20,000 × 20,000 one-bit pixels: about 50 KB compressed, 1.6 GB once decoded to colour.</summary>
    public static byte[] DecompressionBomb(int side = 20_000)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + ((side + 7) / 8)]; // filter byte, then the bits
            for (var y = 0; y < side; y++)
            {
                zlib.Write(row);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, side);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), side);
        header[8] = 1; // bit depth; colour type 0 (grey), compression, filter and interlace 0
        return
        [
            0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", header),
            .. Chunk("IDAT", raw.ToArray()),
            .. Chunk("IEND", []),
        ];
    }

    public static readonly byte[] Svg = Encoding.UTF8.GetBytes(
        """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><script>alert(document.cookie)</script></svg>""");

    public static readonly byte[] Script = Encoding.UTF8.GetBytes("#!/bin/sh\necho 'not a photo'\n");

    /// <summary>An HTML page with a JPEG appended: a browser sniffing it would see HTML first.</summary>
    public static byte[] HtmlThenJpeg() => [.. Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>\n"), .. Jpeg()];

    /// <summary>A valid JPEG with a script appended after its end marker: decoders read the image and ignore the rest.</summary>
    public static byte[] JpegThenScript() => [.. Jpeg(), .. Encoding.UTF8.GetBytes("<script>alert(1)</script>")];

    /// <summary>Whether the bytes hold an EXIF block (APP1 "Exif") or XMP anywhere.</summary>
    public static bool HasMetadata(byte[] image) =>
        IndexOf(image, "Exif\0\0"u8) >= 0 || IndexOf(image, "http://ns.adobe.com/xap"u8) >= 0;

    public static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

    public static (int Width, int Height, SKEncodedImageFormat Format) Inspect(byte[] image)
    {
        using var codec = SKCodec.Create(new MemoryStream(image)) ?? throw new InvalidOperationException("Not an image.");
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }

    /// <summary>The colour of one pixel, after decoding.</summary>
    public static SKColor Pixel(byte[] image, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(image);
        return bitmap.GetPixel(x, y);
    }

    private static byte[] Draw(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, red);
        }

        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
