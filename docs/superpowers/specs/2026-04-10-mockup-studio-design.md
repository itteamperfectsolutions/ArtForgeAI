# Mockup Studio — Design Spec

## Overview

A new page that lets users upload a logo or image and instantly preview it on 20 product mockups (mugs, t-shirts, caps, phone cases, etc.). Uses a two-stage flow: Stage 1 shows all products in a grid preview, Stage 2 lets users click into any product for full customization. Template-based previews render instantly via CSS/SVG; AI-enhanced photorealistic versions available for coins.

## User Flow

### Stage 1 — Upload & Preview Grid

1. User uploads a logo/image (PNG, SVG, JPG — transparent background recommended).
2. Background is auto-removed if the image is not transparent (reuses existing BG removal pipeline).
3. All 20 products render instantly in a grid with the logo composited via SVG overlay templates.
4. Category filter tabs: All | Drinkware | Apparel | Stationery | Home | Promotional.
5. Each product card shows: product SVG shape with logo overlaid, product name.
6. Locked products (based on subscription tier) display a lock icon overlay.
7. "Download All (ZIP)" button for the entire grid of unlocked products.

### Stage 2 — Product Customization View

1. User clicks any unlocked product card to transition to the customization view.
2. Left side: large product preview with the logo rendered in real-time as settings change.
3. Right side: customization controls panel.
4. Back button to return to the grid.
5. "Download" button (template-based high-res render, costs coins).
6. "AI Enhance" button (photorealistic mockup via Gemini, costs more coins).

### Customization Controls (Stage 2)

- **Placement zone** — front/back/side/wrap (varies per product)
- **Position** — drag on preview or X/Y numeric inputs
- **Size** — scale slider
- **Rotation** — degree slider (0-360)
- **Product color** — picker with category-specific presets
- **Text overlay** — tagline text, font picker, color, position
- **Opacity** — logo transparency slider (0-100%)
- **Effects** — shadow toggle, outline toggle with color picker
- **Material/Finish** — matte/glossy (visual hint, used in AI enhancement prompt)

## Products (20 total)

### Drinkware
| Product | Print Zones | Colors | Materials |
|---------|-------------|--------|-----------|
| Standard Mug (11oz/15oz) | Wrap-around | White, Black, Silver | Ceramic |
| Travel Mug / Tumbler | Cylindrical wrap | White, Black, Silver | Stainless Steel |
| Water Bottle | Front label | White, Black, Silver | Stainless Steel |

### Apparel
| Product | Print Zones | Colors | Materials |
|---------|-------------|--------|-----------|
| T-Shirt | Front chest, Back, Left pocket | White, Black, Navy, Red, Grey, Green, Royal Blue, Yellow | Cotton, Polyester blend |
| Hoodie / Sweatshirt | Front chest, Back, Left chest | White, Black, Navy, Red, Grey, Green, Royal Blue, Yellow | Cotton, Polyester blend |
| Cap / Hat | Front panel, Side, Back | White, Black, Navy, Red, Grey, Green, Royal Blue, Yellow | Cotton, Polyester blend |
| Polo Shirt | Left chest, Back | White, Black, Navy, Red, Grey, Green, Royal Blue, Yellow | Cotton, Polyester blend |
| Tote Bag | Front, Back | White, Black, Navy, Red, Grey, Green, Royal Blue, Yellow | Cotton, Polyester blend |

### Stationery & Office
| Product | Print Zones | Colors | Materials |
|---------|-------------|--------|-----------|
| Notebook / Journal | Front cover | White, Black, Kraft Brown | Matte, Glossy |
| Mouse Pad | Full surface | White, Black, Kraft Brown | Matte, Glossy |
| Pen | Barrel area | White, Black, Kraft Brown | Matte, Glossy |

