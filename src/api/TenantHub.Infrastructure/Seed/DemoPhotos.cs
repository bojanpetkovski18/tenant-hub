using SkiaSharp;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// The seed's "photos" (M19): plain patterns drawn by code, never real photos. Each is a coloured background with a grid
/// of rounded tiles and a few circles, chosen from the job's number, so it is the same on every reset and differs per
/// job. No text: the chiseled image has no fonts.
/// </summary>
internal static class DemoPhotos
{
    private static readonly SKColor[] Backgrounds =
    [
        new(0x2F, 0x5D, 0x8A), new(0x3E, 0x7C, 0x59), new(0x8A, 0x5A, 0x2F), new(0x6B, 0x4C, 0x8A), new(0x2F, 0x7A, 0x7A),
    ];

    public static byte[] For(int jobNumber, int width = 1600, int height = 1200)
    {
        var random = new Random(jobNumber);
        var background = Backgrounds[jobNumber % Backgrounds.Length];
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            using var tile = new SKPaint { Color = Lighter(background, 0.25f), IsAntialias = true };
            const int columns = 8;
            const int rows = 6;
            var cell = new SKSize(width / (float)columns, height / (float)rows);
            for (var x = 0; x < columns; x++)
            {
                for (var y = 0; y < rows; y++)
                {
                    if (random.Next(3) == 0)
                    {
                        continue;
                    }

                    var rect = SKRect.Create(x * cell.Width + 12, y * cell.Height + 12, cell.Width - 24, cell.Height - 24);
                    canvas.DrawRoundRect(rect, 18, 18, tile);
                }
            }

            using var dot = new SKPaint { Color = Lighter(background, 0.6f).WithAlpha(200), IsAntialias = true };
            for (var i = 0; i < 4; i++)
            {
                canvas.DrawCircle(random.Next(width), random.Next(height), 60 + random.Next(120), dot);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static SKColor Lighter(SKColor color, float amount) => new(
        (byte)(color.Red + ((255 - color.Red) * amount)),
        (byte)(color.Green + ((255 - color.Green) * amount)),
        (byte)(color.Blue + ((255 - color.Blue) * amount)));
}
