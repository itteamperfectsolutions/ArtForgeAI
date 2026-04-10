# Mockup Studio Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a two-stage mockup studio page where users upload a logo/image and see it previewed on 20 product mockups (mugs, t-shirts, caps, etc.), with per-product customization and AI-enhanced photorealistic rendering.

**Architecture:** Blazor Server page with SVG-based client-side preview rendering (Stage 1 grid), a customization panel with live preview (Stage 2), server-side ImageSharp compositing for downloads, and Gemini AI enhancement for photorealistic renders. Subscription-gated by tier with coin-based transactions.

**Tech Stack:** ASP.NET Core 8, Blazor Server, SixLabors.ImageSharp, SVG templates, Gemini API (via existing GeminiImageService), JSZip for batch downloads.

---

## File Map

| Action | File | Responsibility |
|--------|------|---------------|
| Create | `Components/Pages/MockupStudio.razor` | Two-stage page: upload grid + customization view |
| Create | `Services/MockupService.cs` | Server-side compositing, AI enhancement orchestration |
| Create | `wwwroot/css/mockup-studio.css` | Page-specific styling |
| Create | `wwwroot/js/mockup-studio.js` | Client-side logo positioning, drag, ZIP download |
| Create | `wwwroot/mockup-templates/*.svg` | 20 SVG product templates with print zones |
| Modify | `Models/FeatureAccess.cs` | Add MockupStudio feature constant, plan gating, coin costs |
| Modify | `Components/Layout/NavMenu.razor` | Add sidebar nav entry |

---

### Task 1: Register MockupStudio Feature in FeatureAccess

**Files:**
- Modify: `ArtForgeAI/Models/FeatureAccess.cs`

- [ ] **Step 1: Add the feature constant**

In `FeatureAccess.cs`, add after line 27 (`public const string VideoCreator = "VideoCreator";`):

```csharp
public const string MockupStudio = "MockupStudio";
```

- [ ] **Step 2: Add to plan feature arrays**

Update the `PlanFeatures` dictionary:

- `"Free"` array: add `MockupStudio` (free tier gets 3 products — gating is handled in the page logic, not FeatureAccess)
- `"Starter"` array: add `MockupStudio`
- `"Pro"` array: add `MockupStudio`
- `"Enterprise"` array: add `MockupStudio`

Each array gets `MockupStudio` appended to its existing entries.

- [ ] **Step 3: Add coin cost**

Add to `GenerationCosts` dictionary after the `[VideoCreator] = 5` line:

```csharp
[MockupStudio] = 1,
```

This is the base cost for a single template download. AI enhancement (3 coins) is handled separately in the page.

- [ ] **Step 4: Add to AllFeatures array**

Append `MockupStudio` to the `AllFeatures` array:

```csharp
public static readonly string[] AllFeatures =
[
    QuickStyle, Home, StyleTransfer, StyleRemix, Gallery, MosaicPoster,
    PassportPhoto, ImageViewer, Settings, PhotoExpand, GangSheet, ShapeCutSheet, NegativeScan, AutoEnhance, PhotoCollages, Merger, EmbroideryArt, BackgroundRemoval, SignatureDayDesign, FaceAging, EmotionTransfer, VideoCreator, MockupStudio
];
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: Build succeeds with no errors.

- [ ] **Step 6: Commit**

```bash
git add ArtForgeAI/Models/FeatureAccess.cs
git commit -m "feat: register MockupStudio in FeatureAccess with plan gating and coin costs"
```

---

### Task 2: Add NavMenu Entry

**Files:**
- Modify: `ArtForgeAI/Components/Layout/NavMenu.razor`

- [ ] **Step 1: Add nav item**

In `NavMenu.razor`, add to the `AllFeatureNavItems` array (after the VideoCreator entry at approximately line 199):

```csharp
new("MockupStudio", "mockup-studio", "&#128083;", "Mockup Studio"),
```

`&#128083;` is the top hat emoji — a distinct icon for branding/mockups.

- [ ] **Step 2: Add to DescriptionToFeatureKey dictionary**

Add after the `["Video Creator generation"] = "VideoCreator"` entry:

```csharp
["Mockup Studio download"] = "MockupStudio",
["Mockup Studio AI enhance"] = "MockupStudio",
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add ArtForgeAI/Components/Layout/NavMenu.razor
git commit -m "feat: add Mockup Studio to sidebar navigation"
```

---

### Task 3: Create SVG Product Templates

**Files:**
- Create: `ArtForgeAI/wwwroot/mockup-templates/mug.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/travel-mug.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/water-bottle.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/tshirt.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/hoodie.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/cap.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/polo.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/tote-bag.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/notebook.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/mousepad.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/pen.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/pillow.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/phone-case.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/wall-clock.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/coaster.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/canvas-print.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/business-card.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/letterhead.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/id-badge.svg`
- Create: `ArtForgeAI/wwwroot/mockup-templates/banner.svg`

Each SVG must:
- Be 400x400 viewport for grid display
- Include a product shape drawing (outline/filled shape of the product)
- Define a `<rect>` element with `class="print-zone"` and `data-zone="front"` (or back/side/wrap) marking where the logo goes
- Use a neutral product color as default (white/light grey fill)
- Use `data-product` attribute on the root `<svg>` element for identification

- [ ] **Step 1: Create the mockup-templates directory**

```bash
ls ArtForgeAI/wwwroot/  # verify parent exists
mkdir -p ArtForgeAI/wwwroot/mockup-templates
```

- [ ] **Step 2: Create the mug SVG template**

Create `ArtForgeAI/wwwroot/mockup-templates/mug.svg`:

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 400" data-product="mug">
  <!-- Mug body -->
  <rect x="80" y="100" width="200" height="220" rx="12" fill="#f0f0f0" stroke="#ccc" stroke-width="2"/>
  <!-- Handle -->
  <path d="M280,160 C320,160 340,200 340,230 C340,260 320,300 280,300" fill="none" stroke="#ccc" stroke-width="8" stroke-linecap="round"/>
  <!-- Rim -->
  <ellipse cx="180" cy="100" rx="100" ry="15" fill="#e8e8e8" stroke="#ccc" stroke-width="2"/>
  <!-- Print zone: front wrap -->
  <rect class="print-zone" data-zone="front" x="110" y="150" width="140" height="130" fill="none" stroke="none"/>
