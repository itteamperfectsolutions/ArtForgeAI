using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Generates a pixel-perfect calendar grid image for a given month,
/// with a red heart marker on the special date.
/// </summary>
public static class CalendarImageGenerator
{
    private const int ImgW = 480;
    private const int ImgH = 400;
    private const int Cols = 7;

    /// <summary>
    /// Render a month calendar as a PNG byte array.
    /// White text on a dark semi-transparent background.
    /// The special date is highlighted with a red circle + heart.
    /// </summary>
    public static byte[] Generate(DateTime date)
    {
        var year = date.Year;
        var month = date.Month;
        var markedDay = date.Day;
        var monthName = date.ToString("MMMM").ToUpper();
        var firstDay = new DateTime(year, month, 1);
        int daysInMonth = DateTime.DaysInMonth(year, month);
        int startDow = (int)firstDay.DayOfWeek; // 0 = Sunday

        // Fonts — use system fonts, fall back to a common one
        FontFamily family;
        if (!SystemFonts.TryGet("Segoe UI", out family) &&
            !SystemFonts.TryGet("Arial", out family) &&
            !SystemFonts.TryGet("DejaVu Sans", out family))
        {
            family = SystemFonts.Families.First();
        }

        var headerFont = family.CreateFont(22, FontStyle.Bold);
        var dowFont = family.CreateFont(13, FontStyle.Bold);
        var dayFont = family.CreateFont(16, FontStyle.Regular);
        var dayBoldFont = family.CreateFont(16, FontStyle.Bold);
        var heartFont = family.CreateFont(12, FontStyle.Regular);

        var white = Color.White;
        var grey = Color.FromRgba(200, 200, 200, 255);
        var red = Color.FromRgba(220, 40, 40, 255);
        var bg = Color.FromRgba(30, 30, 40, 220);

        using var img = new Image<Rgba32>(ImgW, ImgH, Color.Transparent);

        img.Mutate(ctx =>
        {
            // Dark background with rounded feel
            ctx.Fill(bg, new RectangularPolygon(0, 0, ImgW, ImgH));

            // ── Month + Year header ──
            var headerText = $"{monthName} {year}";
            var headerOpts = new RichTextOptions(headerFont)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Origin = new PointF(ImgW / 2f, 18)
            };
            ctx.DrawText(headerOpts, headerText, white);

            // ── Day-of-week headers ──
            string[] dows = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
            float cellW = ImgW / (float)Cols;
            float dowY = 58;
            for (int i = 0; i < 7; i++)
            {
                var opts = new RichTextOptions(dowFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Origin = new PointF(cellW * i + cellW / 2f, dowY)
                };
                ctx.DrawText(opts, dows[i], grey);
            }

            // ── Day number grid ──
            float gridTop = 88;
            float rowH = 46;
            int dayNum = 1;

            for (int week = 0; dayNum <= daysInMonth; week++)
            {
                for (int dow = 0; dow < 7; dow++)
                {
                    if ((week == 0 && dow < startDow) || dayNum > daysInMonth)
                        continue;

                    float cx = cellW * dow + cellW / 2f;
                    float cy = gridTop + week * rowH;

                    if (dayNum == markedDay)
                    {
                        // Red circle background
                        var ellipse = new EllipsePolygon(cx + 0.5f, cy + 10f, 18);
                        ctx.Fill(red, ellipse);

                        // Day number in white bold
                        var dOpts = new RichTextOptions(dayBoldFont)
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Origin = new PointF(cx, cy)
                        };
                        ctx.DrawText(dOpts, dayNum.ToString(), white);
                    }
                    else
                    {
                        var dOpts = new RichTextOptions(dayFont)
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Origin = new PointF(cx, cy)
                        };
                        ctx.DrawText(dOpts, dayNum.ToString(), white);
                    }

                    dayNum++;
                }
            }
        });

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Generate and save a calendar image to disk, returning the web-relative path.
    /// </summary>
    public static async Task<string> GenerateAndSaveAsync(DateTime date, string webRootPath)
    {
        var bytes = Generate(date);
        var dir = System.IO.Path.Combine(webRootPath, "generated");
        Directory.CreateDirectory(dir);
        var fileName = $"calendar_{date:yyyyMMdd}_{Guid.NewGuid():N}.png";
        var fullPath = System.IO.Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(fullPath, bytes);
        return $"generated/{fileName}";
    }
}
