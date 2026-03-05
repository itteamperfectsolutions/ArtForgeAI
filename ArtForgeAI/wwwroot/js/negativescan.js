// ===== ArtForge AI - Negative Scan Module =====
window.negativeScan = (function () {
    "use strict";

    // ── State ──
    var cropContainer = null, cropWrapper = null, cropImg = null, cropBoxEl = null;
    var dimEls = {};
    var cropDisplayW = 0, cropDisplayH = 0;
    var cropNaturalW = 0, cropNaturalH = 0;
    var cropState = { x: 0, y: 0, w: 100, h: 100 };
    var dragState = null;

    // No cap — always process at full source resolution for best quality
    var MAX_DIMENSION = 8192;

    // ══════════════════════════════════════════
    // ── DOM-based Interactive Crop Tool (free aspect ratio) ──
    // ══════════════════════════════════════════

    function initCropTool(containerId, dataUrl) {
        disposeCropTool();

        var container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = "";

        var wrapper = document.createElement("div");
        wrapper.style.cssText = "position:relative;display:inline-block;user-select:none;line-height:0;";

        var img = document.createElement("img");
        var maxH = window.innerWidth <= 480 ? 280 : (window.innerWidth <= 900 ? 380 : 500);
        img.style.cssText = "display:block;max-height:" + maxH + "px;max-width:100%;pointer-events:none;";
        img.crossOrigin = "anonymous";

        img.onload = function () {
            cropNaturalW = img.naturalWidth;
            cropNaturalH = img.naturalHeight;
            cropDisplayW = img.clientWidth;
            cropDisplayH = img.clientHeight;

            // Dim overlays
            ["top", "bottom", "left", "right"].forEach(function (side) {
                var d = document.createElement("div");
                d.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;transition:none;";
                wrapper.appendChild(d);
                dimEls[side] = d;
            });

            // Crop box
            cropBoxEl = document.createElement("div");
            cropBoxEl.style.cssText = "position:absolute;border:2px dashed #ff9800;cursor:move;box-sizing:border-box;overflow:hidden;";
            wrapper.appendChild(cropBoxEl);

            // Corner handles
            ["tl", "tr", "bl", "br"].forEach(function (pos) {
                var h = document.createElement("div");
                h.dataset.handle = pos;
                var base = "position:absolute;width:16px;height:16px;background:#ff9800;border:2px solid #fff;box-sizing:border-box;z-index:10;border-radius:2px;";
                if (pos === "tl") h.style.cssText = base + "top:-8px;left:-8px;cursor:nwse-resize;";
                if (pos === "tr") h.style.cssText = base + "top:-8px;right:-8px;cursor:nesw-resize;";
                if (pos === "bl") h.style.cssText = base + "bottom:-8px;left:-8px;cursor:nesw-resize;";
                if (pos === "br") h.style.cssText = base + "bottom:-8px;right:-8px;cursor:nwse-resize;";
                cropBoxEl.appendChild(h);
            });

            // Edge handles
            ["t", "b", "l", "r"].forEach(function (pos) {
                var h = document.createElement("div");
                h.dataset.handle = pos;
                var base = "position:absolute;background:#ff9800;border:1px solid #fff;box-sizing:border-box;z-index:10;border-radius:1px;";
                if (pos === "t") h.style.cssText = base + "top:-5px;left:50%;transform:translateX(-50%);width:24px;height:10px;cursor:ns-resize;";
                if (pos === "b") h.style.cssText = base + "bottom:-5px;left:50%;transform:translateX(-50%);width:24px;height:10px;cursor:ns-resize;";
                if (pos === "l") h.style.cssText = base + "left:-5px;top:50%;transform:translateY(-50%);width:10px;height:24px;cursor:ew-resize;";
                if (pos === "r") h.style.cssText = base + "right:-5px;top:50%;transform:translateY(-50%);width:10px;height:24px;cursor:ew-resize;";
                cropBoxEl.appendChild(h);
            });

            // Default crop: 90% of image, centered
            var defW = cropDisplayW * 0.9;
            var defH = cropDisplayH * 0.9;
            cropState.x = (cropDisplayW - defW) / 2;
            cropState.y = (cropDisplayH - defH) / 2;
            cropState.w = defW;
            cropState.h = defH;
            updateCropUI();

            wrapper.addEventListener("mousedown", onCropMouseDown);
            document.addEventListener("mousemove", onCropMouseMove);
            document.addEventListener("mouseup", onCropMouseUp);
            wrapper.addEventListener("touchstart", onCropTouchStart, { passive: false });
            document.addEventListener("touchmove", onCropTouchMove, { passive: false });
            document.addEventListener("touchend", onCropTouchEnd);
        };

        img.src = dataUrl;
        wrapper.appendChild(img);
        container.appendChild(wrapper);

        cropContainer = container;
        cropWrapper = wrapper;
        cropImg = img;
    }

    // ── Mouse/Touch handling ──

    function onCropMouseDown(e) {
        e.preventDefault();
        var rect = cropWrapper.getBoundingClientRect();
        var mx = e.clientX - rect.left;
        var my = e.clientY - rect.top;

        var handle = e.target.dataset ? e.target.dataset.handle : null;
        if (handle) {
            dragState = { type: "resize", handle: handle, startX: e.clientX, startY: e.clientY, orig: { x: cropState.x, y: cropState.y, w: cropState.w, h: cropState.h } };
            return;
        }

        if (mx >= cropState.x && mx <= cropState.x + cropState.w &&
            my >= cropState.y && my <= cropState.y + cropState.h) {
            dragState = { type: "move", startX: e.clientX, startY: e.clientY, orig: { x: cropState.x, y: cropState.y, w: cropState.w, h: cropState.h } };
        }
    }

    function onCropMouseMove(e) {
        if (!dragState) return;
        e.preventDefault();
        var dx = e.clientX - dragState.startX;
        var dy = e.clientY - dragState.startY;

        if (dragState.type === "move") {
            cropState.x = clamp(dragState.orig.x + dx, 0, cropDisplayW - cropState.w);
            cropState.y = clamp(dragState.orig.y + dy, 0, cropDisplayH - cropState.h);
        } else {
            resizeCrop(dragState.handle, dx, dy, dragState.orig);
        }
        updateCropUI();
    }

    function onCropMouseUp() { dragState = null; }

    function onCropTouchStart(e) {
        if (e.touches.length === 1) {
            e.preventDefault();
            var t = e.touches[0];
            onCropMouseDown({ preventDefault: function () { }, clientX: t.clientX, clientY: t.clientY, target: e.target });
        }
    }

    function onCropTouchMove(e) {
        if (e.touches.length === 1 && dragState) {
            e.preventDefault();
            var t = e.touches[0];
            onCropMouseMove({ preventDefault: function () { }, clientX: t.clientX, clientY: t.clientY });
        }
    }

    function onCropTouchEnd() { dragState = null; }

    // ── Free aspect ratio resize ──

    function resizeCrop(handle, dx, dy, orig) {
        var nx = orig.x, ny = orig.y, nw = orig.w, nh = orig.h;

        if (handle === "br") { nw = orig.w + dx; nh = orig.h + dy; }
        else if (handle === "bl") { nw = orig.w - dx; nh = orig.h + dy; nx = orig.x + orig.w - nw; }
        else if (handle === "tr") { nw = orig.w + dx; nh = orig.h - dy; ny = orig.y + orig.h - nh; }
        else if (handle === "tl") { nw = orig.w - dx; nh = orig.h - dy; nx = orig.x + orig.w - nw; ny = orig.y + orig.h - nh; }
        else if (handle === "r") { nw = orig.w + dx; }
        else if (handle === "l") { nw = orig.w - dx; nx = orig.x + orig.w - nw; }
        else if (handle === "b") { nh = orig.h + dy; }
        else if (handle === "t") { nh = orig.h - dy; ny = orig.y + orig.h - nh; }

        // Minimum size
        if (nw < 40) { nw = 40; if (handle === "tl" || handle === "bl" || handle === "l") nx = orig.x + orig.w - 40; }
        if (nh < 40) { nh = 40; if (handle === "tl" || handle === "tr" || handle === "t") ny = orig.y + orig.h - 40; }

        // Clamp to image bounds
        if (nx < 0) { nw += nx; nx = 0; }
        if (ny < 0) { nh += ny; ny = 0; }
        if (nx + nw > cropDisplayW) nw = cropDisplayW - nx;
        if (ny + nh > cropDisplayH) nh = cropDisplayH - ny;

        cropState.x = nx;
        cropState.y = ny;
        cropState.w = nw;
        cropState.h = nh;
    }

    function updateCropUI() {
        if (!cropBoxEl) return;
        var s = cropState;
        cropBoxEl.style.left = s.x + "px";
        cropBoxEl.style.top = s.y + "px";
        cropBoxEl.style.width = s.w + "px";
        cropBoxEl.style.height = s.h + "px";

        if (dimEls.top) {
            dimEls.top.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;left:0;top:0;width:" + cropDisplayW + "px;height:" + Math.max(0, s.y) + "px;";
            dimEls.bottom.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;left:0;top:" + (s.y + s.h) + "px;width:" + cropDisplayW + "px;height:" + Math.max(0, cropDisplayH - s.y - s.h) + "px;";
            dimEls.left.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;left:0;top:" + s.y + "px;width:" + Math.max(0, s.x) + "px;height:" + s.h + "px;";
            dimEls.right.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;left:" + (s.x + s.w) + "px;top:" + s.y + "px;width:" + Math.max(0, cropDisplayW - s.x - s.w) + "px;height:" + s.h + "px;";
        }
    }

    function getCropRect() {
        if (!cropImg) return { x: 0, y: 0, w: 1, h: 1 };
        return {
            x: Math.max(0, Math.min(cropState.x / cropDisplayW, 1)),
            y: Math.max(0, Math.min(cropState.y / cropDisplayH, 1)),
            w: Math.max(0.01, Math.min(cropState.w / cropDisplayW, 1)),
            h: Math.max(0.01, Math.min(cropState.h / cropDisplayH, 1)),
        };
    }

    function disposeCropTool() {
        if (cropWrapper) {
            cropWrapper.removeEventListener("mousedown", onCropMouseDown);
            document.removeEventListener("mousemove", onCropMouseMove);
            document.removeEventListener("mouseup", onCropMouseUp);
            cropWrapper.removeEventListener("touchstart", onCropTouchStart);
            document.removeEventListener("touchmove", onCropTouchMove);
            document.removeEventListener("touchend", onCropTouchEnd);
        }
        if (cropContainer) cropContainer.innerHTML = "";
        cropContainer = null; cropWrapper = null; cropImg = null; cropBoxEl = null;
        dimEls = {}; dragState = null;
    }

    function clamp(v, min, max) { return Math.max(min, Math.min(v, max)); }

    // ══════════════════════════════════════════
    // ── Negative Inversion Algorithms ──
    // ══════════════════════════════════════════

    function cropAndConvert(canvasId, negativeType, frameStyle) {
        if (!cropImg) return Promise.resolve(null);
        var rect = getCropRect();
        var srcX = Math.round(rect.x * cropNaturalW);
        var srcY = Math.round(rect.y * cropNaturalH);
        var srcW = Math.round(rect.w * cropNaturalW);
        var srcH = Math.round(rect.h * cropNaturalH);

        // Use full source resolution (only cap at extreme sizes to avoid browser crashes)
        var scale = 1;
        if (srcW > MAX_DIMENSION || srcH > MAX_DIMENSION) {
            scale = MAX_DIMENSION / Math.max(srcW, srcH);
        }
        var outW = Math.max(1, Math.round(srcW * scale));
        var outH = Math.max(1, Math.round(srcH * scale));

        return new Promise(function (resolve) {
            // Draw cropped region to offscreen canvas at full resolution
            var offscreen = document.createElement("canvas");
            offscreen.width = outW;
            offscreen.height = outH;
            var ctx = offscreen.getContext("2d");
            ctx.imageSmoothingEnabled = true;
            ctx.imageSmoothingQuality = "high";

            // Load from the original full-res image
            var fullImg = new Image();
            fullImg.crossOrigin = "anonymous";
            fullImg.onload = function () {
                ctx.drawImage(fullImg, srcX, srcY, srcW, srcH, 0, 0, outW, outH);

                // Apply inversion based on negative type
                var imageData = ctx.getImageData(0, 0, outW, outH);
                if (negativeType === "color") {
                    invertColor(imageData);
                } else if (negativeType === "bw") {
                    invertBW(imageData);
                } else if (negativeType === "slide") {
                    enhanceSlide(imageData);
                }
                ctx.putImageData(imageData, 0, 0);

                // Apply framing and render to output canvas
                renderFramed(canvasId, offscreen, frameStyle);
                resolve(true);
            };
            fullImg.onerror = function () { resolve(null); };
            fullImg.src = cropImg.src;
        });
    }

    // Crop only (no inversion) — returns data URL via hidden canvas. Used for AI conversion.
    function cropOnly(canvasId) {
        if (!cropImg) return Promise.resolve(null);
        var rect = getCropRect();
        var srcX = Math.round(rect.x * cropNaturalW);
        var srcY = Math.round(rect.y * cropNaturalH);
        var srcW = Math.round(rect.w * cropNaturalW);
        var srcH = Math.round(rect.h * cropNaturalH);

        var scale = 1;
        if (srcW > MAX_DIMENSION || srcH > MAX_DIMENSION) {
            scale = MAX_DIMENSION / Math.max(srcW, srcH);
        }
        var outW = Math.max(1, Math.round(srcW * scale));
        var outH = Math.max(1, Math.round(srcH * scale));

        return new Promise(function (resolve) {
            var fullImg = new Image();
            fullImg.crossOrigin = "anonymous";
            fullImg.onload = function () {
                var el = document.getElementById(canvasId);
                if (!el) { resolve(null); return; }
                el.width = outW;
                el.height = outH;
                var ctx = el.getContext("2d");
                ctx.imageSmoothingEnabled = true;
                ctx.imageSmoothingQuality = "high";
                ctx.drawImage(fullImg, srcX, srcY, srcW, srcH, 0, 0, outW, outH);
                resolve(el.toDataURL("image/png"));
            };
            fullImg.onerror = function () { resolve(null); };
            fullImg.src = cropImg.src;
        });
    }

    // Apply a frame to a data URL image, render to canvas
    function applyFrameToDataUrl(canvasId, dataUrl, frameStyle) {
        return new Promise(function (resolve) {
            var img = new Image();
            img.crossOrigin = "anonymous";
            img.onload = function () {
                var offscreen = document.createElement("canvas");
                offscreen.width = img.naturalWidth;
                offscreen.height = img.naturalHeight;
                var ctx = offscreen.getContext("2d");
                ctx.drawImage(img, 0, 0);
                renderFramed(canvasId, offscreen, frameStyle);
                resolve();
            };
            img.onerror = function () { resolve(); };
            img.src = dataUrl;
        });
    }

    // ══════════════════════════════════════════════════════
    // ── Color C-41 Negative Inversion (log-space method) ──
    // ══════════════════════════════════════════════════════
    //
    // C-41 negatives have an orange base (mask) baked into the film.
    // The correct approach:
    //   1. Sample the film base color from the lightest area (unexposed = densest orange)
    //   2. Convert to log-density space
    //   3. Subtract the film base
    //   4. Invert density → positive
    //   5. Per-channel auto-stretch in linear space
    //   6. Apply color correction, contrast, and warmth

    function invertColor(imageData) {
        var d = imageData.data;
        var len = d.length;
        var pixCount = len / 4;

        // Step 1: Estimate the orange film base from the brightest pixels
        // (on a negative, the brightest = most exposed = densest; unexposed film border
        //  is the dimmest. But for a cropped frame, we sample the darkest shadow region
        //  of the negative which corresponds to the film base.)
        // We use the 98th percentile of each channel as the film base estimate.
        var histR = new Uint32Array(256), histG = new Uint32Array(256), histB = new Uint32Array(256);
        for (var i = 0; i < len; i += 4) {
            histR[d[i]]++;
            histG[d[i + 1]]++;
            histB[d[i + 2]]++;
        }
        var baseR = findPercentile(histR, Math.floor(pixCount * 0.98));
        var baseG = findPercentile(histG, Math.floor(pixCount * 0.98));
        var baseB = findPercentile(histB, Math.floor(pixCount * 0.98));

        // Ensure minimums to avoid log(0)
        baseR = Math.max(baseR, 1);
        baseG = Math.max(baseG, 1);
        baseB = Math.max(baseB, 1);

        // Log-density of film base
        var logBaseR = Math.log(baseR / 255);
        var logBaseG = Math.log(baseG / 255);
        var logBaseB = Math.log(baseB / 255);

        // Step 2: Convert each pixel: log density → subtract base → invert → exp back to linear
        var linR = new Float32Array(pixCount);
        var linG = new Float32Array(pixCount);
        var linB = new Float32Array(pixCount);

        for (var i = 0; i < pixCount; i++) {
            var idx = i * 4;
            var r = Math.max(d[idx], 1) / 255;
            var g = Math.max(d[idx + 1], 1) / 255;
            var b = Math.max(d[idx + 2], 1) / 255;

            // Log density, subtract film base, negate (invert)
            linR[i] = Math.exp(-(Math.log(r) - logBaseR));
            linG[i] = Math.exp(-(Math.log(g) - logBaseG));
            linB[i] = Math.exp(-(Math.log(b) - logBaseB));
        }

        // Step 3: Per-channel auto-stretch with 0.5% clipping
        stretchChannel(linR, pixCount, 0.005);
        stretchChannel(linG, pixCount, 0.005);
        stretchChannel(linB, pixCount, 0.005);

        // Step 4: Apply gamma correction (slightly warm) + write back
        var gammaR = 0.95;  // less gamma = brighter reds (warmer)
        var gammaG = 1.00;
        var gammaB = 1.08;  // more gamma = darker blues (warmer)

        for (var i = 0; i < pixCount; i++) {
            var idx = i * 4;
            d[idx]     = clampByte(Math.pow(linR[i], gammaR) * 255);
            d[idx + 1] = clampByte(Math.pow(linG[i], gammaG) * 255);
            d[idx + 2] = clampByte(Math.pow(linB[i], gammaB) * 255);
        }

        // Step 5: S-curve for contrast + saturation boost
        var lut = buildSCurve(0.22);
        for (var i = 0; i < len; i += 4) {
            d[i]     = lut[d[i]];
            d[i + 1] = lut[d[i + 1]];
            d[i + 2] = lut[d[i + 2]];
        }

        // Step 6: Gentle saturation boost (1.15x)
        for (var i = 0; i < len; i += 4) {
            var gray = d[i] * 0.299 + d[i + 1] * 0.587 + d[i + 2] * 0.114;
            d[i]     = clampByte(gray + (d[i] - gray) * 1.15);
            d[i + 1] = clampByte(gray + (d[i + 1] - gray) * 1.15);
            d[i + 2] = clampByte(gray + (d[i + 2] - gray) * 1.15);
        }
    }

    // Stretch a float channel [0..inf) to [0..1] with percentile clipping
    function stretchChannel(arr, count, clipPct) {
        // Build a sorted sample for percentile finding (sample 10k pixels max for speed)
        var step = Math.max(1, Math.floor(count / 10000));
        var samples = [];
        for (var i = 0; i < count; i += step) {
            samples.push(arr[i]);
        }
        samples.sort(function (a, b) { return a - b; });

        var lo = samples[Math.floor(samples.length * clipPct)] || 0;
        var hi = samples[Math.floor(samples.length * (1 - clipPct))] || 1;
        var range = Math.max(0.001, hi - lo);

        for (var i = 0; i < count; i++) {
            arr[i] = Math.max(0, Math.min(1, (arr[i] - lo) / range));
        }
    }

    // ── B&W: invert → grayscale → aggressive auto-levels → contrast ──

    function invertBW(imageData) {
        var d = imageData.data;
        var len = d.length;
        var pixCount = len / 4;

        // Step 1: Invert and convert to grayscale
        for (var i = 0; i < len; i += 4) {
            var r = 255 - d[i];
            var g = 255 - d[i + 1];
            var b = 255 - d[i + 2];
            var gray = Math.round(r * 0.299 + g * 0.587 + b * 0.114);
            d[i] = d[i + 1] = d[i + 2] = gray;
        }

        // Step 2: Aggressive auto-levels (0.5% clip)
        autoLevels(d, len, 0.005);

        // Step 3: S-curve contrast
        var lut = buildSCurve(0.30);
        for (var i = 0; i < len; i += 4) {
            d[i]     = lut[d[i]];
            d[i + 1] = lut[d[i + 1]];
            d[i + 2] = lut[d[i + 2]];
        }
    }

    // ── Slide E-6: no inversion → auto-levels → saturation + contrast ──

    function enhanceSlide(imageData) {
        var d = imageData.data;
        var len = d.length;

        // Step 1: Aggressive auto-levels
        autoLevels(d, len, 0.005);

        // Step 2: Saturation boost (1.3x)
        for (var i = 0; i < len; i += 4) {
            var gray = d[i] * 0.299 + d[i + 1] * 0.587 + d[i + 2] * 0.114;
            d[i]     = clampByte(gray + (d[i] - gray) * 1.3);
            d[i + 1] = clampByte(gray + (d[i + 1] - gray) * 1.3);
            d[i + 2] = clampByte(gray + (d[i + 2] - gray) * 1.3);
        }

        // Step 3: Contrast
        var lut = buildSCurve(0.22);
        for (var i = 0; i < len; i += 4) {
            d[i]     = lut[d[i]];
            d[i + 1] = lut[d[i + 1]];
            d[i + 2] = lut[d[i + 2]];
        }
    }

    // ── Helpers ──

    function autoLevels(d, len, clipPct) {
        clipPct = clipPct || 0.005;
        var histR = new Uint32Array(256), histG = new Uint32Array(256), histB = new Uint32Array(256);
        for (var i = 0; i < len; i += 4) {
            histR[clampByte(d[i])]++;
            histG[clampByte(d[i + 1])]++;
            histB[clampByte(d[i + 2])]++;
        }
        var pixCount = len / 4;
        var clipLo = Math.floor(pixCount * clipPct);
        var clipHi = Math.floor(pixCount * (1 - clipPct));

        var minR = findPercentile(histR, clipLo), maxR = findPercentile(histR, clipHi);
        var minG = findPercentile(histG, clipLo), maxG = findPercentile(histG, clipHi);
        var minB = findPercentile(histB, clipLo), maxB = findPercentile(histB, clipHi);

        var rangeR = Math.max(1, maxR - minR);
        var rangeG = Math.max(1, maxG - minG);
        var rangeB = Math.max(1, maxB - minB);

        for (var i = 0; i < len; i += 4) {
            d[i]     = clampByte(((d[i] - minR) / rangeR) * 255);
            d[i + 1] = clampByte(((d[i + 1] - minG) / rangeG) * 255);
            d[i + 2] = clampByte(((d[i + 2] - minB) / rangeB) * 255);
        }
    }

    function findPercentile(hist, count) {
        var sum = 0;
        for (var i = 0; i < 256; i++) {
            sum += hist[i];
            if (sum >= count) return i;
        }
        return 255;
    }

    function buildSCurve(strength) {
        var lut = new Uint8Array(256);
        for (var i = 0; i < 256; i++) {
            var x = i / 255.0;
            // Sigmoid-based S-curve
            var s = x - 0.5;
            var curved = 0.5 + s + strength * s * (1 - 4 * s * s);
            lut[i] = clampByte(Math.round(curved * 255));
        }
        return lut;
    }

    function clampByte(v) { return Math.max(0, Math.min(255, Math.round(v))); }

    // ══════════════════════════════════════════
    // ── Frame Rendering ──
    // ══════════════════════════════════════════

    function renderFramed(canvasId, srcCanvas, frameStyle) {
        var el = document.getElementById(canvasId);
        if (!el) return;
        var ctx = el.getContext("2d");
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = "high";

        var imgW = srcCanvas.width;
        var imgH = srcCanvas.height;
        var pad, totalW, totalH, bottomPad;

        if (frameStyle === "polaroid") {
            pad = Math.round(Math.max(imgW, imgH) * 0.06);
            bottomPad = Math.round(Math.max(imgW, imgH) * 0.18);
            totalW = imgW + pad * 2;
            totalH = imgH + pad + bottomPad;
            el.width = totalW; el.height = totalH;

            // White background
            ctx.fillStyle = "#ffffff";
            ctx.fillRect(0, 0, totalW, totalH);

            // Subtle shadow
            ctx.shadowColor = "rgba(0,0,0,0.15)";
            ctx.shadowBlur = pad * 0.5;
            ctx.shadowOffsetY = pad * 0.2;

            ctx.drawImage(srcCanvas, pad, pad, imgW, imgH);
            ctx.shadowColor = "transparent";

        } else if (frameStyle === "classic") {
            pad = Math.round(Math.max(imgW, imgH) * 0.05);
            totalW = imgW + pad * 2;
            totalH = imgH + pad * 2;
            el.width = totalW; el.height = totalH;

            // Wooden brown gradient border
            var grad = ctx.createLinearGradient(0, 0, totalW, totalH);
            grad.addColorStop(0, "#8B6914");
            grad.addColorStop(0.3, "#A0782C");
            grad.addColorStop(0.5, "#C49A3C");
            grad.addColorStop(0.7, "#A0782C");
            grad.addColorStop(1, "#6B4F12");
            ctx.fillStyle = grad;
            ctx.fillRect(0, 0, totalW, totalH);

            // Inner bevel
            ctx.strokeStyle = "rgba(255,255,255,0.3)";
            ctx.lineWidth = 2;
            ctx.strokeRect(pad - 3, pad - 3, imgW + 6, imgH + 6);

            ctx.drawImage(srcCanvas, pad, pad, imgW, imgH);

        } else if (frameStyle === "modern") {
            pad = Math.round(Math.max(imgW, imgH) * 0.015);
            var shadowPad = Math.round(Math.max(imgW, imgH) * 0.04);
            totalW = imgW + pad * 2 + shadowPad * 2;
            totalH = imgH + pad * 2 + shadowPad * 2;
            el.width = totalW; el.height = totalH;

            // Light background for shadow visibility
            ctx.fillStyle = "var(--bg-primary, #1a1a2e)";
            ctx.fillRect(0, 0, totalW, totalH);

            // Shadow
            ctx.shadowColor = "rgba(0,0,0,0.35)";
            ctx.shadowBlur = shadowPad;
            ctx.shadowOffsetX = shadowPad * 0.15;
            ctx.shadowOffsetY = shadowPad * 0.25;

            // Dark thin border
            ctx.fillStyle = "#2a2a2a";
            ctx.fillRect(shadowPad, shadowPad, imgW + pad * 2, imgH + pad * 2);
            ctx.shadowColor = "transparent";

            ctx.drawImage(srcCanvas, shadowPad + pad, shadowPad + pad, imgW, imgH);

        } else {
            // None — just the image
            el.width = imgW; el.height = imgH;
            ctx.drawImage(srcCanvas, 0, 0, imgW, imgH);
        }
    }

    // ══════════════════════════════════════════
    // ── Download ──
    // ══════════════════════════════════════════

    function downloadResult(canvasId, format, fileName) {
        var el = document.getElementById(canvasId);
        if (!el) return;

        var mime = format === "jpg" ? "image/jpeg" : "image/png";
        var quality = format === "jpg" ? 0.92 : undefined;
        var dataUrl = el.toDataURL(mime, quality);

        var a = document.createElement("a");
        a.href = dataUrl;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    // Get canvas data URL for server-side AI enhancement
    function getResultDataUrl(canvasId) {
        var el = document.getElementById(canvasId);
        if (!el) return null;
        return el.toDataURL("image/png");
    }

    // Display an AI-enhanced image on the comparison canvas
    function displayEnhancedImage(canvasId, dataUrl) {
        var el = document.getElementById(canvasId);
        if (!el) return;
        var ctx = el.getContext("2d");
        var img = new Image();
        img.crossOrigin = "anonymous";
        img.onload = function () {
            el.width = img.naturalWidth;
            el.height = img.naturalHeight;
            ctx.drawImage(img, 0, 0);
        };
        img.src = dataUrl;
    }

    // Download directly from a data URL (no canvas needed)
    function downloadFromDataUrl(dataUrl, fileName) {
        if (!dataUrl) return;
        var a = document.createElement("a");
        a.href = dataUrl;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    function dispose() {
        disposeCropTool();
    }

    // ── Public API ──
    return {
        initCropTool: initCropTool,
        disposeCropTool: disposeCropTool,
        cropAndConvert: cropAndConvert,
        cropOnly: cropOnly,
        applyFrameToDataUrl: applyFrameToDataUrl,
        downloadResult: downloadResult,
        downloadFromDataUrl: downloadFromDataUrl,
        getResultDataUrl: getResultDataUrl,
        displayEnhancedImage: displayEnhancedImage,
        dispose: dispose
    };
})();