</svg>
```

- [ ] **Step 3: Create the t-shirt SVG template**

Create `ArtForgeAI/wwwroot/mockup-templates/tshirt.svg`:

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 400" data-product="tshirt">
  <!-- T-shirt body -->
  <path d="M100,80 L60,120 L90,150 L110,130 L110,350 L290,350 L290,130 L310,150 L340,120 L300,80 L250,60 C240,80 220,95 200,95 C180,95 160,80 150,60 Z" fill="#f0f0f0" stroke="#ccc" stroke-width="2"/>
  <!-- Collar -->
  <path d="M150,60 C160,80 180,95 200,95 C220,95 240,80 250,60" fill="none" stroke="#ccc" stroke-width="2"/>
  <!-- Print zone: front chest -->
  <rect class="print-zone" data-zone="front" x="140" y="120" width="120" height="120" fill="none" stroke="none"/>
  <!-- Print zone: back (same position, toggled via UI) -->
  <rect class="print-zone" data-zone="back" x="130" y="110" width="140" height="160" fill="none" stroke="none" style="display:none"/>
  <!-- Print zone: left pocket -->
  <rect class="print-zone" data-zone="pocket" x="210" y="120" width="50" height="50" fill="none" stroke="none" style="display:none"/>
</svg>
```

- [ ] **Step 4: Create remaining 18 SVG templates**

Create each of the following files following the same pattern — a product shape with `<rect class="print-zone" data-zone="...">` elements defining print areas. Each SVG is 400x400 viewport:

- `cap.svg` — baseball cap shape with front-panel, side, back zones
- `hoodie.svg` — hoodie outline with front-chest, back, left-chest zones
- `polo.svg` — polo with collar, left-chest and back zones
- `tote-bag.svg` — rectangular tote shape with front and back zones
- `travel-mug.svg` — tall cylindrical tumbler with wrap zone
- `water-bottle.svg` — bottle shape with front-label zone
- `notebook.svg` — rectangle with rounded corners, front-cover zone
- `mousepad.svg` — rounded rectangle, full-surface zone
- `pen.svg` — elongated pen shape, barrel zone
- `pillow.svg` — square with soft edges, front-face zone
- `phone-case.svg` — phone outline with back-panel zone
- `wall-clock.svg` — circle with hour markers, face zone
- `coaster.svg` — rounded square, full-surface zone
- `canvas-print.svg` — framed rectangle, full-surface zone
- `business-card.svg` — standard card proportions, front and back zones
- `letterhead.svg` — A4 proportions, header zone
- `id-badge.svg` — portrait card with lanyard, front-face zone
- `banner.svg` — wide rectangle, centered zone

Each template must include the `data-product` attribute on the root SVG and proper `class="print-zone"` rects with `data-zone` attributes.

- [ ] **Step 5: Commit**

```bash
git add ArtForgeAI/wwwroot/mockup-templates/
git commit -m "feat: add 20 SVG product mockup templates with print zones"
```

---

### Task 4: Create MockupService

**Files:**
- Create: `ArtForgeAI/Services/MockupService.cs`

- [ ] **Step 1: Create the service file**

Create `ArtForgeAI/Services/MockupService.cs`:

```csharp
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

    /// <summary>Check if a product is available for the given plan.</summary>
    public static bool IsProductAvailable(string planName, string productSlug)
    {
        if (TierProducts.TryGetValue(planName, out var slugs))
            return slugs.Contains(productSlug);
        return false;
    }

    /// <summary>Get the user's current plan name for tier checks.</summary>
    public static string[] GetAvailableProductSlugs(string planName)
    {
        return TierProducts.TryGetValue(planName, out var slugs) ? slugs : [];
    }

    /// <summary>
    /// Composites a logo onto a product template image at high resolution for download.
    /// </summary>
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
        // Fill with product color
        var bgColor = Color.ParseHex(productColor);
        product.Mutate(ctx => ctx.Fill(bgColor));

        // Load and resize logo
        using var logo = Image.Load<Rgba32>(logoBytes);
        int logoW = (int)(logo.Width * logoScale);
        int logoH = (int)(logo.Height * logoScale);
        if (logoW > 0 && logoH > 0)
        {
            logo.Mutate(ctx =>
            {
                ctx.Resize(logoW, logoH);
                if (Math.Abs(logoRotation) > 0.1f)
                    ctx.Rotate(logoRotation);
            });

            // Apply opacity
            if (logoOpacity < 1.0f)
            {
                logo.Mutate(ctx => ctx.Opacity(logoOpacity));
            }

            // Calculate position (logoX/logoY are 0-1 normalized coordinates)
            int posX = (int)(logoX * outputWidth) - (logo.Width / 2);
            int posY = (int)(logoY * outputHeight) - (logo.Height / 2);

            product.Mutate(ctx => ctx.DrawImage(logo, new Point(posX, posY), 1f));
        }

        // Embed 300 DPI
        product.Metadata.HorizontalResolution = 300;
        product.Metadata.VerticalResolution = 300;
        product.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;

        using var ms = new MemoryStream();
        await product.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Generates a photorealistic AI-enhanced mockup using Gemini.
    /// </summary>
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
        "#C0C0C0" or "#808080" or "#C0C0C0" => "grey",
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

/// <summary>Metadata for a single mockup product template.</summary>
public record MockupProduct(
    string Name,
    string Slug,
    string Category,
    string[] Zones,
    string[] Colors,
    string[] Materials);
```

- [ ] **Step 2: Register in DI**

In `Program.cs`, find where other services are registered (search for `builder.Services.AddScoped` or `AddSingleton` near other service registrations) and add:

```csharp
builder.Services.AddScoped<ArtForgeAI.Services.MockupService>();
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add ArtForgeAI/Services/MockupService.cs ArtForgeAI/Program.cs
git commit -m "feat: add MockupService with compositing and AI enhancement"
```