### Home & Lifestyle
| Product | Print Zones | Colors | Materials |
|---------|-------------|--------|-----------|
| Throw Pillow / Cushion | Front face | White, Black, Grey, Beige | Canvas, Fabric |
| Phone Case | Back panel | White, Black, Grey, Beige | Plastic |
| Wall Clock | Clock face | White, Black, Grey, Beige | Plastic |
| Coaster | Full surface | White, Black, Grey, Beige | Canvas, Fabric |
| Canvas / Poster Print | Full surface | White, Black, Grey, Beige | Canvas |

### Promotional / Business
| Product | Print Zones | Colors | Materials |
|---------|-------------|--------|-----------|
| Business Card | Front, Back | White, Off-white | Matte, Glossy, Linen |
| Letterhead / Stationery | Header area | White, Off-white | Matte, Glossy, Linen |
| ID Badge / Lanyard | Front face | White, Off-white | Matte, Glossy, Linen |
| Banner / Flag | Centered area | White, Off-white | Matte, Glossy, Linen |

## Subscription Gating

| Tier | Products Available | Template Download | AI Enhancement |
|------|-------------------|-------------------|----------------|
| Free | 3 (Mug, T-Shirt, Business Card) | Yes | No |
| Starter | 10 (Free + Cap, Tote Bag, Phone Case, Notebook, Canvas Print, Water Bottle, Mousepad) | Yes | Yes (standard coin cost) |
| Pro | All 20 | Yes | Yes (reduced coin cost) |
| Enterprise | All 20 | Yes | Yes (lowest cost + batch enhance) |

## Coin Costs

| Action | Cost |
|--------|------|
| Template download (single product) | 1 coin |
| Template download all (ZIP) | 5 coins |
| AI-enhanced mockup (single product) | 3 coins |
| AI-enhanced batch (all unlocked, Enterprise only) | 20 coins |

## Technical Architecture

### New Files
- `Components/Pages/MockupStudio.razor` — page component at route `/mockup-studio`
- `Services/MockupService.cs` — template loading, compositing, AI enhancement orchestration
- `Models/MockupTemplate.cs` — product metadata model (name, category, print zones, colors, materials)
- `wwwroot/mockup-templates/` — SVG templates per product with defined print zones
- `wwwroot/css/mockup-studio.css` — page-specific styling

### Modified Files
- `Components/Layout/NavMenu.razor` — add "Mockup Studio" nav entry
- `Models/FeatureAccess.cs` — add `MockupStudio` feature with tier-based product limits
- `Program.cs` — seed MockupTemplate data using `incrementalStyles` pattern

### Rendering Pipeline

**Template previews (Stage 1 grid + Stage 2 live preview):**
- SVG templates define product shape and print zone coordinates/dimensions
- Client-side: logo image overlaid onto SVG print zone via CSS transforms (position, scale, rotation, opacity)
- Instant, no server round-trip needed for preview updates

**Template downloads:**
- Server-side compositing using `SixLabors.ImageSharp`
- Renders logo onto product template image at 300 DPI
- Applies all customization settings (position, scale, rotation, color, effects)

**AI-enhanced downloads:**
- Sends template-rendered mockup + structured prompt to Gemini (`GeminiImageService`)
- Prompt includes: product type, color, material/finish, logo description, placement details
- Returns photorealistic mockup image

### Data Model — MockupTemplate

```
MockupTemplate:
  Id: int
  Name: string (e.g. "Standard Mug")
  Slug: string (e.g. "standard-mug")
  Category: string (e.g. "Drinkware")
  SvgTemplatePath: string (path to SVG file)
  PrintZones: JSON array of { Name, X, Y, Width, Height }
  AvailableColors: JSON array of hex strings
  AvailableMaterials: JSON array of strings
  SortOrder: int
  IsActive: bool
```

### Integration Points
- **BG Removal:** Reuse existing `BackgroundRemovalService` for auto-removing logo backgrounds
- **Image Compositing:** `SixLabors.ImageSharp` (already in project dependencies)
- **AI Enhancement:** `GeminiImageService` (existing service)
- **Coin Deduction:** `CoinService` (existing service)
- **Feature Gating:** `FeatureAccess` (existing pattern)
- **Download:** Reuse existing download infrastructure (300 DPI embedding, ZIP generation)
