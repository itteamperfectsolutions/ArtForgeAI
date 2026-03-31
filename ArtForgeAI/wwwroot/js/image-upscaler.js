/**
 * image-upscaler.js — Standalone image upscaler module (2x, 4x, 8x)
 *
 * Two modes:
 *   1. AI upscale  – Real-ESRGAN 4x via ONNX Runtime Web (tiled inference)
 *   2. Lanczos     – High-quality canvas resampling (no model needed)
 *
 * Usage:
 *   import { ImageUpscaler } from './image-upscaler.js';
 *
 *   const upscaler = new ImageUpscaler();
 *   // Optional: load ONNX model for AI mode
 *   await upscaler.loadModel('/models/realesrgan-x4plus.onnx');
 *
 *   // Upscale from image/canvas/blob
 *   const resultBlob = await upscaler.upscale(sourceImage, {
 *     scale: 4,               // 2 | 4 | 8
 *     mode: 'ai',             // 'ai' | 'lanczos' (default: 'ai' if model loaded)
 *     format: 'image/png',    // output format
 *     quality: 0.95,          // JPEG quality (0-1)
 *     dpi: 300,               // embeds DPI in PNG chunks
 *     onProgress: (pct) => {} // 0-100
 *   });
 *
 *   // Helper: trigger download
 *   ImageUpscaler.download(resultBlob, 'upscaled_4x.png');
 */

// ─── Lanczos kernel ───────────────────────────────────────────────
function lanczosKernel(x, a) {
    if (x === 0) return 1;
    if (Math.abs(x) >= a) return 0;
    const pi_x = Math.PI * x;
    return (a * Math.sin(pi_x) * Math.sin(pi_x / a)) / (pi_x * pi_x);
}

function lanczosResample(srcData, srcW, srcH, dstW, dstH) {
    const a = 3; // Lanczos3
    const dst = new Uint8ClampedArray(dstW * dstH * 4);
    const xRatio = srcW / dstW;
    const yRatio = srcH / dstH;

    // Horizontal pass → temp buffer
    const tmp = new Float32Array(dstW * srcH * 4);
    for (let y = 0; y < srcH; y++) {
        for (let x = 0; x < dstW; x++) {
            const cx = (x + 0.5) * xRatio - 0.5;
            const left = Math.ceil(cx - a);
            const right = Math.floor(cx + a);
            let r = 0, g = 0, b = 0, alpha = 0, wSum = 0;
            for (let ix = left; ix <= right; ix++) {
                const sx = Math.min(Math.max(ix, 0), srcW - 1);
                const w = lanczosKernel(cx - ix, a);
                const off = (y * srcW + sx) * 4;
                r += srcData[off] * w;
                g += srcData[off + 1] * w;
                b += srcData[off + 2] * w;
                alpha += srcData[off + 3] * w;
                wSum += w;
            }
            const off = (y * dstW + x) * 4;
            tmp[off] = r / wSum;
            tmp[off + 1] = g / wSum;
            tmp[off + 2] = b / wSum;
            tmp[off + 3] = alpha / wSum;
        }
    }

    // Vertical pass → final
    for (let x = 0; x < dstW; x++) {
        for (let y = 0; y < dstH; y++) {
            const cy = (y + 0.5) * yRatio - 0.5;
            const top = Math.ceil(cy - a);
            const bottom = Math.floor(cy + a);
            let r = 0, g = 0, b = 0, alpha = 0, wSum = 0;
            for (let iy = top; iy <= bottom; iy++) {
                const sy = Math.min(Math.max(iy, 0), srcH - 1);
                const w = lanczosKernel(cy - iy, a);
                const off = (sy * dstW + x) * 4;
                r += tmp[off] * w;
                g += tmp[off + 1] * w;
                b += tmp[off + 2] * w;
                alpha += tmp[off + 3] * w;
                wSum += w;
            }
            const off = (y * dstW + x) * 4;
            dst[off] = Math.min(255, Math.max(0, Math.round(r / wSum)));
            dst[off + 1] = Math.min(255, Math.max(0, Math.round(g / wSum)));
            dst[off + 2] = Math.min(255, Math.max(0, Math.round(b / wSum)));
            dst[off + 3] = Math.min(255, Math.max(0, Math.round(alpha / wSum)));
        }
    }
    return dst;
}