---

### Task 5: Create mockup-studio.js (Client-Side Interactivity)

**Files:**
- Create: `ArtForgeAI/wwwroot/js/mockup-studio.js`

- [ ] **Step 1: Create the JS file**

Create `ArtForgeAI/wwwroot/js/mockup-studio.js`:

```javascript
window.mockupStudio = (function () {

    /**
     * Initialize drag-to-position on the logo preview element.
     * @param {string} containerId - The ID of the preview container
     * @param {DotNetObjectReference} dotnetRef - Blazor component reference
     */
    function initLogoDrag(containerId, dotnetRef) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const logo = container.querySelector('.ms-logo-draggable');
        if (!logo) return;

        let isDragging = false;
        let startX, startY, origLeft, origTop;

        logo.addEventListener('pointerdown', (e) => {
            isDragging = true;
            startX = e.clientX;
            startY = e.clientY;
            origLeft = logo.offsetLeft;
            origTop = logo.offsetTop;
            logo.setPointerCapture(e.pointerId);
            e.preventDefault();
        });

        logo.addEventListener('pointermove', (e) => {
            if (!isDragging) return;
            const dx = e.clientX - startX;
            const dy = e.clientY - startY;
            const newLeft = origLeft + dx;
            const newTop = origTop + dy;
            logo.style.left = newLeft + 'px';
            logo.style.top = newTop + 'px';
        });

        logo.addEventListener('pointerup', (e) => {
            if (!isDragging) return;
            isDragging = false;

            // Calculate normalized position (0-1) relative to container
            const rect = container.getBoundingClientRect();
            const normX = (logo.offsetLeft + logo.offsetWidth / 2) / rect.width;
            const normY = (logo.offsetTop + logo.offsetHeight / 2) / rect.height;

            dotnetRef.invokeMethodAsync('OnLogoPositionChanged', normX, normY);
        });
    }

    /**
     * Download all mockups as a ZIP file using JSZip.
     * @param {string[]} base64Images - Array of base64 PNG data (no data: prefix)
     * @param {string[]} names - Array of product names for filenames
     */
    async function downloadAllAsZip(base64Images, names) {
        const zip = new JSZip();
        const timestamp = new Date().toISOString().slice(0, 10).replace(/-/g, '');

        for (let i = 0; i < base64Images.length; i++) {
            const bytes = Uint8Array.from(atob(base64Images[i]), c => c.charCodeAt(0));
            zip.file(`${names[i]}_mockup.png`, bytes);
        }

        const blob = await zip.generateAsync({ type: 'blob' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `mockups_${timestamp}.zip`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    return {
        initLogoDrag,
        downloadAllAsZip
    };

})();
```

- [ ] **Step 2: Commit**

```bash
git add ArtForgeAI/wwwroot/js/mockup-studio.js
git commit -m "feat: add mockup-studio.js with logo drag and ZIP download"
```

---

### Task 6: Create mockup-studio.css

**Files:**
- Create: `ArtForgeAI/wwwroot/css/mockup-studio.css`

- [ ] **Step 1: Create the CSS file**

Create `ArtForgeAI/wwwroot/css/mockup-studio.css`:

