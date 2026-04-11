# 3D Mockups Page — Design Spec

**Date:** 2026-04-11
**Status:** Approved

## Overview

A new page (`/3d-mockups`) that provides interactive 3D product previews with logo texture mapping and AI-powered photorealistic enhancement. Users can rotate/zoom real 3D models (mug, t-shirt), place their logo on the surface, and optionally generate an AI-enhanced photorealistic version via Gemini.

## Goals

- Interactive WebGL 3D viewer for product mockups (mug, t-shirt)
- Real-time logo placement on 3D surfaces with size/position controls
- AI enhancement via Gemini for photorealistic output
- Side-by-side comparison of 3D render vs AI result
- Free preview, paid downloads and AI enhancement

## Products (Initial Scope)

Two products for v1:

| Product | Model File | Colors | Free Tier |
|---------|-----------|--------|-----------|
| Standard Mug | `mug.glb` | White, Black, Grey | Yes |
| T-Shirt | `tshirt.glb` | White, Black, Grey, Navy, Red | Starter+ |

3D models sourced from free/open-source repositories (Sketchfab, etc.) in `.glb` format.

## Architecture

### New Files

| File | Purpose |
|------|---------|
| `Components/Pages/Mockup3D.razor` | Page component (Blazor Server) |
| `wwwroot/js/mockup3d.js` | Three.js scene, controls, texture mapping |
| `wwwroot/css/mockup3d.css` | Page-specific styles |
| `wwwroot/3d-models/mug.glb` | Mug 3D model |
| `wwwroot/3d-models/tshirt.glb` | T-shirt 3D model |
| `Services/Mockup3DService.cs` | AI enhancement, snapshot handling |

### Modified Files

| File | Change |
|------|--------|
| `Components/Layout/NavMenu.razor` | Add "3D Mockups" nav entry |
| `Program.cs` | Register `Mockup3DService` as scoped |

### Service Dependencies

- `Mockup3DService` — new service for AI mockup generation
- `IImageGenerationService` — existing, reused for Gemini AI calls
- `IImageStorageService` — existing, reused for saving snapshots/results
- `ICoinService` — existing, reused for coin debit
- `ISubscriptionService` — existing, reused for plan-based product access

## Page Layout

### Full-Width Viewer + Bottom Toolbar

The page uses an immersive layout: a large 3D viewport fills most of the screen, with a compact bottom toolbar for controls.

```
┌──────────────────────────────────────────────────┐
│                                                  │
│              3D WebGL Viewport                   │
│         (rotate/zoom with mouse/touch)           │
│                                                  │
│                  [Upload Logo]                    │
│                                                  │
├──────────────────────────────────────────────────┤
│ [Mug][T-Shirt] │ ⚪⚫🔘 │ Size━━ │ ↕━━ │ ↔━━ │ [⬇ 2][✨ 5] │
└──────────────────────────────────────────────────┘
```

### Bottom Toolbar Controls (left to right)

1. **Product tabs** — Mug / T-Shirt toggle buttons
2. **Color swatches** — Per-product color options
3. **Logo Size** — Slider controlling decal scale on 3D surface
4. **Vertical Position** — Slider moving logo up/down
5. **Horizontal Position** — Slider moving logo left/right
6. **Download button** — 2 coins, captures high-res PNG
7. **AI Enhance button** — 5 coins, sends snapshot to Gemini

### Responsive Behavior

- Toolbar wraps into two rows on smaller screens
- On mobile (<768px), toolbar becomes a scrollable horizontal strip
- 3D viewport adjusts height to `calc(100vh - toolbar - header)`

## 3D Viewer (Three.js)

### Technology