// ─── DPI embedding (PNG pHYs chunk) ──────────────────────────────
function embedDpiInPng(pngArrayBuffer, dpi) {
    const ppm = Math.round(dpi / 0.0254); // pixels per metre
    const src = new Uint8Array(pngArrayBuffer);
    // Build pHYs chunk: 4 bytes X-ppm + 4 bytes Y-ppm + 1 byte unit(1=metre)
    const physData = new Uint8Array(9);
    const dv = new DataView(physData.buffer);
    dv.setUint32(0, ppm);
    dv.setUint32(4, ppm);
    physData[8] = 1; // unit = metre

    // CRC32 for pHYs
    const crcBuf = new Uint8Array(4 + 9);
    crcBuf.set([0x70, 0x48, 0x59, 0x73]); // "pHYs"
    crcBuf.set(physData, 4);
    const crc = crc32(crcBuf);

    // Full chunk: length(4) + type(4) + data(9) + crc(4) = 21 bytes
    const chunk = new Uint8Array(21);
    const chunkDv = new DataView(chunk.buffer);
    chunkDv.setUint32(0, 9);                      // length
    chunk.set([0x70, 0x48, 0x59, 0x73], 4);       // "pHYs"
    chunk.set(physData, 8);                        // data
    chunkDv.setUint32(17, crc);                    // CRC

    // Insert after IHDR (PNG sig 8 bytes + IHDR chunk: len4 + type4 + data13 + crc4 = 33)
    const insertAt = 8 + 25; // 8 (sig) + 4+4+13+4 (IHDR)
    const result = new Uint8Array(src.length + 21);
    result.set(src.subarray(0, insertAt));
    result.set(chunk, insertAt);
    result.set(src.subarray(insertAt), insertAt + 21);
    return result.buffer;
}

// CRC32 (PNG-compatible)
const crcTable = (() => {
    const t = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
        let c = n;
        for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
        t[n] = c;
    }
    return t;
})();

function crc32(buf) {
    let c = 0xFFFFFFFF;
    for (let i = 0; i < buf.length; i++) c = crcTable[(c ^ buf[i]) & 0xFF] ^ (c >>> 8);
    return (c ^ 0xFFFFFFFF) >>> 0;
}

// ─── Helpers ─────────────────────────────────────────────────────
function toCanvas(source) {
    return new Promise((resolve, reject) => {
        if (source instanceof HTMLCanvasElement) {
            resolve(source);
            return;
        }
        if (source instanceof OffscreenCanvas) {
            const c = document.createElement('canvas');
            c.width = source.width;
            c.height = source.height;
            c.getContext('2d').drawImage(source, 0, 0);
            resolve(c);
            return;
        }
        if (source instanceof HTMLImageElement) {
            if (source.complete && source.naturalWidth) {
                const c = document.createElement('canvas');
                c.width = source.naturalWidth;
                c.height = source.naturalHeight;
                c.getContext('2d').drawImage(source, 0, 0);
                resolve(c);
            } else {
                source.onload = () => {
                    const c = document.createElement('canvas');
                    c.width = source.naturalWidth;
                    c.height = source.naturalHeight;
                    c.getContext('2d').drawImage(source, 0, 0);
                    resolve(c);
                };
                source.onerror = reject;
            }
            return;
        }
        if (source instanceof Blob) {
            const img = new Image();
            img.onload = () => {
                const c = document.createElement('canvas');
                c.width = img.naturalWidth;
                c.height = img.naturalHeight;
                c.getContext('2d').drawImage(img, 0, 0);
                URL.revokeObjectURL(img.src);
                resolve(c);
            };
            img.onerror = reject;
            img.src = URL.createObjectURL(source);
            return;
        }
        if (typeof source === 'string') {
            // data URL or path
            const img = new Image();
            img.crossOrigin = 'anonymous';
            img.onload = () => {
                const c = document.createElement('canvas');
                c.width = img.naturalWidth;
                c.height = img.naturalHeight;
                c.getContext('2d').drawImage(img, 0, 0);
                resolve(c);
            };
            img.onerror = reject;
            img.src = source;
            return;
        }
        reject(new Error('Unsupported source type'));
    });
}

function canvasToBlob(canvas, format, quality) {
    return new Promise((resolve, reject) => {
        canvas.toBlob(
            blob => blob ? resolve(blob) : reject(new Error('toBlob failed')),
            format,
            quality
        );
    });
}