```css
/* ── Mockup Studio Styles ── */

/* Stage 1: Category filter tabs */
.ms-category-tabs {
    display: flex;
    gap: 0.35rem;
    flex-wrap: wrap;
    margin-bottom: 0.75rem;
}

.ms-tab {
    padding: 0.3rem 0.75rem;
    border: 1px solid var(--border-color);
    border-radius: 999px;
    background: var(--bg-secondary);
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 0.75rem;
    font-weight: 600;
    transition: all 0.2s ease;
}

.ms-tab:hover {
    border-color: var(--primary);
    color: var(--text-primary);
}

.ms-tab.active {
    background: var(--primary);
    border-color: var(--primary);
    color: #fff;
}

/* Stage 1: Product grid */
.ms-product-grid {
    display: grid;
    grid-template-columns: repeat(5, 1fr);
    gap: 0.6rem;
}

.ms-product-card {
    position: relative;
    background: var(--bg-secondary);
    border: 2px solid var(--border-color);
    border-radius: var(--radius-sm);
    padding: 0.5rem;
    text-align: center;
    cursor: pointer;
    transition: all 0.2s ease;
    overflow: hidden;
}

.ms-product-card:hover {
    border-color: var(--primary);
    transform: translateY(-2px);
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.2);
}

.ms-product-card.locked {
    opacity: 0.5;
    cursor: not-allowed;
}

.ms-product-card.locked:hover {
    border-color: var(--border-color);
    transform: none;
    box-shadow: none;
}

.ms-lock-overlay {
    position: absolute;
    top: 4px;
    right: 4px;
    font-size: 0.85rem;
    opacity: 0.7;
}

.ms-product-preview {
    position: relative;
    width: 100%;
    aspect-ratio: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    margin-bottom: 0.35rem;
}

.ms-product-preview svg {
    width: 100%;
    height: 100%;
}

.ms-product-preview .ms-grid-logo {
    position: absolute;
    max-width: 40%;
    max-height: 40%;
    object-fit: contain;
    pointer-events: none;
}

.ms-product-name {
    font-size: 0.72rem;
    font-weight: 600;
    color: var(--text-secondary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

/* Stage 2: Customization layout */
.ms-customize-layout {
    display: grid;
    grid-template-columns: 1fr 320px;
    gap: 1rem;
    min-height: 500px;
}

.ms-preview-panel {
    background: var(--bg-secondary);
    border-radius: var(--radius-sm);
    border: 2px solid var(--border-color);
    display: flex;
    align-items: center;
    justify-content: center;
    position: relative;
    overflow: hidden;
    min-height: 400px;
}

.ms-preview-panel svg {
    width: 80%;
    height: 80%;
}

.ms-logo-draggable {
    position: absolute;
    cursor: grab;
    max-width: 30%;
    max-height: 30%;
    object-fit: contain;
    user-select: none;
    touch-action: none;
}

.ms-logo-draggable:active {
    cursor: grabbing;
}

/* Controls panel */
.ms-controls-panel {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
}

.ms-control-group {
    background: var(--bg-secondary);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    padding: 0.6rem;
}

.ms-control-label {
    font-size: 0.72rem;
    font-weight: 600;
    color: var(--text-secondary);
    margin-bottom: 0.35rem;
    display: block;
}

.ms-color-picker {
    display: flex;
    gap: 0.3rem;
    flex-wrap: wrap;
}

.ms-color-swatch {
    width: 24px;
    height: 24px;
    border-radius: 50%;
    border: 2px solid var(--border-color);
    cursor: pointer;
    transition: all 0.2s ease;
}

.ms-color-swatch:hover {
    transform: scale(1.15);
}

.ms-color-swatch.active {
    border-color: var(--primary);
    box-shadow: 0 0 0 2px rgba(99, 102, 241, 0.4);
}

.ms-zone-selector {
    display: flex;
    gap: 0.3rem;
    flex-wrap: wrap;
}

.ms-zone-btn {
    padding: 0.25rem 0.6rem;
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    background: var(--bg-tertiary);
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 0.72rem;
    font-weight: 600;
    transition: all 0.2s ease;
}

.ms-zone-btn.active {
    border-color: var(--primary);
    background: rgba(99, 102, 241, 0.15);
    color: var(--primary);
}

.ms-slider-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
}

.ms-slider-row input[type="range"] {
    flex: 1;
    accent-color: var(--primary);
}

.ms-slider-value {
    font-size: 0.7rem;
    color: var(--text-muted);
    min-width: 36px;
    text-align: right;
}

/* Text overlay */
.ms-text-input {
    width: 100%;
    padding: 0.35rem 0.5rem;
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    background: var(--bg-tertiary);
    color: var(--text-primary);
    font-size: 0.8rem;
}

.ms-text-input:focus {
    border-color: var(--primary);
    outline: none;
}

/* Effect toggles */
.ms-toggle-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.2rem 0;
}

.ms-toggle-label {
    font-size: 0.75rem;
    color: var(--text-secondary);
}

/* Action buttons */
.ms-actions {
    display: flex;
    gap: 0.4rem;
    flex-wrap: wrap;
    margin-top: 0.5rem;
}

.ms-actions .btn-primary-gradient,
.ms-actions .btn-secondary {
    flex: 1;
    min-width: 120px;
    font-size: 0.82rem;
    padding: 0.45rem 0.6rem;
}

/* Back button */
.ms-back-btn {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.3rem 0.6rem;
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    background: var(--bg-secondary);
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 0.8rem;
    margin-bottom: 0.75rem;
    transition: all 0.2s ease;
}

.ms-back-btn:hover {
    border-color: var(--primary);
    color: var(--text-primary);
}

/* Material selector */
.ms-material-selector {
    display: flex;
    gap: 0.3rem;
    flex-wrap: wrap;
}

.ms-material-btn {
    padding: 0.2rem 0.5rem;
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    background: var(--bg-tertiary);
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 0.68rem;
    transition: all 0.2s ease;
}

.ms-material-btn.active {
    border-color: var(--primary);
    background: rgba(99, 102, 241, 0.15);
    color: var(--primary);
}

/* Responsive */
@media (max-width: 1024px) {
    .ms-product-grid {
        grid-template-columns: repeat(4, 1fr);
    }
    .ms-customize-layout {
        grid-template-columns: 1fr;
    }
}

@media (max-width: 768px) {
    .ms-product-grid {
        grid-template-columns: repeat(3, 1fr);
    }
}

@media (max-width: 480px) {
    .ms-product-grid {
        grid-template-columns: repeat(2, 1fr);
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add ArtForgeAI/wwwroot/css/mockup-studio.css
git commit -m "feat: add mockup-studio.css with two-stage layout styles"
```

---

### Task 7: Create MockupStudio.razor Page — Stage 1 (Upload + Grid)

**Files:**
- Create: `ArtForgeAI/Components/Pages/MockupStudio.razor`

- [ ] **Step 1: Create the page file with directives, Stage 1 markup, and upload handling**

Create `ArtForgeAI/Components/Pages/MockupStudio.razor`:

