using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public class MockupService
{
    private readonly IImageGenerationService _imageGen;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MockupService> _logger;

    public MockupService(
        IImageGenerationService imageGen,
        IWebHostEnvironment env,
        ILogger<MockupService> logger)
    {
        _imageGen = imageGen;
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
        using var product = new Image<Rgba32>(outputWidth, outputHeight);
        Color bgColor;
        if (!Color.TryParseHex(productColor, out bgColor))
            bgColor = Color.White;
        product.Mutate(ctx => ctx.Fill(bgColor));

        using var logo = Image.Load<Rgba32>(logoBytes);
        int logoW = (int)(logo.Width * logoScale);
        int logoH = (int)(logo.Height * logoScale);
        logoW = Math.Max(logoW, 1);
        logoH = Math.Max(logoH, 1);

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

    public async Task<GenerationResult> GenerateAiMockup(
        string logoImagePath,
        string productName,
        string productColor,
        string material,
        string zone,
        string userId)
    {
        var colorName = GetColorName(productColor);
        var prompt = $"Create a photorealistic product mockup photograph of a {colorName} {productName} made of {material}. " +
                     $"The logo/design from the reference image should be professionally printed on the {zone} of the {productName}. " +
                     $"Studio lighting, clean white background, professional product photography style. High quality, sharp details.";

        var request = new GenerationRequest
        {
            Prompt = prompt,
            ReferenceImagePaths = [logoImagePath],
            Provider = ImageProvider.Gemini,
            EnhancePrompt = false,
            Width = 1536,
            Height = 1024,
            UserId = userId
        };

        return await _imageGen.GenerateImageAsync(request);
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