// ─── AI Upscaler (ONNX Runtime Web + Real-ESRGAN) ───────────────
const AI_SCALE = 4;     // Real-ESRGAN is a fixed 4x model
const TILE_SIZE = 192;
const TILE_PAD = 10;

class AiUpscaler {
    constructor() {
        this.session = null;
        this.inputName = null;
        this.outputName = null;
    }

    get isLoaded() { return this.session !== null; }

    async load(modelPath, ortOptions) {
        if (typeof ort === 'undefined') {
            throw new Error(
                'ONNX Runtime Web not found. Add <script src="https://cdn.jsdelivr.net/npm/onnxruntime-web/dist/ort.min.js"></script> before using AI mode.'
            );
        }
        const opts = new ort.InferenceSession.SessionOptions?.() ?? {};
        this.session = await ort.InferenceSession.create(modelPath, {
            executionProviders: ['webgl', 'wasm'],
            ...ortOptions
        });
        this.inputName = this.session.inputNames[0];
        this.outputName = this.session.outputNames[0];
    }

    async inferenceTile(rgbFloat32, tileW, tileH) {
        const tensor = new ort.Tensor('float32', rgbFloat32, [1, 3, tileH, tileW]);
        const feeds = { [this.inputName]: tensor };
        const results = await this.session.run(feeds);
        const output = results[this.outputName];
        return { data: output.data, width: tileW * AI_SCALE, height: tileH * AI_SCALE };
    }

    async upscale4x(canvas, onProgress) {
        const srcW = canvas.width;
        const srcH = canvas.height;
        const ctx = canvas.getContext('2d');
        const imgData = ctx.getImageData(0, 0, srcW, srcH);
        const pixels = imgData.data;

        const outW = srcW * AI_SCALE;
        const outH = srcH * AI_SCALE;
        const outCanvas = document.createElement('canvas');
        outCanvas.width = outW;
        outCanvas.height = outH;
        const outCtx = outCanvas.getContext('2d');
        const outImgData = outCtx.createImageData(outW, outH);
        const outPixels = outImgData.data;

        const tilesX = Math.ceil(srcW / TILE_SIZE);
        const tilesY = Math.ceil(srcH / TILE_SIZE);
        const totalTiles = tilesX * tilesY;
        let processed = 0;

        for (let ty = 0; ty < tilesY; ty++) {
            for (let tx = 0; tx < tilesX; tx++) {
                const srcX = tx * TILE_SIZE;
                const srcY = ty * TILE_SIZE;
                const tileW = Math.min(TILE_SIZE, srcW - srcX);
                const tileH = Math.min(TILE_SIZE, srcH - srcY);

                const padLeft = Math.min(TILE_PAD, srcX);
                const padTop = Math.min(TILE_PAD, srcY);
                const padRight = Math.min(TILE_PAD, srcW - srcX - tileW);
                const padBottom = Math.min(TILE_PAD, srcH - srcY - tileH);

                const cropX = srcX - padLeft;
                const cropY = srcY - padTop;
                const cropW = tileW + padLeft + padRight;
                const cropH = tileH + padTop + padBottom;

                // Extract tile as RGB float32 (NCHW layout)
                const rgbFloat = new Float32Array(3 * cropH * cropW);
                for (let y = 0; y < cropH; y++) {
                    for (let x = 0; x < cropW; x++) {
                        const srcOff = ((cropY + y) * srcW + (cropX + x)) * 4;
                        const idx = y * cropW + x;
                        rgbFloat[idx] = pixels[srcOff] / 255;                     // R
                        rgbFloat[cropH * cropW + idx] = pixels[srcOff + 1] / 255;  // G
                        rgbFloat[2 * cropH * cropW + idx] = pixels[srcOff + 2] / 255; // B
                    }
                }

                const result = await this.inferenceTile(rgbFloat, cropW, cropH);

                // Write non-padded region to output
                const outPadLeft = padLeft * AI_SCALE;
                const outPadTop = padTop * AI_SCALE;
                const outTileW = tileW * AI_SCALE;
                const outTileH = tileH * AI_SCALE;
                const dstX = srcX * AI_SCALE;
                const dstY = srcY * AI_SCALE;
                const fullOutW = cropW * AI_SCALE;

                for (let y = 0; y < outTileH; y++) {
                    for (let x = 0; x < outTileW; x++) {
                        const rIdx = (outPadTop + y) * fullOutW + (outPadLeft + x);
                        const gIdx = result.height * fullOutW + rIdx;
                        const bIdx = 2 * result.height * fullOutW + rIdx;

                        // ONNX output is NCHW: [1, 3, H, W]
                        const plane = fullOutW * result.height;
                        const ri = (outPadTop + y) * fullOutW + (outPadLeft + x);

                        const outOff = ((dstY + y) * outW + (dstX + x)) * 4;
                        outPixels[outOff] = Math.min(255, Math.max(0, Math.round(result.data[ri] * 255)));
                        outPixels[outOff + 1] = Math.min(255, Math.max(0, Math.round(result.data[plane + ri] * 255)));
                        outPixels[outOff + 2] = Math.min(255, Math.max(0, Math.round(result.data[2 * plane + ri] * 255)));
                        outPixels[outOff + 3] = 255;
                    }
                }

                processed++;
                if (onProgress) onProgress(Math.round(processed * 100 / totalTiles));
            }
        }

        outCtx.putImageData(outImgData, 0, 0);
        return outCanvas;
    }
}

