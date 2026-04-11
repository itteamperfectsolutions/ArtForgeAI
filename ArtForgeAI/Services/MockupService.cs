using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using ArtForgeAI.Models;
using Svg.Skia;
using SkiaSharp;

namespace ArtForgeAI.Services;

public class MockupService
{
    private readonly IImageGenerationService _imageGen;
    private readonly IImageStorageService _imageStorage;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MockupService> _logger;

    public MockupService(
        IImageGenerationService imageGen,
        IImageStorageService imageStorage,
        IWebHostEnvironment env,
        ILogger<MockupService> logger)
    {
        _imageGen = imageGen;
        _imageStorage = imageStorage;
        _env = env;
        _logger = logger;
    }

    /// <summary>Product template metadata for all 20 products.</summary>
    public static readonly MockupProduct[] AllProducts =
    [
        // Drinkware
        new("Standard Mug", "mug", "Drinkware", ["front"], ["#FFFFFF","#222222","#C0C0C0"], ["Ceramic"]),
        new("Travel Mug", "travel-mug", "Drinkware", ["front"], ["#FFFFFF","#222222","#C0C0C0"], ["Stainless Steel"]),
        new("Water Bottle", "water-bottle", "Drinkware", ["front"], ["#FFFFFF","#222222","#C0C0C0"], ["Stainless Steel"]),

        // Apparel
        new("T-Shirt", "tshirt", "Apparel", ["front","back","pocket"], ["#FFFFFF","#222222","#1B3A5C","#C0392B","#808080","#2E7D32","#1565C0","#F9A825"], ["Cotton","Polyester Blend"]),
        new("Hoodie", "hoodie", "Apparel", ["front","back","left-chest"], ["#FFFFFF","#222222","#1B3A5C","#C0392B","#808080","#2E7D32","#1565C0","#F9A825"], ["Cotton","Polyester Blend"]),
        new("Cap", "cap", "Apparel", ["front","side","back"], ["#FFFFFF","#222222","#1B3A5C","#C0392B","#808080","#2E7D32","#1565C0","#F9A825"], ["Cotton","Polyester Blend"]),
        new("Polo Shirt", "polo", "Apparel", ["left-chest","back"], ["#FFFFFF","#222222","#1B3A5C","#C0392B","#808080","#2E7D32","#1565C0","#F9A825"], ["Cotton","Polyester Blend"]),
        new("Tote Bag", "tote-bag", "Apparel", ["front","back"], ["#FFFFFF","#222222","#1B3A5C","#C0392B","#808080","#2E7D32","#1565C0","#F9A825"], ["Cotton","Polyester Blend"]),

        // Stationery
        new("Notebook", "notebook", "Stationery", ["front"], ["#FFFFFF","#222222","#8D6E63"], ["Matte","Glossy"]),
        new("Mouse Pad", "mousepad", "Stationery", ["front"], ["#FFFFFF","#222222","#8D6E63"], ["Matte","Glossy"]),
        new("Pen", "pen", "Stationery", ["front"], ["#FFFFFF","#222222","#8D6E63"], ["Matte","Glossy"]),

        // Home
        new("Throw Pillow", "pillow", "Home", ["front"], ["#FFFFFF","#222222","#808080","#D7CCC8"], ["Canvas","Fabric"]),
        new("Phone Case", "phone-case", "Home", ["front"], ["#FFFFFF","#222222","#808080","#D7CCC8"], ["Plastic"]),
        new("Wall Clock", "wall-clock", "Home", ["front"], ["#FFFFFF","#222222","#808080","#D7CCC8"], ["Plastic"]),
        new("Coaster", "coaster", "Home", ["front"], ["#FFFFFF","#222222","#808080","#D7CCC8"], ["Canvas","Fabric"]),
        new("Canvas Print", "canvas-print", "Home", ["front"], ["#FFFFFF","#222222","#808080","#D7CCC8"], ["Canvas"]),

        // Promotional
        new("Business Card", "business-card", "Promotional", ["front","back"], ["#FFFFFF","#FFF8E7"], ["Matte","Glossy","Linen"]),
        new("Letterhead", "letterhead", "Promotional", ["front"], ["#FFFFFF","#FFF8E7"], ["Matte","Glossy","Linen"]),
        new("ID Badge", "id-badge", "Promotional", ["front"], ["#FFFFFF","#FFF8E7"], ["Matte","Glossy","Linen"]),
        new("Banner", "banner", "Promotional", ["front"], ["#FFFFFF","#FFF8E7"], ["Matte","Glossy","Linen"]),
    ];