```razor
@page "/mockup-studio"
@rendermode InteractiveServer
@attribute [Authorize]
@inject IJSRuntime JS
@inject IWebHostEnvironment Env
@inject ILogger<MockupStudio> Logger
@inject IImageStorageService ImageStorage
@inject IImageGenerationService ImageGenService
@inject ISubscriptionService SubscriptionService
@inject ICoinService CoinService
@inject AuthenticationStateProvider AuthState
@inject NavigationManager Nav
@inject MockupService MockupSvc
@using ArtForgeAI.Services
@using ArtForgeAI.Models
@using SixLabors.ImageSharp
@using SixLabors.ImageSharp.PixelFormats
@using SixLabors.ImageSharp.Processing
@implements IDisposable

<PageTitle>ArtForge AI - Mockup Studio</PageTitle>

<link href="css/mockup-studio.css" rel="stylesheet" />
<script src="js/mockup-studio.js"></script>

<div class="page-header page-header-lg">
    <h2>Mockup Studio</h2>
    <p>Preview your logo or design on 20+ products — mugs, t-shirts, caps, and more</p>
</div>

@if (_stage == Stage.Upload)
{
    @* ── STAGE 1: Upload + Product Grid ── *@
    <div class="create-layout">
        <div class="create-left">
            <div class="card">
                <div class="card-body">
                    <div class="mb-3">
                        <label class="form-label">Upload Your Logo or Image</label>
                        @if (!string.IsNullOrEmpty(_uploadedDataUrl))
                        {
                            <div class="et-uploaded-preview">
                                <button class="ref-thumb-remove" @onclick="RemoveUpload" disabled="@_isProcessing"
                                        style="position:absolute;top:4px;right:4px;z-index:20;">&#10005;</button>
                                <img src="@_uploadedDataUrl" alt="Uploaded logo" class="et-preview-img" />
                            </div>
                        }
                        else
                        {
                            <label class="upload-area upload-area-compact @(_isUploading ? "disabled" : "")">
                                <span class="upload-icon">&#128083;</span>
                                <span>Upload a logo or image</span>
                                <span style="font-size:0.7rem;color:var(--text-muted);margin-left:0.5rem;">PNG, SVG, JPG — transparent background recommended</span>
                                <InputFile OnChange="HandleFileUpload" accept=".jpg,.jpeg,.png,.webp,.svg" style="display:none" disabled="@(_isProcessing || _isUploading)" />
                            </label>
                        }
                        @if (_isUploading)
                        {
                            <div class="upload-progress-bar" style="margin-top:0.35rem;">
                                <div class="upload-progress-fill" style="width:50%"></div>
                                <span class="upload-progress-text">Uploading...</span>
                            </div>
                        }
                    </div>

                    @if (!string.IsNullOrEmpty(_uploadedDataUrl))
                    {
                        <hr style="margin:0.3rem 0;border-color:var(--border-color);" />
                        <button class="btn-primary-gradient" style="width:100%;padding:0.4rem;font-size:0.85rem;"
                                @onclick="DownloadAllMockups" disabled="@_isProcessing">
                            &#128230; Download All (ZIP) &middot; 5 coins
                        </button>
                    }
                </div>
            </div>

            @if (!string.IsNullOrEmpty(_errorMessage))
            {
                <div class="card" style="margin-top:0.75rem;border-left:3px solid var(--danger);">
                    <div class="card-body" style="padding:0.75rem 1rem;color:var(--danger);font-size:0.85rem;">
                        <strong>Error:</strong> @_errorMessage
                    </div>
                </div>
            }
        </div>

        <div class="create-right">
            @if (!string.IsNullOrEmpty(_uploadedDataUrl))
            {
                <div class="right-panel-card">
                    <div class="right-panel-label">Product Mockups</div>

                    @* Category filter tabs *@
                    <div class="ms-category-tabs">
                        @foreach (var cat in _categories)
                        {
                            <button class="ms-tab @(_selectedCategory == cat ? "active" : "")"
                                    @onclick="() => _selectedCategory = cat">@cat</button>
                        }
                    </div>

                    @* Product grid *@
                    <div class="ms-product-grid">
                        @foreach (var product in FilteredProducts)
                        {
                            var p = product;
                            var isLocked = !_availableSlugs.Contains(p.Slug);
                            <div class="ms-product-card @(isLocked ? "locked" : "")"
                                 @onclick="() => OpenCustomize(p)" title="@(isLocked ? "Upgrade plan to unlock" : $"Customize {p.Name}")">

                                @if (isLocked)
                                {
                                    <span class="ms-lock-overlay">&#128274;</span>
                                }

                                <div class="ms-product-preview">
                                    <img src="mockup-templates/@(p.Slug).svg" alt="@p.Name" style="width:100%;height:100%;" />
                                    @if (!string.IsNullOrEmpty(_uploadedDataUrl))
                                    {
                                        <img src="@_uploadedDataUrl" class="ms-grid-logo" alt="Logo" />
                                    }
                                </div>
                                <div class="ms-product-name">@p.Name</div>
                            </div>
                        }
                    </div>
                </div>
            }
            else
            {
                <div class="right-panel-card" style="display:flex;align-items:center;justify-content:center;min-height:300px;">
                    <p style="color:var(--text-muted);font-size:0.9rem;">Upload a logo to see mockup previews</p>
                </div>
            }
        </div>
    </div>
}
else if (_stage == Stage.Customize)
{
    @* ── STAGE 2: Product Customization ── *@
    <button class="ms-back-btn" @onclick="BackToGrid">&#8592; Back to all products</button>

    <div class="ms-customize-layout">
        @* Left: Large preview *@
        <div class="ms-preview-panel" id="ms-preview-container">
            <img src="mockup-templates/@(_activeProduct!.Slug).svg" alt="@_activeProduct.Name"
                 style="width:80%;height:80%;position:absolute;" />
            @if (!string.IsNullOrEmpty(_uploadedDataUrl))
            {
                <img src="@_uploadedDataUrl" class="ms-logo-draggable" alt="Logo"
                     style="left:@(_logoX * 100)%;top:@(_logoY * 100)%;transform:translate(-50%,-50%) scale(@_logoScale) rotate(@(_logoRotation)deg);opacity:@_logoOpacity;" />
            }
        </div>

        @* Right: Controls *@
        <div class="ms-controls-panel">
            <h3 style="margin:0 0 0.3rem;font-size:1rem;color:var(--text-primary);">@_activeProduct!.Name</h3>

            @* Placement zone *@
            @if (_activeProduct.Zones.Length > 1)
            {
                <div class="ms-control-group">
                    <span class="ms-control-label">Placement Zone</span>
                    <div class="ms-zone-selector">
                        @foreach (var zone in _activeProduct.Zones)
                        {
                            var z = zone;
                            <button class="ms-zone-btn @(_selectedZone == z ? "active" : "")"
                                    @onclick="() => _selectedZone = z">@FormatZoneName(z)</button>
                        }
                    </div>
                </div>
            }

            @* Size slider *@
            <div class="ms-control-group">
                <span class="ms-control-label">Logo Size</span>
                <div class="ms-slider-row">
                    <input type="range" min="0.1" max="2.0" step="0.05" @bind="_logoScale" @bind:event="oninput" />
                    <span class="ms-slider-value">@($"{_logoScale:F1}x")</span>
                </div>
            </div>

            @* Rotation slider *@
            <div class="ms-control-group">
                <span class="ms-control-label">Rotation</span>
                <div class="ms-slider-row">
                    <input type="range" min="0" max="360" step="1" @bind="_logoRotation" @bind:event="oninput" />
                    <span class="ms-slider-value">@($"{_logoRotation:F0}")&deg;</span>
                </div>
            </div>

            @* Opacity slider *@
            <div class="ms-control-group">
                <span class="ms-control-label">Opacity</span>
                <div class="ms-slider-row">
                    <input type="range" min="0.1" max="1.0" step="0.05" @bind="_logoOpacity" @bind:event="oninput" />
                    <span class="ms-slider-value">@($"{(int)(_logoOpacity * 100)}%")</span>
                </div>
            </div>

            @* Product color *@
            <div class="ms-control-group">
                <span class="ms-control-label">Product Color</span>
                <div class="ms-color-picker">
                    @foreach (var color in _activeProduct.Colors)
                    {
                        var c = color;
                        <div class="ms-color-swatch @(_selectedColor == c ? "active" : "")"
                             style="background-color:@c;" @onclick="() => _selectedColor = c"
                             title="@c"></div>
                    }
                </div>
            </div>

            @* Material *@
            @if (_activeProduct.Materials.Length > 1)
            {
                <div class="ms-control-group">
                    <span class="ms-control-label">Material / Finish</span>
                    <div class="ms-material-selector">
                        @foreach (var mat in _activeProduct.Materials)
                        {
                            var m = mat;
                            <button class="ms-material-btn @(_selectedMaterial == m ? "active" : "")"
                                    @onclick="() => _selectedMaterial = m">@m</button>
                        }
                    </div>
                </div>
            }

            @* Text overlay *@
            <div class="ms-control-group">
                <span class="ms-control-label">Text Overlay (optional)</span>
                <input class="ms-text-input" type="text" placeholder="Add a tagline..." @bind="_overlayText" />
            </div>

            @* Effects *@
            <div class="ms-control-group">
                <span class="ms-control-label">Effects</span>
                <div class="ms-toggle-row">
                    <span class="ms-toggle-label">Drop Shadow</span>
                    <input type="checkbox" @bind="_shadowEnabled" />
                </div>
                <div class="ms-toggle-row">
                    <span class="ms-toggle-label">Outline</span>
                    <input type="checkbox" @bind="_outlineEnabled" />
                </div>
            </div>

            @* Action buttons *@
            <div class="ms-actions">
                <button class="btn-primary-gradient" @onclick="DownloadMockup" disabled="@_isProcessing">
                    &#128424; Download &middot; @FeatureAccess.GetCost("MockupStudio") coin
                </button>
                <button class="btn-secondary" @onclick="AiEnhanceMockup" disabled="@(_isProcessing || !_canAiEnhance)">
                    &#10024; AI Enhance &middot; 3 coins
                </button>
            </div>

            @if (!string.IsNullOrEmpty(_aiResultDataUrl))
            {
                <div class="ms-control-group" style="margin-top:0.5rem;">
                    <span class="ms-control-label">AI-Enhanced Result</span>
                    <div style="background:var(--bg-tertiary);border-radius:var(--radius-sm);padding:0.5rem;text-align:center;">
                        <img src="@_aiResultDataUrl" alt="AI enhanced" style="max-width:100%;max-height:250px;border-radius:var(--radius-sm);" />
                        <div style="margin-top:0.4rem;">
                            <button class="btn-primary-gradient" style="font-size:0.8rem;padding:0.3rem 0.8rem;" @onclick="DownloadAiResult">
                                &#128424; Download AI Mockup
                            </button>
                        </div>
                    </div>
                </div>
            }
        </div>
    </div>
}

@* ── Processing overlay ── *@
@if (_isProcessing)
{
    <div class="pp-processing-overlay">
        <div class="pp-processing-modal">
            <div class="processing-spinner" style="width:36px;height:36px;margin:0 auto 1rem;"></div>
            <p style="margin:0;font-size:0.95rem;color:var(--text-primary);">@_processingStatus</p>
        </div>
    </div>
}

@code {
    private enum Stage { Upload, Customize }

    // ── State ──
    private Stage _stage = Stage.Upload;
    private string? _uploadedPath;
    private string? _uploadedDataUrl;
    private byte[]? _uploadedBytes;
    private string? _uploadedFullPath;
    private bool _isUploading;
    private bool _isProcessing;
    private string _processingStatus = "";
    private string? _errorMessage;
    private int _userId;
    private bool _isSuperAdmin;
    private string _userPlan = "Free";

    // Category filter
    private readonly string[] _categories = ["All", "Drinkware", "Apparel", "Stationery", "Home", "Promotional"];
    private string _selectedCategory = "All";
    private string[] _availableSlugs = [];

    // Stage 2: Customization state
    private MockupProduct? _activeProduct;
    private string _selectedZone = "front";
    private float _logoX = 0.5f;
    private float _logoY = 0.5f;
    private float _logoScale = 1.0f;
    private float _logoRotation = 0f;
    private float _logoOpacity = 1.0f;
    private string _selectedColor = "#FFFFFF";
    private string _selectedMaterial = "";
    private string _overlayText = "";
    private bool _shadowEnabled;
    private bool _outlineEnabled;
    private string? _aiResultDataUrl;
    private string? _aiResultPath;
    private bool _canAiEnhance;

    private MockupProduct[] FilteredProducts =>
        _selectedCategory == "All"
            ? MockupService.AllProducts
            : MockupService.AllProducts.Where(p => p.Category == _selectedCategory).ToArray();

    // ── Lifecycle ──

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthState.GetAuthenticationStateAsync();
        var user = authState.User;
        _isSuperAdmin = user.IsInRole("SuperAdmin");

        var userIdStr = user.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (int.TryParse(userIdStr, out var uid))
        {
            _userId = uid;
            if (!_isSuperAdmin)
            {
                if (!await SubscriptionService.HasFeatureAccessAsync(uid, FeatureAccess.MockupStudio))
                {
                    Nav.NavigateTo("/pricing", forceLoad: true);
                    return;
                }
            }

            // Get user's plan for tier-based product gating
            var sub = await SubscriptionService.GetActiveSubscriptionAsync(uid);
            _userPlan = sub?.PlanName ?? "Free";
            _availableSlugs = _isSuperAdmin
                ? MockupService.AllProducts.Select(p => p.Slug).ToArray()
                : MockupService.GetAvailableProductSlugs(_userPlan);

            // Determine if user can use AI enhance (Starter+ only)
            _canAiEnhance = _isSuperAdmin || _userPlan != "Free";
        }
    }

    public void Dispose() { }

    // ── Upload ──

    private async Task HandleFileUpload(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file is null) return;

        if (file.Size > ImageUploadHelper.DefaultMaxUploadSize)
        {
            if (file.Size > ImageUploadHelper.MaxReadSize)
            {
                _errorMessage = $"File is too large ({ImageUploadHelper.FormatFileSize(file.Size)}). Maximum allowed is {ImageUploadHelper.FormatFileSize(ImageUploadHelper.MaxReadSize)}.";
                StateHasChanged();
                return;
            }
            try
            {
                using var readMs = new MemoryStream();
                await file.OpenReadStream(ImageUploadHelper.MaxReadSize).CopyToAsync(readMs);
                var compressed = ImageUploadHelper.CompressToFit(readMs.ToArray(), file.Name);
                await ProcessUpload(file.Name, compressed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Image compression failed");
                _errorMessage = "Failed to compress the image. Please try a smaller file.";
                StateHasChanged();
            }
            return;
        }

        using var stream = file.OpenReadStream(ImageUploadHelper.MaxReadSize);
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        await ProcessUpload(file.Name, ms);
    }

    private async Task ProcessUpload(string fileName, MemoryStream dataStream)
    {
        _isUploading = true;
        _errorMessage = null;
        StateHasChanged();

        try
        {
            dataStream.Position = 0;
            _uploadedPath = await ImageStorage.SaveUploadedFileAsync(dataStream, fileName);
            _uploadedFullPath = ResolveFullPath(_uploadedPath);
            _uploadedBytes = await File.ReadAllBytesAsync(_uploadedFullPath);

            var ext = _uploadedPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
            _uploadedDataUrl = $"data:image/{ext};base64," + Convert.ToBase64String(_uploadedBytes);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Upload failed");
            _errorMessage = "Upload failed. Please try again.";
        }
        finally
        {
            _isUploading = false;
            StateHasChanged();
        }
    }

    // ── Navigation ──

    private void OpenCustomize(MockupProduct product)
    {
        if (!_availableSlugs.Contains(product.Slug)) return;

        _activeProduct = product;
        _selectedZone = product.Zones[0];
        _selectedColor = product.Colors[0];
        _selectedMaterial = product.Materials[0];
        _logoX = 0.5f;
        _logoY = 0.5f;
        _logoScale = 1.0f;
        _logoRotation = 0f;
        _logoOpacity = 1.0f;
        _overlayText = "";
        _shadowEnabled = false;
        _outlineEnabled = false;
        _aiResultDataUrl = null;
        _aiResultPath = null;
        _stage = Stage.Customize;
        StateHasChanged();
    }

    private void BackToGrid()
    {
        _stage = Stage.Upload;
        _activeProduct = null;
        _aiResultDataUrl = null;
        _aiResultPath = null;
        StateHasChanged();
    }

    // ── Download: single product ──

    private async Task DownloadMockup()
    {
        if (_uploadedBytes == null || _activeProduct == null) return;

        if (!await TryDebitCoins(FeatureAccess.GetCost("MockupStudio"), "Mockup Studio download"))
            return;

        _isProcessing = true;
        _processingStatus = $"Rendering {_activeProduct.Name} mockup...";
        StateHasChanged();

        try
        {
            var pngBytes = await MockupSvc.CompositeForDownload(
                _uploadedBytes, _activeProduct.Slug, _selectedZone, _selectedColor,
                _logoX, _logoY, _logoScale, _logoRotation, _logoOpacity,
                string.IsNullOrWhiteSpace(_overlayText) ? null : _overlayText,
                null, null, _shadowEnabled, _outlineEnabled, null);

            var base64 = Convert.ToBase64String(pngBytes);
            var fileName = $"{_activeProduct.Slug}_mockup_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            await JS.InvokeVoidAsync("downloadFileFromBytes", fileName, "image/png", base64);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Mockup download failed");
            _errorMessage = "Download failed: " + ex.Message;
        }
        finally
        {
            _isProcessing = false;
            _processingStatus = "";
            StateHasChanged();
        }
    }

    // ── Download: all products as ZIP ──

    private async Task DownloadAllMockups()
    {
        if (_uploadedBytes == null) return;

        if (!await TryDebitCoins(5, "Mockup Studio download"))
            return;

        _isProcessing = true;
        _processingStatus = "Rendering all mockups...";
        StateHasChanged();

        try
        {
            var base64List = new List<string>();
            var nameList = new List<string>();

            foreach (var product in MockupService.AllProducts.Where(p => _availableSlugs.Contains(p.Slug)))
            {
                _processingStatus = $"Rendering {product.Name}...";
                StateHasChanged();
                await Task.Delay(1); // yield for UI update

                var pngBytes = await MockupSvc.CompositeForDownload(
                    _uploadedBytes, product.Slug, product.Zones[0], product.Colors[0],
                    0.5f, 0.5f, 1.0f, 0f, 1.0f, null, null, null, false, false, null);

                base64List.Add(Convert.ToBase64String(pngBytes));
                nameList.Add(product.Slug);
            }

            await JS.InvokeVoidAsync("mockupStudio.downloadAllAsZip",
                base64List.ToArray(), nameList.ToArray());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ZIP download failed");
            _errorMessage = "ZIP download failed: " + ex.Message;
        }
        finally
        {
            _isProcessing = false;
            _processingStatus = "";
            StateHasChanged();
        }
    }

    // ── AI Enhancement ──

    private async Task AiEnhanceMockup()
    {
        if (_uploadedFullPath == null || _activeProduct == null) return;

        if (!await TryDebitCoins(3, "Mockup Studio AI enhance"))
            return;

        _isProcessing = true;
        _processingStatus = $"Generating AI-enhanced {_activeProduct.Name} mockup...";
        _aiResultDataUrl = null;
        StateHasChanged();

        try
        {
            var result = await MockupSvc.GenerateAiMockup(
                _uploadedFullPath, _activeProduct.Name, _selectedColor,
                _selectedMaterial, _selectedZone, _userId.ToString());

            if (result.Success && !string.IsNullOrEmpty(result.LocalImagePath))
            {
                var resultPath = ResolveFullPath(result.LocalImagePath);
                var resultBytes = await File.ReadAllBytesAsync(resultPath);
                _aiResultDataUrl = "data:image/png;base64," + Convert.ToBase64String(resultBytes);
                _aiResultPath = resultPath;
            }
            else
            {
                _errorMessage = "AI enhancement failed: " + (result.ErrorMessage ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "AI enhancement failed");
            _errorMessage = "AI enhancement failed: " + ex.Message;
        }
        finally
        {
            _isProcessing = false;
            _processingStatus = "";
            StateHasChanged();
        }
    }

    private async Task DownloadAiResult()
    {
        if (string.IsNullOrEmpty(_aiResultDataUrl)) return;
        var base64 = _aiResultDataUrl[(_aiResultDataUrl.IndexOf(",", StringComparison.Ordinal) + 1)..];
        var dpiBase64 = await Task.Run(() => EmbedDpi300(base64));
        var fileName = $"{_activeProduct?.Slug ?? "mockup"}_ai_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        await JS.InvokeVoidAsync("downloadFileFromBytes", fileName, "image/png", dpiBase64);
    }

    // ── JS Interop ──

    [JSInvokable]
    public void OnLogoPositionChanged(float normX, float normY)
    {
        _logoX = Math.Clamp(normX, 0f, 1f);
        _logoY = Math.Clamp(normY, 0f, 1f);
        InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_stage == Stage.Customize && _activeProduct != null)
        {
            try
            {
                var dotnetRef = DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("mockupStudio.initLogoDrag", "ms-preview-container", dotnetRef);
            }
            catch { /* ignore if element not ready */ }
        }
    }

    // ── Helpers ──

    private async Task RemoveUpload()
    {
        if (!string.IsNullOrEmpty(_uploadedPath))
            await ImageStorage.DeleteImageAsync(_uploadedPath);

        _uploadedPath = null;
        _uploadedDataUrl = null;
        _uploadedBytes = null;
        _uploadedFullPath = null;
        _errorMessage = null;
        _stage = Stage.Upload;
        _activeProduct = null;
        _aiResultDataUrl = null;
    }

    private async Task<bool> TryDebitCoins(int cost, string description)
    {
        if (_isSuperAdmin) return true;

        if (!await CoinService.DebitCoinsAsync(_userId, cost, CoinTransactionType.GenerationSpend, description))
        {
            _errorMessage = "Insufficient coins. Please top up to continue.";
            StateHasChanged();
            return false;
        }
        return true;
    }

    private string ResolveFullPath(string webRelativePath) =>
        System.IO.Path.Combine(Env.WebRootPath, webRelativePath.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()));

    private static string EmbedDpi300(string base64Png, int dpi = 300)
    {
        var bytes = Convert.FromBase64String(base64Png);
        using var img = Image.Load<Rgba32>(bytes);
        img.Metadata.HorizontalResolution = dpi;
        img.Metadata.VerticalResolution = dpi;
        img.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
        using var ms = new MemoryStream();
        img.SaveAsPng(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string FormatZoneName(string zone) => zone switch
    {
        "front" => "Front",
        "back" => "Back",
        "side" => "Side",
        "left-chest" => "Left Chest",
        "pocket" => "Pocket",
        _ => zone[0..1].ToUpper() + zone[1..]
    };
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build`
Expected: Build succeeds. Fix any compilation errors (e.g., missing usings, method signature mismatches with `ISubscriptionService.GetActiveSubscriptionAsync`).