// ─── Main ImageUpscaler class ────────────────────────────────────
class ImageUpscaler {
    constructor() {
        this._ai = new AiUpscaler();
    }

    /** Load Real-ESRGAN ONNX model for AI upscaling */
    async loadModel(modelPath, ortOptions) {
        await this._ai.load(modelPath, ortOptions);
    }

    /** True if ONNX model is loaded and ready */
    get modelLoaded() { return this._ai.isLoaded; }

    /**
     * Upscale an image.
     * @param {HTMLImageElement|HTMLCanvasElement|Blob|string} source
     * @param {Object} options
     * @param {number}   options.scale      - 2, 4, or 8 (default 4)
     * @param {string}   options.mode       - 'ai' | 'lanczos' (default: ai if model loaded)
     * @param {string}   options.format     - 'image/png' | 'image/jpeg' (default png)
     * @param {number}   options.quality    - JPEG quality 0-1 (default 0.95)
     * @param {number}   options.dpi        - Embed DPI metadata in PNG (default 300)
     * @param {number}   options.maxDim     - Cap max dimension (default 16384)
     * @param {Function} options.onProgress - Progress callback (0-100)
     * @returns {Promise<Blob>} Upscaled image blob
     */
    async upscale(source, options = {}) {
        const {
            scale = 4,
            mode = this._ai.isLoaded ? 'ai' : 'lanczos',
            format = 'image/png',
            quality = 0.95,
            dpi = 300,
            maxDim = 16384,
            onProgress
        } = options;

        if (![2, 4, 8].includes(scale)) throw new Error('Scale must be 2, 4, or 8');
        if (mode === 'ai' && !this._ai.isLoaded) throw new Error('ONNX model not loaded. Call loadModel() first or use mode: "lanczos".');

        const srcCanvas = await toCanvas(source);
        let resultCanvas;

        if (mode === 'ai') {
            resultCanvas = await this._upscaleAi(srcCanvas, scale, maxDim, onProgress);
        } else {
            resultCanvas = this._upscaleLanczos(srcCanvas, scale, maxDim, onProgress);
        }

        // Convert to blob
        const blob = await canvasToBlob(resultCanvas, format, format === 'image/jpeg' ? quality : undefined);

        // Embed DPI metadata for PNG
        if (dpi && format === 'image/png') {
            const buf = await blob.arrayBuffer();
            const withDpi = embedDpiInPng(buf, dpi);
            return new Blob([withDpi], { type: 'image/png' });
        }

        return blob;
    }

    /**
     * Upscale and immediately download.
     * Same options as upscale(), plus options.fileName.
     */
    async upscaleAndDownload(source, options = {}) {
        const { fileName, ...upscaleOpts } = options;
        const scale = options.scale || 4;
        const ext = (options.format === 'image/jpeg') ? 'jpg' : 'png';
        const name = fileName || `upscaled_${scale}x.${ext}`;
        const blob = await this.upscale(source, upscaleOpts);
        ImageUpscaler.download(blob, name);
        return blob;
    }