    /// <summary>Products available per subscription tier.</summary>
    public static readonly Dictionary<string, string[]> TierProducts = new()
    {
        ["Free"] = ["mug", "tshirt", "business-card"],
        ["Starter"] = ["mug", "tshirt", "business-card", "cap", "tote-bag", "phone-case", "notebook", "canvas-print", "water-bottle", "mousepad"],
        ["Pro"] = AllProducts.Select(p => p.Slug).ToArray(),
        ["Enterprise"] = AllProducts.Select(p => p.Slug).ToArray(),
    };

    public static bool IsProductAvailable(string planName, string productSlug)
    {
        if (TierProducts.TryGetValue(planName, out var slugs))
            return slugs.Contains(productSlug);
        return false;
    }

    public static string[] GetAvailableProductSlugs(string planName)
    {
        return TierProducts.TryGetValue(planName, out var slugs) ? slugs : [];
    }

    public async Task<byte[]> CompositeForDownload(
        byte[] logoBytes,
        string productSlug,
        string zone,
        string productColor,
        float logoX, float logoY,
        float logoScale,
        float logoRotation,
        float logoOpacity,
        string? overlayText,
        string? textFont,
        string? textColor,
        bool shadowEnabled,
        bool outlineEnabled,
        string? outlineColor,
        int outputWidth = 1200,
        int outputHeight = 1200)
    {
        // Render the product SVG template as the background
        using var product = RenderProductSvg(productSlug, productColor, outputWidth, outputHeight);

        using var logo = Image.Load<Rgba32>(logoBytes);

        // In the browser preview, the logo <img> renders at its natural size inside
        // a ~500px container, then CSS transform:scale() shrinks it.  The visual ratio
        // depends on the container width, not the image's pixel dimensions.
        // To match, we scale the logo proportional to the output canvas width.
        // Browser container is approximately 500 CSS pixels wide;
        // logoScale of 1.0 should make the logo fill ~100% of the canvas.
        const float BrowserContainerPx = 500f;
        float effectiveScale = logoScale * (outputWidth / BrowserContainerPx);
        int logoW = Math.Max((int)(logo.Width * effectiveScale), 1);
        int logoH = Math.Max((int)(logo.Height * effectiveScale), 1);

        logo.Mutate(ctx =>
        {
            ctx.Resize(logoW, logoH);
            if (Math.Abs(logoRotation) > 0.1f)
                ctx.Rotate(logoRotation);
        });

        if (logoOpacity < 1.0f)
        {
            logo.Mutate(ctx => ctx.Opacity(logoOpacity));
        }

        int posX = (int)(logoX * outputWidth) - (logo.Width / 2);
        int posY = (int)(logoY * outputHeight) - (logo.Height / 2);

        product.Mutate(ctx => ctx.DrawImage(logo, new Point(posX, posY), 1f));

        product.Metadata.HorizontalResolution = 300;
        product.Metadata.VerticalResolution = 300;
        product.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;

        using var ms = new MemoryStream();
        await product.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Renders the SVG product template with the selected color applied,
    /// centered on a dark background matching the preview panel.
    /// </summary>
    private Image<Rgba32> RenderProductSvg(string productSlug, string productColor, int width, int height)
    {
        var svgPath = Path.Combine(_env.WebRootPath, "mockup-templates", $"{productSlug}.svg");

        if (!File.Exists(svgPath))
        {
            // Fallback: plain colored background if SVG not found
            var fallback = new Image<Rgba32>(width, height);
            Color bgColor;
            if (!Color.TryParseHex(productColor, out bgColor))
                bgColor = Color.White;
            fallback.Mutate(ctx => ctx.Fill(bgColor));
            return fallback;
        }

        // Read and apply color replacement (same logic as GetColoredSvg in the Razor page)
        var svgContent = File.ReadAllText(svgPath)
            .Replace("fill=\"#f0f0f0\"", $"fill=\"{productColor}\"")
            .Replace("fill=\"#e8e8e8\"", $"fill=\"{AdjustColor(productColor, -0.05)}\"")
            .Replace("fill=\"#e0e0e0\"", $"fill=\"{AdjustColor(productColor, -0.1)}\"");

        // Render SVG to SKBitmap
        using var svg = new SKSvg();
        using var svgStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgContent));
        svg.Load(svgStream);

        var picture = svg.Picture;
        if (picture == null)
        {
            var fallback = new Image<Rgba32>(width, height);
            fallback.Mutate(ctx => ctx.Fill(Color.White));
            return fallback;
        }

