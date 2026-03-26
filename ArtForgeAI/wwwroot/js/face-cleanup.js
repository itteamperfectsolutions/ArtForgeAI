// ── Local Face Cleanup Module ──
// Professional-grade client-side skin retouching using frequency separation,
// bilateral filtering, and median-based spot healing.
window.faceCleanup = (() => {
    let _sourceCanvas = null;
    let _resultCanvas = null;
    let _originalImageData = null;

    // ───────────────────────── Public API ─────────────────────────

    async function loadImage(dataUrl) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => {
                _sourceCanvas = document.createElement('canvas');
                _sourceCanvas.width = img.width;
                _sourceCanvas.height = img.height;
                const ctx = _sourceCanvas.getContext('2d');
                ctx.drawImage(img, 0, 0);
                _originalImageData = ctx.getImageData(0, 0, img.width, img.height);
                resolve({ width: img.width, height: img.height });
            };
            img.onerror = () => reject('Failed to load image');
            img.src = dataUrl;
        });
    }

    /**
     * Skin smoothing via frequency separation (professional retouching technique).
     * Separates image into low-freq (color/tone) and high-freq (texture/detail),
     * smooths only the low-freq on skin areas, then recombines.
     */
    function applySkinSmoothing(strength) {
        if (!_sourceCanvas) return null;
        const w = _sourceCanvas.width;
        const h = _sourceCanvas.height;
        const srcCtx = _sourceCanvas.getContext('2d');
        const src = srcCtx.getImageData(0, 0, w, h);
        const srcData = src.data;

        const config = {
            light:  { blurRadius: 3, blend: 0.35 },
            medium: { blurRadius: 5, blend: 0.55 },
            heavy:  { blurRadius: 8, blend: 0.75 }
        };
        const { blurRadius, blend } = config[strength] || config.medium;

        // Build skin mask with soft edges (0.0 - 1.0)
        const skinMask = buildSkinMask(srcData, w, h);
        // Feather the mask for smooth transitions
        const feathered = featherMask(skinMask, w, h, Math.max(3, blurRadius));

        // Create low-frequency layer via Gaussian blur
        const lowFreq = gaussianBlur(srcData, w, h, blurRadius);

        // High frequency = original - lowFreq + 128 (neutral grey)
        // We'll recombine as: result = lowFreq_smoothed + (original - lowFreq)
        // On skin areas, use smoothed lowFreq; off skin, keep original.

        // Apply bilateral filter on low-freq for better edge preservation
        const smoothedLow = bilateralFilter(lowFreq, w, h,
            Math.max(2, Math.floor(blurRadius * 0.7)), 25, 50);

        _resultCanvas = document.createElement('canvas');
        _resultCanvas.width = w;
        _resultCanvas.height = h;
        const ctx = _resultCanvas.getContext('2d');
        const result = ctx.createImageData(w, h);
        const out = result.data;

        for (let i = 0; i < srcData.length; i += 4) {
            const px = i >> 2;
            const skinFactor = feathered[px] * blend;

            // High frequency detail = original - lowFreq
            const hiR = srcData[i]     - lowFreq[i];
            const hiG = srcData[i + 1] - lowFreq[i + 1];
            const hiB = srcData[i + 2] - lowFreq[i + 2];

            // Recombine: smoothedLow + highFreq on skin, original elsewhere
            const newR = smoothedLow[i]     + hiR;
            const newG = smoothedLow[i + 1] + hiG;
            const newB = smoothedLow[i + 2] + hiB;

            out[i]     = clamp(srcData[i]     + (newR - srcData[i])     * skinFactor);
            out[i + 1] = clamp(srcData[i + 1] + (newG - srcData[i + 1]) * skinFactor);
            out[i + 2] = clamp(srcData[i + 2] + (newB - srcData[i + 2]) * skinFactor);
            out[i + 3] = srcData[i + 3];
        }

        ctx.putImageData(result, 0, 0);
        return _resultCanvas.toDataURL('image/png');
    }

    /**
     * Blemish removal using local variance detection + median inpainting.
     * Detects anomalous dark/red spots on skin and replaces with
     * weighted median of surrounding clean pixels.
     */
    function removeBlemishes(intensity) {
        if (!_sourceCanvas) return null;
        const w = _sourceCanvas.width;
        const h = _sourceCanvas.height;
        const srcCtx = _sourceCanvas.getContext('2d');
        const src = srcCtx.getImageData(0, 0, w, h);
        const srcData = src.data;

        const config = {
            gentle:     { lumThresh: 30, redThresh: 1.15, varThresh: 400, patchR: 4, blendBack: 0.25 },
            moderate:   { lumThresh: 20, redThresh: 1.10, varThresh: 300, patchR: 6, blendBack: 0.15 },
            aggressive: { lumThresh: 12, redThresh: 1.05, varThresh: 200, patchR: 8, blendBack: 0.10 }
        };
        const { lumThresh, redThresh, varThresh, patchR, blendBack } = config[intensity] || config.moderate;

        // Build skin mask
        const skinMask = buildSkinMask(srcData, w, h);

        // Luminance
        const lum = new Float32Array(w * h);
        for (let y = 0; y < h; y++)
            for (let x = 0; x < w; x++) {
                const i = (y * w + x) * 4;
                lum[y * w + x] = 0.299 * srcData[i] + 0.587 * srcData[i+1] + 0.114 * srcData[i+2];
            }

        // Local stats via integral images
        const localAvg = integralAverage(lum, w, h, patchR * 3);
        const localVar = integralVariance(lum, w, h, patchR * 2);

        // Detect blemishes: skin pixels that are darker than local avg OR
        // have reddish tint (acne) OR have high local variance (texture anomaly)
        const blemishStrength = new Float32Array(w * h); // 0-1 strength
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const px = y * w + x;
                if (skinMask[px] < 0.3) continue;

                const i = px * 4;
                const r = srcData[i], g = srcData[i+1], b = srcData[i+2];
                const l = lum[px];
                let score = 0;

                // Darker than surroundings
                const lumDiff = localAvg[px] - l;
                if (lumDiff > lumThresh) {
                    score += Math.min(1.0, lumDiff / (lumThresh * 2.5));
                }

                // Reddish hue (acne/inflammation)
                const avgGB = (g + b) / 2;
                if (avgGB > 0 && r / avgGB > redThresh && r > 60) {
                    score += Math.min(0.6, (r / avgGB - redThresh) * 2);
                }

                // High local variance on skin = texture anomaly
                if (localVar[px] > varThresh) {
                    score += Math.min(0.4, (localVar[px] - varThresh) / (varThresh * 3));
                }

                blemishStrength[px] = Math.min(1.0, score) * skinMask[px];
            }
        }

        // Feather the blemish mask for smooth blending
        const featheredBlemish = featherMask(blemishStrength, w, h, 2);

        // Inpaint: for each blemish pixel, use weighted median of clean neighbors
        _resultCanvas = document.createElement('canvas');
        _resultCanvas.width = w;
        _resultCanvas.height = h;
        const ctx = _resultCanvas.getContext('2d');
        const result = ctx.createImageData(w, h);
        const out = result.data;

        // Copy original first
        out.set(srcData);

        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const px = y * w + x;
                const strength = featheredBlemish[px];
                if (strength < 0.05) continue;

                // Collect clean neighbor pixels (weighted by distance)
                const samples = [];
                const searchR = patchR + 2;
                for (let dy = -searchR; dy <= searchR; dy++) {
                    for (let dx = -searchR; dx <= searchR; dx++) {
                        const nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        const npx = ny * w + nx;
                        // Skip blemish pixels as sources
                        if (featheredBlemish[npx] > 0.3) continue;
                        const dist = Math.sqrt(dx * dx + dy * dy);
                        if (dist > searchR) continue;
                        const weight = 1.0 / (1.0 + dist);
                        const ni = npx * 4;
                        samples.push({ r: srcData[ni], g: srcData[ni+1], b: srcData[ni+2], w: weight });
                    }
                }

                if (samples.length < 3) continue;

                // Weighted median for each channel
                const medR = weightedMedian(samples.map(s => ({ v: s.r, w: s.w })));
                const medG = weightedMedian(samples.map(s => ({ v: s.g, w: s.w })));
                const medB = weightedMedian(samples.map(s => ({ v: s.b, w: s.w })));

                const i = px * 4;
                const s = strength * (1.0 - blendBack);
                out[i]     = clamp(srcData[i]     + (medR - srcData[i])     * s);
                out[i + 1] = clamp(srcData[i + 1] + (medG - srcData[i + 1]) * s);
                out[i + 2] = clamp(srcData[i + 2] + (medB - srcData[i + 2]) * s);
            }
        }

        ctx.putImageData(result, 0, 0);
        return _resultCanvas.toDataURL('image/png');
    }

    /**
     * Full cleanup: blemish removal then frequency-separated smoothing.
     */
    function clearAllMarks(level) {
        if (!_sourceCanvas) return null;
        const w = _sourceCanvas.width;
        const h = _sourceCanvas.height;

        // Step 1: Remove blemishes
        const blemishLevel = { light: 'gentle', medium: 'moderate', heavy: 'aggressive' };
        removeBlemishes(blemishLevel[level] || 'moderate');

        if (!_resultCanvas) return null;

        // Use blemish-removed result as source for smoothing
        const savedSource = _sourceCanvas;
        _sourceCanvas = document.createElement('canvas');
        _sourceCanvas.width = w;
        _sourceCanvas.height = h;
        _sourceCanvas.getContext('2d').drawImage(_resultCanvas, 0, 0);

        // Step 2: Skin smoothing on clean image
        const finalResult = applySkinSmoothing(level);

        // Restore original source
        _sourceCanvas = savedSource;
        return finalResult;
    }

    function resetToOriginal() {
        if (!_originalImageData || !_sourceCanvas) return null;
        const ctx = _sourceCanvas.getContext('2d');
        ctx.putImageData(_originalImageData, 0, 0);
        return _sourceCanvas.toDataURL('image/png');
    }

    function getResultDataUrl() {
        if (_resultCanvas) return _resultCanvas.toDataURL('image/png');
        if (_sourceCanvas) return _sourceCanvas.toDataURL('image/png');
        return null;
    }

    function commitResult() {
        if (!_resultCanvas) return false;
        _sourceCanvas = document.createElement('canvas');
        _sourceCanvas.width = _resultCanvas.width;
        _sourceCanvas.height = _resultCanvas.height;
        _sourceCanvas.getContext('2d').drawImage(_resultCanvas, 0, 0);
        return true;
    }

    // ───────────────────────── Core Algorithms ─────────────────────────

    /**
     * Skin detection returning a soft mask (0.0 - 1.0).
     * Uses YCbCr + HSV dual-space detection for broad skin tone coverage.
     */
    function buildSkinMask(data, w, h) {
        const mask = new Float32Array(w * h);
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const i = (y * w + x) * 4;
                const r = data[i], g = data[i+1], b = data[i+2];
                mask[y * w + x] = skinProbability(r, g, b);
            }
        }
        return mask;
    }

    /**
     * Returns 0.0-1.0 probability that pixel is skin.
     * Combines YCbCr range check + HSV hue check for robustness.
     */
    function skinProbability(r, g, b) {
        // YCbCr skin detection (widely used, works across ethnicities)
        const y  = 0.299 * r + 0.587 * g + 0.114 * b;
        const cb = 128 - 0.169 * r - 0.331 * g + 0.500 * b;
        const cr = 128 + 0.500 * r - 0.419 * g - 0.081 * b;

        // Relaxed YCbCr ranges for diverse skin tones
        let ycbcrScore = 0;
        if (y > 40 && y < 240 && cb > 77 && cb < 135 && cr > 130 && cr < 175) {
            // Core range — high confidence
            ycbcrScore = 1.0;
        } else if (y > 30 && y < 250 && cb > 70 && cb < 145 && cr > 120 && cr < 185) {
            // Extended range — medium confidence
            ycbcrScore = 0.5;
        }

        // HSV hue check as secondary signal
        const max = Math.max(r, g, b), min = Math.min(r, g, b);
        const diff = max - min;
        let hsvScore = 0;
        if (diff > 10 && max > 40) {
            let hue = 0;
            if (max === r) hue = ((g - b) / diff) % 6;
            else if (max === g) hue = (b - r) / diff + 2;
            else hue = (r - g) / diff + 4;
            hue *= 60;
            if (hue < 0) hue += 360;
            // Skin hues: roughly 0-50 degrees (red-orange-yellow)
            if (hue < 50 || hue > 340) hsvScore = 0.8;
            else if (hue < 70) hsvScore = 0.3;
        }

        // Combined score (YCbCr is primary, HSV is secondary)
        const combined = ycbcrScore * 0.7 + hsvScore * 0.3;
        return Math.min(1.0, combined);
    }

    /**
     * Feather/blur a float mask for smooth transitions.
     */
    function featherMask(mask, w, h, radius) {
        const out = new Float32Array(w * h);
        // Separable box blur on the mask
        const temp = new Float32Array(w * h);

        // Horizontal
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                let sum = 0, count = 0;
                const x0 = Math.max(0, x - radius), x1 = Math.min(w - 1, x + radius);
                for (let xx = x0; xx <= x1; xx++) {
                    sum += mask[y * w + xx];
                    count++;
                }
                temp[y * w + x] = sum / count;
            }
        }
        // Vertical
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                let sum = 0, count = 0;
                const y0 = Math.max(0, y - radius), y1 = Math.min(h - 1, y + radius);
                for (let yy = y0; yy <= y1; yy++) {
                    sum += temp[yy * w + x];
                    count++;
                }
                out[y * w + x] = sum / count;
            }
        }
        return out;
    }

    /**
     * Gaussian blur via 3-pass box blur (Box-Muller approximation).
     * Much smoother than single-pass box blur.
     */
    function gaussianBlur(data, w, h, radius) {
        let buf = new Uint8ClampedArray(data);
        // 3 passes of box blur ≈ Gaussian
        for (let pass = 0; pass < 3; pass++) {
            buf = boxBlurPass(buf, w, h, radius);
        }
        return buf;
    }

    function boxBlurPass(data, w, h, radius) {
        const out = new Uint8ClampedArray(data.length);
        const temp = new Uint8ClampedArray(data.length);

        // Horizontal using running sum
        for (let y = 0; y < h; y++) {
            for (let ch = 0; ch < 3; ch++) {
                let sum = 0, count = 0;
                // Initialize window
                for (let x = 0; x <= Math.min(radius, w - 1); x++) {
                    sum += data[(y * w + x) * 4 + ch];
                    count++;
                }
                for (let x = 0; x < w; x++) {
                    // Add right edge
                    const addX = x + radius;
                    if (addX < w && addX > radius) {
                        sum += data[(y * w + addX) * 4 + ch];
                        count++;
                    }
                    // Remove left edge
                    const remX = x - radius - 1;
                    if (remX >= 0) {
                        sum -= data[(y * w + remX) * 4 + ch];
                        count--;
                    }
                    temp[(y * w + x) * 4 + ch] = Math.round(sum / count);
                }
            }
            // Copy alpha
            for (let x = 0; x < w; x++)
                temp[(y * w + x) * 4 + 3] = data[(y * w + x) * 4 + 3];
        }

        // Vertical using running sum
        for (let x = 0; x < w; x++) {
            for (let ch = 0; ch < 3; ch++) {
                let sum = 0, count = 0;
                for (let y = 0; y <= Math.min(radius, h - 1); y++) {
                    sum += temp[(y * w + x) * 4 + ch];
                    count++;
                }
                for (let y = 0; y < h; y++) {
                    const addY = y + radius;
                    if (addY < h && addY > radius) {
                        sum += temp[(addY * w + x) * 4 + ch];
                        count++;
                    }
                    const remY = y - radius - 1;
                    if (remY >= 0) {
                        sum -= temp[(remY * w + x) * 4 + ch];
                        count--;
                    }
                    out[(y * w + x) * 4 + ch] = Math.round(sum / count);
                }
            }
            for (let y = 0; y < h; y++)
                out[(y * w + x) * 4 + 3] = temp[(y * w + x) * 4 + 3];
        }

        return out;
    }

    /**
     * Bilateral filter — smooths while preserving edges.
     * Uses spatial + range (color similarity) weighting.
     */
    function bilateralFilter(data, w, h, radius, sigmaSpace, sigmaColor) {
        const out = new Uint8ClampedArray(data.length);
        const invSigmaSpace2 = -1 / (2 * sigmaSpace * sigmaSpace);
        const invSigmaColor2 = -1 / (2 * sigmaColor * sigmaColor);

        // Pre-compute spatial weights
        const spatialWeights = new Float32Array((radius * 2 + 1) ** 2);
        for (let dy = -radius; dy <= radius; dy++) {
            for (let dx = -radius; dx <= radius; dx++) {
                const idx = (dy + radius) * (radius * 2 + 1) + (dx + radius);
                spatialWeights[idx] = Math.exp((dx * dx + dy * dy) * invSigmaSpace2);
            }
        }

        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const ci = (y * w + x) * 4;
                const cr = data[ci], cg = data[ci+1], cb = data[ci+2];
                let sumR = 0, sumG = 0, sumB = 0, sumW = 0;

                for (let dy = -radius; dy <= radius; dy++) {
                    const ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (let dx = -radius; dx <= radius; dx++) {
                        const nx = x + dx;
                        if (nx < 0 || nx >= w) continue;

                        const ni = (ny * w + nx) * 4;
                        const dr = data[ni] - cr;
                        const dg = data[ni+1] - cg;
                        const db = data[ni+2] - cb;
                        const colorDist2 = dr * dr + dg * dg + db * db;

                        const si = (dy + radius) * (radius * 2 + 1) + (dx + radius);
                        const weight = spatialWeights[si] * Math.exp(colorDist2 * invSigmaColor2);

                        sumR += data[ni]     * weight;
                        sumG += data[ni + 1] * weight;
                        sumB += data[ni + 2] * weight;
                        sumW += weight;
                    }
                }

                out[ci]     = clamp(sumR / sumW);
                out[ci + 1] = clamp(sumG / sumW);
                out[ci + 2] = clamp(sumB / sumW);
                out[ci + 3] = data[ci + 3];
            }
        }
        return out;
    }

    /**
     * Integral-image based local average.
     */
    function integralAverage(lum, w, h, radius) {
        const avg = new Float32Array(w * h);
        const integral = new Float64Array((w + 1) * (h + 1));
        const W1 = w + 1;

        for (let y = 0; y < h; y++)
            for (let x = 0; x < w; x++)
                integral[(y + 1) * W1 + (x + 1)] =
                    lum[y * w + x]
                    + integral[y * W1 + (x + 1)]
                    + integral[(y + 1) * W1 + x]
                    - integral[y * W1 + x];

        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const x0 = Math.max(0, x - radius), x1 = Math.min(w - 1, x + radius);
                const y0 = Math.max(0, y - radius), y1 = Math.min(h - 1, y + radius);
                const sum = integral[(y1+1) * W1 + (x1+1)]
                          - integral[y0 * W1 + (x1+1)]
                          - integral[(y1+1) * W1 + x0]
                          + integral[y0 * W1 + x0];
                avg[y * w + x] = sum / ((x1 - x0 + 1) * (y1 - y0 + 1));
            }
        }
        return avg;
    }

    /**
     * Integral-image based local variance.
     */
    function integralVariance(lum, w, h, radius) {
        const variance = new Float32Array(w * h);
        const W1 = w + 1;

        // Integral of values and squared values
        const intSum = new Float64Array((w + 1) * (h + 1));
        const intSq  = new Float64Array((w + 1) * (h + 1));

        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const v = lum[y * w + x];
                intSum[(y+1) * W1 + (x+1)] = v + intSum[y * W1 + (x+1)] + intSum[(y+1) * W1 + x] - intSum[y * W1 + x];
                intSq[(y+1) * W1 + (x+1)]  = v*v + intSq[y * W1 + (x+1)] + intSq[(y+1) * W1 + x] - intSq[y * W1 + x];
            }
        }

        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const x0 = Math.max(0, x - radius), x1 = Math.min(w - 1, x + radius);
                const y0 = Math.max(0, y - radius), y1 = Math.min(h - 1, y + radius);
                const area = (x1 - x0 + 1) * (y1 - y0 + 1);

                const sum = intSum[(y1+1) * W1 + (x1+1)] - intSum[y0 * W1 + (x1+1)] - intSum[(y1+1) * W1 + x0] + intSum[y0 * W1 + x0];
                const sq  = intSq[(y1+1) * W1 + (x1+1)]  - intSq[y0 * W1 + (x1+1)]  - intSq[(y1+1) * W1 + x0]  + intSq[y0 * W1 + x0];

                const mean = sum / area;
                variance[y * w + x] = Math.max(0, sq / area - mean * mean);
            }
        }
        return variance;
    }

    /**
     * Weighted median — produces much cleaner results than weighted average
     * for inpainting (resists outlier influence).
     */
    function weightedMedian(pairs) {
        // pairs = [{v, w}, ...]
        pairs.sort((a, b) => a.v - b.v);
        let totalW = 0;
        for (const p of pairs) totalW += p.w;
        const half = totalW / 2;
        let cumW = 0;
        for (const p of pairs) {
            cumW += p.w;
            if (cumW >= half) return p.v;
        }
        return pairs[pairs.length - 1].v;
    }

    function clamp(v) {
        return v < 0 ? 0 : v > 255 ? 255 : Math.round(v);
    }

    return {
        loadImage,
        applySkinSmoothing,
        removeBlemishes,
        clearAllMarks,
        resetToOriginal,
        getResultDataUrl,
        commitResult
    };
})();