    // ── AI path ──────────────────────────────────────────────────
    async _upscaleAi(srcCanvas, scale, maxDim, onProgress) {
        // Real-ESRGAN is fixed 4x. For 2x: do 4x then downscale. For 8x: do 4x twice.
        const reportProgress = (phase, phasePct) => {
            if (!onProgress) return;
            if (scale === 2) {
                onProgress(Math.round(phasePct * 0.9)); // 90% for 4x, 10% for downscale
            } else if (scale === 8) {
                if (phase === 1) onProgress(Math.round(phasePct * 0.5));
                else onProgress(50 + Math.round(phasePct * 0.5));
            } else {
                onProgress(phasePct);
            }
        };

        let result = await this._ai.upscale4x(srcCanvas, pct => reportProgress(1, pct));

        if (scale === 2) {
            // 4x → downscale to 2x using Lanczos
            const targetW = srcCanvas.width * 2;
            const targetH = srcCanvas.height * 2;
            result = this._lanczosResize(result, targetW, targetH);
            if (onProgress) onProgress(100);
        } else if (scale === 8) {
            // 4x → 4x again = 16x → downscale to 8x
            // Cap intermediate if needed
            if (result.width > maxDim || result.height > maxDim) {
                const s = maxDim / Math.max(result.width, result.height);
                result = this._lanczosResize(result, Math.round(result.width * s), Math.round(result.height * s));
            }
            result = await this._ai.upscale4x(result, pct => reportProgress(2, pct));
            // Downscale from 16x to 8x
            const targetW = srcCanvas.width * 8;
            const targetH = srcCanvas.height * 8;
            const cappedW = Math.min(targetW, maxDim);
            const cappedH = Math.min(targetH, maxDim);
            if (result.width !== cappedW || result.height !== cappedH) {
                result = this._lanczosResize(result, cappedW, cappedH);
            }
            if (onProgress) onProgress(100);
        }

        // Apply max dimension cap
        if (result.width > maxDim || result.height > maxDim) {
            const s = maxDim / Math.max(result.width, result.height);
            result = this._lanczosResize(result, Math.round(result.width * s), Math.round(result.height * s));
        }

        return result;
    }

    // ── Lanczos path ─────────────────────────────────────────────
    _upscaleLanczos(srcCanvas, scale, maxDim, onProgress) {
        if (onProgress) onProgress(10);

        let targetW = srcCanvas.width * scale;
        let targetH = srcCanvas.height * scale;

        // Cap max dimension
        if (targetW > maxDim || targetH > maxDim) {
            const s = maxDim / Math.max(targetW, targetH);
            targetW = Math.round(targetW * s);
            targetH = Math.round(targetH * s);
        }

        if (onProgress) onProgress(30);
        const result = this._lanczosResize(srcCanvas, targetW, targetH);
        if (onProgress) onProgress(100);
        return result;
    }

    _lanczosResize(srcCanvas, dstW, dstH) {
        const ctx = srcCanvas.getContext('2d');
        const srcData = ctx.getImageData(0, 0, srcCanvas.width, srcCanvas.height).data;
        const resampled = lanczosResample(srcData, srcCanvas.width, srcCanvas.height, dstW, dstH);

        const out = document.createElement('canvas');
        out.width = dstW;
        out.height = dstH;
        const outCtx = out.getContext('2d');
        const outImgData = outCtx.createImageData(dstW, dstH);
        outImgData.data.set(resampled);
        outCtx.putImageData(outImgData, 0, 0);
        return out;
    }

    // ── Static helpers ───────────────────────────────────────────
    /** Trigger a browser download for a blob */
    static download(blob, fileName) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    /** Get image dimensions without fully decoding */
    static async getSize(source) {
        const c = await toCanvas(source);
        return { width: c.width, height: c.height };
    }

    /** Preview: return target dimensions for a given scale */
    static previewSize(width, height, scale, maxDim = 16384) {
        let w = width * scale;
        let h = height * scale;
        if (w > maxDim || h > maxDim) {
            const s = maxDim / Math.max(w, h);
            w = Math.round(w * s);
            h = Math.round(h * s);
        }
        return { width: w, height: h };
    }
}

// ─── Export ──────────────────────────────────────────────────────
// ES module
export { ImageUpscaler };

// Also expose globally for non-module usage (<script> tag)
if (typeof window !== 'undefined') {
    window.ImageUpscaler = ImageUpscaler;
}