        // Calculate scaling to fit SVG within the output dimensions
        // matching CSS inset:5% = 90% of area
        var svgW = picture.CullRect.Width;
        var svgH = picture.CullRect.Height;
        var scale = Math.Min(width * 0.90f / svgW, height * 0.90f / svgH);

        var scaledW = (int)(svgW * scale);
        var scaledH = (int)(svgH * scale);

        // Render SVG to a bitmap
        using var bitmap = new SKBitmap(scaledW, scaledH);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        canvas.Flush();

        // Convert SKBitmap to ImageSharp Image
        using var skImage = SKImage.FromBitmap(bitmap);
        using var skData = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using var skStream = skData.AsStream();
        using var svgRendered = Image.Load<Rgba32>(skStream);

        // Create dark background (matching the preview panel) and center the SVG
        var result = new Image<Rgba32>(width, height);
        result.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("#1a1a2e"));
            var offsetX = (width - scaledW) / 2;
            var offsetY = (height - scaledH) / 2;
            ctx.DrawImage(svgRendered, new Point(offsetX, offsetY), 1f);
        });

        return result;
    }

    private static string AdjustColor(string hex, double factor)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return "#" + hex;
        int r = Convert.ToInt32(hex[..2], 16);
        int g = Convert.ToInt32(hex[2..4], 16);
        int b = Convert.ToInt32(hex[4..6], 16);
        if (factor < 0)
        {
            r = (int)(r * (1 + factor));
            g = (int)(g * (1 + factor));
            b = (int)(b * (1 + factor));
        }
        else
        {
            r = r + (int)((255 - r) * factor);
            g = g + (int)((255 - g) * factor);
            b = b + (int)((255 - b) * factor);
        }
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public async Task<GenerationResult> GenerateAiMockup(
        byte[] logoBytes,
        string productSlug,
        string productName,
        string productColor,
        string material,
        string zone,
        float logoX, float logoY,
        float logoScale,
        float logoRotation,
        float logoOpacity,
        string? overlayText,
        bool shadowEnabled,
        bool outlineEnabled,
        string userId)
    {
        // Render the composite mockup (product + logo) as a reference image for Gemini
        var compositeBytes = await CompositeForDownload(
            logoBytes, productSlug, zone, productColor,
            logoX, logoY, logoScale, logoRotation, logoOpacity,
            overlayText, null, null, shadowEnabled, outlineEnabled, null);

        // Save composite under wwwroot so SafeResolvePath allows access
        var tempFileName = $"mockup_ref_{Guid.NewGuid():N}.png";
        var tempRelPath = await _imageStorage.SaveImageFromBytesAsync(
            BinaryData.FromBytes(compositeBytes), tempFileName);

        try
        {
            var colorName = GetColorName(productColor);
            var prompt = $"Transform this flat mockup into a photorealistic product photograph. " +
                         $"The product is a {colorName} {productName} made of {material}. " +
                         $"Keep the exact same logo/design placement and appearance from the reference image. " +
                         $"Add realistic 3D shape, {material} texture, shadows, reflections, and studio lighting. " +
                         $"Clean white background, professional product photography style. High quality, sharp details.";

            var request = new GenerationRequest
            {
                Prompt = prompt,
                ReferenceImagePaths = [tempRelPath],
                Provider = ImageProvider.Gemini,
                EnhancePrompt = false,
                Width = 1536,
                Height = 1024,
                UserId = userId
            };

            return await _imageGen.GenerateImageAsync(request);
        }
        finally
        {
            // Clean up the temp composite file
            try
            {
                var fullPath = Path.Combine(_env.WebRootPath, tempRelPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            catch { /* ignore cleanup failure */ }
        }
    }

    private static string GetColorName(string hex) => hex.ToUpperInvariant() switch
    {
        "#FFFFFF" => "white",
        "#222222" => "black",
        "#C0C0C0" or "#808080" => "grey",
        "#1B3A5C" => "navy blue",
        "#C0392B" => "red",
        "#2E7D32" => "green",
        "#1565C0" => "royal blue",
        "#F9A825" => "yellow",
        "#8D6E63" => "kraft brown",
        "#D7CCC8" => "beige",
        "#FFF8E7" => "off-white",
        _ => "colored"
    };
}

public record MockupProduct(
    string Name,
    string Slug,
    string Category,
    string[] Zones,
    string[] Colors,
    string[] Materials);