If `GetActiveSubscriptionAsync` does not exist on `ISubscriptionService`, search for the actual method name:

```bash
grep -n "GetActive\|GetSubscription\|PlanName" ArtForgeAI/Services/ISubscriptionService.cs
```

Then adjust the `OnInitializedAsync` code to use the correct method to retrieve the user's plan name.

- [ ] **Step 3: Commit**

```bash
git add ArtForgeAI/Components/Pages/MockupStudio.razor
git commit -m "feat: add MockupStudio.razor page with two-stage upload grid and customization"
```

---

### Task 8: Verify End-to-End Build and Manual Smoke Test

**Files:**
- No new files

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: Build succeeds with zero errors.

- [ ] **Step 2: Run the application**

Run: `dotnet run --project ArtForgeAI`
Expected: Application starts without errors.

- [ ] **Step 3: Manual smoke test checklist**

1. Navigate to `/mockup-studio` — page loads with upload area
2. Upload a PNG logo — grid of 20 products appears with logo overlaid
3. Category tabs filter the grid correctly
4. Locked products show lock icon and can't be clicked (test with Free plan)
5. Click an unlocked product — Stage 2 customization view opens
6. Sliders (size, rotation, opacity) update the preview
7. Color swatches change the product color
8. Zone buttons switch placement area
9. "Back to all products" returns to grid
10. "Download" generates and downloads a PNG
11. "AI Enhance" generates a photorealistic mockup (if Starter+ plan)
12. "Download All (ZIP)" downloads a ZIP of all unlocked products

- [ ] **Step 4: Fix any issues found during testing**

Address any rendering, styling, or functional issues discovered.

- [ ] **Step 5: Final commit**

```bash
git add -A
git commit -m "fix: address issues found during MockupStudio smoke test"
```

---

### Task 9: Add .gitignore Entry for .superpowers

**Files:**
- Modify: `.gitignore`

- [ ] **Step 1: Check if .superpowers is already ignored**

```bash
grep -n "superpowers" .gitignore
```

- [ ] **Step 2: Add if not present**

If not found, append to `.gitignore`:

```
.superpowers/
```

- [ ] **Step 3: Commit**

```bash
git add .gitignore
git commit -m "chore: add .superpowers to gitignore"
```