- **Three.js** loaded via CDN `<script>` tag (matches project's plain JS pattern)
- No npm/bundler — IIFE module pattern like existing `mockup-studio.js`

### Scene Setup

- `PerspectiveCamera`: 45deg FOV, positioned for product framing
- `OrbitControls`: mouse/touch drag to rotate, scroll to zoom
  - Constrain vertical rotation to prevent flipping (minPolarAngle/maxPolarAngle)
  - Damping enabled for smooth interaction
- `WebGLRenderer`: anti-aliased, transparent background option
- Background color: `#1a1a2e` (matching app aesthetic)

### Lighting

3-point studio lighting:
- **Key light** (warm white, DirectionalLight) — main illumination from upper-right
- **Fill light** (cool white, DirectionalLight) — softer, from upper-left
- **Rim light** (subtle, DirectionalLight) — from behind for edge definition
- **Ambient light** — low-intensity fill to prevent pure black shadows

### Model Loading

- Use `GLTFLoader` to load `.glb` files from `wwwroot/3d-models/`
- Show loading spinner during model load
- On product switch: dispose old model geometry/materials, load new model
- Center model in scene after loading (`Box3` + center offset)

### Logo Texture Mapping

- User uploads/selects logo -> image converted to `CanvasTexture`
- Applied as a **decal** on the model's front surface mesh
- Logo controls update texture transform in real-time (no re-render delay):
  - **Size**: scales the decal UV mapping
  - **Vertical position**: offsets decal Y
  - **Horizontal position**: offsets decal X
- PNG transparency preserved in texture (alphaMap or transparent material)

### Color Application

- Product color applied to the base material's `color` property
- Same color-replacement approach as existing Mockup Studio
- Instant update on swatch click

## Logo Source: Gallery Picker + Upload

Triggered by an "Upload Logo" button, shown as a modal/floating panel:

### Tab 1: Upload
- Drag-and-drop or file picker
- Accepts PNG, JPG, WebP
- Same upload component style as existing pages
- Transparent background recommended (shown as hint)

### Tab 2: Gallery
- Grid of user's previously uploaded/generated images
- Fetched via existing image storage/history services
- Click to select — logo appears on 3D model immediately
- Pagination or scroll for large galleries

## AI Enhancement

### Flow

1. User clicks "AI Enhance - 5 coins"
2. Coin check via `ICoinService.HasSufficientCoinsAsync()`
3. Debit 5 coins via `ICoinService.DebitCoinsAsync()`
4. Capture Three.js canvas as PNG: `renderer.domElement.toDataURL('image/png')`
   - Temporarily resize renderer to 1536x1024 for high-res capture
   - Restore original size after capture
5. Send snapshot to `Mockup3DService.GenerateAi3DMockup()`
6. Service saves snapshot via `IImageStorageService`, calls Gemini via `IImageGenerationService`
7. Show loading spinner overlay on viewer during generation
8. On completion, show side-by-side comparison panel

### AI Prompt

```
Transform this 3D product render into a photorealistic product photograph.
The product is a {color} {productName}.
Keep the exact logo/design placement and appearance from the image.
Add realistic textures, reflections, ambient occlusion, and studio lighting.
Clean white background, professional product photography style.
High quality, sharp details.
```

### Side-by-Side Comparison

Appears below the 3D viewer after AI enhancement completes:

```
┌────────────────────────┬────────────────────────┐
│    3D Render           │    AI Enhanced          │
│  ┌──────────────────┐  │  ┌──────────────────┐  │
│  │                  │  │  │                  │  │
│  │  [Canvas snap]   │  │  │  [Gemini result] │  │
│  │                  │  │  │                  │  │
│  └──────────────────┘  │  └──────────────────┘  │
│  ⬇ Download 3D Render  │  ⬇ Download AI Mockup  │
└────────────────────────┴────────────────────────┘
```

- Each side has its own download button
- Downloading the 3D render from comparison: 2 coins (standard download cost)
- Downloading the AI result: free (already paid 5 coins for generation)
- "Close" button to dismiss comparison and return to viewer
- 3D viewer remains interactive above

## Pricing

| Action | Cost |
|--------|------|
| 3D preview, rotate, zoom | Free |
| Upload/select logo | Free |
| Adjust logo size/position | Free |
| Download 3D render (PNG) | 2 coins |
| AI photorealistic enhance (includes AI download) | 5 coins |

## Navigation

- Sidebar entry: "3D Mockups" with cube emoji
- Feature key: `Mockup3D`
- Plan access: Free (mug only), Starter+ (mug + t-shirt)
- Position: after "Mockup Studio" in the sidebar

## JS Interop (Blazor <-> Three.js)

Communication between Blazor and the Three.js scene via `IJSRuntime`:

### Blazor -> JS calls:
- `mockup3d.initScene(canvasId)` — initialize Three.js scene
- `mockup3d.loadModel(modelUrl)` — load a .glb model
- `mockup3d.setColor(hexColor)` — change product color
- `mockup3d.setLogo(dataUrl)` — apply logo texture from data URL
- `mockup3d.updateLogoTransform(size, vPos, hPos)` — update logo decal
- `mockup3d.captureSnapshot(width, height)` — returns base64 PNG
- `mockup3d.dispose()` — cleanup on page leave

### JS -> Blazor callbacks:
- `onModelLoaded()` — model ready, hide spinner
- `onModelError(message)` — loading failed

## Error Handling

- Model load failure: show error message with retry button
- AI generation failure: refund coins, show error
- WebGL not supported: show fallback message suggesting a modern browser
- Gallery load failure: show upload tab as fallback

## Out of Scope (v1)

- AR mode / view in room
- Custom 3D model upload
- Multiple logos per product
- Text overlay on 3D surface
- More than 2 products
- Batch download / ZIP export
