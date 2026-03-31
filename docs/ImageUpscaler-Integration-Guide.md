# ImageUpscaler.js — Integration Guide

Standalone client-side image upscaler (2x, 4x, 8x) extracted from ArtForge AI.
Single file, zero build step, works in any browser project.

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [Installation](#2-installation)
3. [API Reference](#3-api-reference)
4. [Upscaling Modes](#4-upscaling-modes)
5. [Integration Examples](#5-integration-examples)
6. [AI Mode Setup (Real-ESRGAN)](#6-ai-mode-setup-real-esrgan)
7. [Architecture](#7-architecture)
8. [Performance & Limits](#8-performance--limits)
9. [Troubleshooting](#9-troubleshooting)

---

## 1. Quick Start

Copy `wwwroot/js/image-upscaler.js` into your project. No npm install, no bundler required.

```html
<!-- Lanczos mode (zero dependencies) -->
<script type="module">
  import { ImageUpscaler } from './image-upscaler.js';

  const upscaler = new ImageUpscaler();
  const fileInput = document.querySelector('#file');

  fileInput.addEventListener('change', async (e) => {
    const blob = await upscaler.upscale(e.target.files[0], { scale: 4 });
    ImageUpscaler.download(blob, 'upscaled_4x.png');
  });
</script>
```

That's it — a 4x upscaled PNG with 300 DPI metadata, downloaded to the user's machine.

---

## 2. Installation

### Option A: Copy the file (recommended)

```
your-project/
├── js/
│   └── image-upscaler.js    ← copy this file
├── models/                   ← optional, for AI mode
│   └── realesrgan-x4plus.onnx
└── index.html
```

### Option B: ES module import

```js
import { ImageUpscaler } from './js/image-upscaler.js';
```

### Option C: Script tag (non-module)

```html
<script src="js/image-upscaler.js" type="module"></script>
<script>
  // Available as window.ImageUpscaler after module loads
  const upscaler = new ImageUpscaler();
</script>
```

### Dependencies

| Mode     | Dependencies           |
|----------|------------------------|
| Lanczos  | **None** — pure JS     |
| AI       | `onnxruntime-web` CDN + ONNX model file |

---

## 3. API Reference

### `new ImageUpscaler()`

Creates an upscaler instance. Lightweight — no model loaded until you call `loadModel()`.

---

### `upscaler.loadModel(modelPath, ortOptions?)`

Loads an ONNX model for AI upscaling. Only needed if you want AI mode.

| Parameter    | Type   | Description |
|-------------|--------|-------------|
| `modelPath` | string | URL or path to the `.onnx` model file |
| `ortOptions`| object | Optional ONNX Runtime session options |

```js
await upscaler.loadModel('/models/realesrgan-x4plus.onnx');
```

---

### `upscaler.modelLoaded`

Boolean getter — `true` if an ONNX model is loaded and ready.

---

### `upscaler.upscale(source, options?)`

Upscales an image and returns a `Blob`.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `source`  | `HTMLImageElement` \| `HTMLCanvasElement` \| `OffscreenCanvas` \| `Blob` \| `File` \| `string` (URL/data-URL) | The input image |

**Options:**

| Option       | Type     | Default       | Description |
|-------------|----------|---------------|-------------|
| `scale`     | number   | `4`           | Upscale factor: `2`, `4`, or `8` |
| `mode`      | string   | auto          | `'ai'` or `'lanczos'`. Defaults to `'ai'` if model loaded, else `'lanczos'` |
| `format`    | string   | `'image/png'` | Output format: `'image/png'` or `'image/jpeg'` |
| `quality`   | number   | `0.95`        | JPEG quality (0–1). Ignored for PNG |
| `dpi`       | number   | `300`         | Embeds DPI in PNG pHYs chunk. Set `0` to skip |
| `maxDim`    | number   | `16384`       | Cap max output dimension to prevent OOM |
| `onProgress`| function | —             | Callback `(percent: number) => void`, 0–100 |

**Returns:** `Promise<Blob>`

```js
const blob = await upscaler.upscale(myImage, {
  scale: 2,
  format: 'image/jpeg',
  quality: 0.92,
  onProgress: (pct) => progressBar.style.width = pct + '%'
});
```

---

### `upscaler.upscaleAndDownload(source, options?)`

Combines `upscale()` + browser download in one call.

Additional option:

| Option     | Type   | Default               | Description |
|-----------|--------|-----------------------|-------------|
| `fileName`| string | `upscaled_{scale}x.{ext}` | Download filename |

```js
await upscaler.upscaleAndDownload(canvas, {
  scale: 8,
  fileName: 'poster_8x_300dpi.png'
});
```

---

### `ImageUpscaler.download(blob, fileName)` (static)

Triggers a browser download for any Blob.

```js
ImageUpscaler.download(myBlob, 'result.png');
```

---

### `ImageUpscaler.previewSize(width, height, scale, maxDim?)` (static)

Returns the target `{ width, height }` for a given scale, accounting for the max dimension cap. Useful for showing the user what dimensions they'll get before upscaling.

```js
const size = ImageUpscaler.previewSize(500, 400, 4);
// → { width: 2000, height: 1600 }

const capped = ImageUpscaler.previewSize(5000, 4000, 8);
// → { width: 16384, height: 13107 }  (capped at 16384)
```

---

### `ImageUpscaler.getSize(source)` (static async)

Returns `{ width, height }` of a source without fully processing it.

```js
const { width, height } = await ImageUpscaler.getSize(file);
```

---

## 4. Upscaling Modes

### Lanczos Mode (default, no dependencies)

Uses a two-pass **Lanczos3** resampling kernel — the same algorithm used by ImageSharp, Photoshop, and GIMP for high-quality resizing. Runs entirely in JavaScript on the main thread.

- **Best for:** Fast upscaling, print preparation, images that are already sharp
- **Quality:** Excellent interpolation, no AI hallucination
- **Speed:** Instant for most images (< 2 seconds)

### AI Mode (ONNX Runtime Web + Real-ESRGAN)

Uses the **Real-ESRGAN** neural network via ONNX Runtime Web. The model runs in the browser using WebGL or WASM — no server round-trip.

- **Best for:** Enhancing low-resolution photos, restoring detail, removing artifacts
- **Quality:** AI-generated detail (edges, textures, faces)
- **Speed:** Depends on image size and GPU (~5–30 seconds)

### How scales map to model runs

| Scale | AI Strategy | Pipeline |
|-------|------------|----------|
| 2x    | 4x Real-ESRGAN → Lanczos downscale to 2x | Runs model once, then shrinks |
| 4x    | 4x Real-ESRGAN direct | Single model pass |
| 8x    | 4x Real-ESRGAN → 4x Real-ESRGAN → Lanczos downscale to 8x | Two model passes |

---

## 5. Integration Examples

### Example 1: File Input → Download

```html
<input type="file" id="photo" accept="image/*" />
<select id="scale">
  <option value="2">2x</option>
  <option value="4" selected>4x</option>
  <option value="8">8x</option>
</select>
<button id="go">Upscale & Download</button>
<progress id="bar" max="100" value="0" style="display:none"></progress>

<script type="module">
  import { ImageUpscaler } from './image-upscaler.js';
  const upscaler = new ImageUpscaler();

  document.getElementById('go').addEventListener('click', async () => {
    const file = document.getElementById('photo').files[0];
    if (!file) return alert('Pick an image first');

    const scale = parseInt(document.getElementById('scale').value);
    const bar = document.getElementById('bar');
    bar.style.display = 'block';

    await upscaler.upscaleAndDownload(file, {
      scale,
      dpi: 300,
      onProgress: (pct) => bar.value = pct
    });

    bar.style.display = 'none';
  });
</script>
```

---

### Example 2: Canvas → Blob (for further processing)

```js
import { ImageUpscaler } from './image-upscaler.js';

const upscaler = new ImageUpscaler();
const canvas = document.getElementById('myCanvas');

// Upscale to 2x, get blob for upload
const blob = await upscaler.upscale(canvas, {
  scale: 2,
  mode: 'lanczos',
  format: 'image/jpeg',
  quality: 0.9,
  dpi: 0  // skip DPI metadata for JPEG
});

// Upload to server
const form = new FormData();
form.append('image', blob, 'upscaled.jpg');
await fetch('/api/upload', { method: 'POST', body: form });
```

---

### Example 3: Image URL → Preview

```js
import { ImageUpscaler } from './image-upscaler.js';

const upscaler = new ImageUpscaler();

// Show expected output size before upscaling
const { width, height } = await ImageUpscaler.getSize('/photos/portrait.jpg');
const target = ImageUpscaler.previewSize(width, height, 4);
document.getElementById('info').textContent =
  `Will upscale from ${width}x${height} → ${target.width}x${target.height}`;

// Then upscale
const blob = await upscaler.upscale('/photos/portrait.jpg', { scale: 4 });
const url = URL.createObjectURL(blob);
document.getElementById('preview').src = url;
```

---

### Example 4: AI Mode with Real-ESRGAN

```html
<!-- Load ONNX Runtime Web from CDN -->
<script src="https://cdn.jsdelivr.net/npm/onnxruntime-web@1.17.0/dist/ort.min.js"></script>

<script type="module">
  import { ImageUpscaler } from './image-upscaler.js';

  const upscaler = new ImageUpscaler();

  // Load the model (one-time, ~64MB download)
  const status = document.getElementById('status');
  status.textContent = 'Loading AI model...';
  await upscaler.loadModel('/models/realesrgan-x4plus.onnx');
  status.textContent = 'Model ready';

  document.getElementById('enhance').addEventListener('click', async () => {
    const file = document.getElementById('photo').files[0];
    status.textContent = 'Enhancing...';

    const blob = await upscaler.upscale(file, {
      scale: 4,
      mode: 'ai',
      onProgress: (pct) => status.textContent = `Enhancing... ${pct}%`
    });

    ImageUpscaler.download(blob, 'ai_enhanced_4x.png');
    status.textContent = 'Done!';
  });
</script>
```

---

### Example 5: Blazor / C# Interop

```html
<!-- In _Host.cshtml or index.html -->
<script src="js/image-upscaler.js" type="module"></script>
```

```js
// In a separate interop JS file
window.upscaleImage = async function (dataUrl, scale, format) {
  const upscaler = new ImageUpscaler();
  const blob = await upscaler.upscale(dataUrl, {
    scale: scale,
    mode: 'lanczos',
    format: format === 'jpg' ? 'image/jpeg' : 'image/png',
    dpi: 300
  });
  ImageUpscaler.download(blob, `upscaled_${scale}x.${format}`);
};
```

```csharp
// In Blazor component
await JS.InvokeVoidAsync("upscaleImage", imageDataUrl, 4, "png");
```

---

### Example 6: React Component

```jsx
import { useRef, useState } from 'react';
import { ImageUpscaler } from './image-upscaler.js';

const upscaler = new ImageUpscaler();

export function UpscaleButton({ imageUrl, scale = 4 }) {
  const [progress, setProgress] = useState(0);
  const [busy, setBusy] = useState(false);

  const handleClick = async () => {
    setBusy(true);
    setProgress(0);
    await upscaler.upscaleAndDownload(imageUrl, {
      scale,
      onProgress: setProgress
    });
    setBusy(false);
  };

  return (
    <button onClick={handleClick} disabled={busy}>
      {busy ? `${progress}%` : `Download ${scale}x`}
    </button>
  );
}
```

---

## 6. AI Mode Setup (Real-ESRGAN)

### Step 1: Add ONNX Runtime Web

```html
<script src="https://cdn.jsdelivr.net/npm/onnxruntime-web@1.17.0/dist/ort.min.js"></script>
```

Or install via npm:

```bash
npm install onnxruntime-web
```

### Step 2: Get the ONNX Model

The Real-ESRGAN x4plus model (~64MB) can be obtained from:

- ArtForge AI ships it at `ArtForgeAI/Models/realesrgan-x4plus.onnx`
- Convert from PyTorch using the [Real-ESRGAN repo](https://github.com/xinntao/Real-ESRGAN)

Place it in your web-accessible static files:

```
your-project/
├── models/
│   └── realesrgan-x4plus.onnx
└── ...
```

### Step 3: Serve with Correct Headers

Your web server must serve `.onnx` files with proper MIME type and allow large responses:

**Nginx:**
```nginx
location /models/ {
    types { application/octet-stream onnx; }
    add_header Cache-Control "public, max-age=31536000";
}
```

**Express.js:**
```js
app.use('/models', express.static('models', {
  setHeaders: (res) => res.set('Cache-Control', 'public, max-age=31536000')
}));
```

**ASP.NET (Program.cs):**
```csharp
var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".onnx"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = provider });
```

---

## 7. Architecture

```
┌─────────────────────────────────────────────────────┐
│                  ImageUpscaler                       │
│                                                     │
│  upscale(source, options) ──┬── mode: 'lanczos'     │
│                             │   └─ lanczosResample() │
│                             │      Two-pass Lanczos3 │
│                             │      kernel (CPU)      │
│                             │                        │
│                             └── mode: 'ai'           │
│                                 └─ AiUpscaler        │
│                                    ├─ Tile grid      │
│                                    ├─ Pad overlap     │
│                                    ├─ NCHW tensor     │
│                                    ├─ ONNX inference  │
│                                    └─ Stitch output   │
│                                                      │
│  Output ────────────────────── canvasToBlob()         │
│                                 └─ embedDpiInPng()    │
│                                    PNG pHYs chunk     │
└─────────────────────────────────────────────────────┘
```

### Tiled Inference (AI Mode)

Large images are split into 192x192 tiles with 10px overlap padding to prevent seam artifacts. Each tile is:

1. Extracted from source with padding
2. Converted to NCHW float32 tensor (normalized 0–1)
3. Fed through Real-ESRGAN (outputs 4x resolution)
4. The padded border is trimmed
5. The result is stitched into the output canvas

This mirrors the tiling strategy in ArtForge's C# `OnnxImageEnhancerService`.

### DPI Embedding

PNG files get a `pHYs` chunk injected after the IHDR chunk, encoding pixels-per-metre at the requested DPI. This makes the file print-ready — image viewers and print software will read the correct physical size.

---

## 8. Performance & Limits

| Image Size | Lanczos 4x | AI 4x (WebGL) | AI 4x (WASM) |
|-----------|-----------|---------------|---------------|
| 256x256   | ~50ms     | ~2s           | ~8s           |
| 512x512   | ~200ms    | ~5s           | ~25s          |
| 1024x1024 | ~800ms    | ~15s          | ~60s          |
| 2048x2048 | ~3s       | ~50s          | ~4min         |

### Memory considerations

- **maxDim** (default 16384) prevents canvas OOM crashes
- 8x AI mode on large images creates very large intermediate canvases — consider capping input size
- For images > 2000px, Lanczos mode is recommended unless AI detail recovery is critical

### Browser support

- **Lanczos mode:** All modern browsers (Chrome 60+, Firefox 55+, Safari 11+, Edge 79+)
- **AI mode:** Requires ONNX Runtime Web support (Chrome 80+, Firefox 78+, Edge 80+)

---

## 9. Troubleshooting

### "ONNX Runtime Web not found"

Add the ONNX Runtime Web script before importing the upscaler:

```html
<script src="https://cdn.jsdelivr.net/npm/onnxruntime-web@1.17.0/dist/ort.min.js"></script>
```

### "Scale must be 2, 4, or 8"

Only integer scales of 2, 4, and 8 are supported. For arbitrary scaling, upscale to the next power and use CSS or canvas to resize.

### Model file fails to load (404 / CORS)

- Verify the `.onnx` file is in a web-accessible directory
- Check your server serves `.onnx` with `application/octet-stream` MIME type
- For cross-origin, add `Access-Control-Allow-Origin` headers

### Canvas size exceeded / OOM

Reduce `maxDim` in options:

```js
await upscaler.upscale(image, { scale: 8, maxDim: 8192 });
```

### Blank or black output

Ensure the source image is fully loaded before passing it. For `<img>` elements, wait for `onload`. The module handles this automatically for `Blob` and URL sources.

### Slow AI inference

- Prefer **WebGL** execution provider (default) over WASM
- Reduce input image size before AI upscaling
- Use Lanczos mode for images that are already sharp
