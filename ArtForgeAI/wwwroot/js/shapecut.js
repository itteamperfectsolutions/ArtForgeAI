// ===== ArtForge AI - Shape Cut Gang Sheet Module =====
window.shapeCut = (function () {
    "use strict";

    // ── State ──
    var cropContainer = null;
    var cropWrapper = null;
    var cropImg = null;
    var cropBoxEl = null;
    var dimEls = {};
    var cropDisplayW = 0, cropDisplayH = 0;
    var cropNaturalW = 0, cropNaturalH = 0;
    var cropState = { x: 0, y: 0, w: 100, h: 100 };
    var dragState = null;
    var aspectRatio = 0; // 0 = free crop (no ratio lock)

    var sheetCanvas = null;
    var sheetResizeObserver = null;
    var viewMode = "print"; // "print" or "cut"

    // Cache last render params for re-rendering on view mode toggle
    var lastRenderParams = null;

    // Outline preview overlay on crop
    var outlineOverlayEl = null;
    var lastPreviewParams = null; // {distancePx, cornerRadiusPx, widthPx}

    // ══════════════════════════════════════════
    // ── DOM-based Interactive Crop Tool ──
    // ══════════════════════════════════════════

    function initCropTool(containerId, dataUrl) {
        disposeCropTool();

        var container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = "";

        var wrapper = document.createElement("div");
        wrapper.style.cssText = "position:relative;display:inline-block;user-select:none;line-height:0;";

        var img = document.createElement("img");
        var maxH = window.innerWidth <= 480 ? 250 : (window.innerWidth <= 900 ? 300 : 400);
        img.style.cssText = "display:block;max-height:" + maxH + "px;max-width:100%;pointer-events:none;";
        img.crossOrigin = "anonymous";

        img.onload = function () {
            cropNaturalW = img.naturalWidth;
            cropNaturalH = img.naturalHeight;
            cropDisplayW = img.clientWidth;
            cropDisplayH = img.clientHeight;

            ["top", "bottom", "left", "right"].forEach(function (side) {
                var d = document.createElement("div");
                d.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;transition:none;";
                wrapper.appendChild(d);
                dimEls[side] = d;
            });

            cropBoxEl = document.createElement("div");
            cropBoxEl.style.cssText = "position:absolute;border:2px dashed #00c8ff;cursor:move;box-sizing:border-box;overflow:hidden;";
            wrapper.appendChild(cropBoxEl);

            ["tl", "tr", "bl", "br"].forEach(function (pos) {
                var h = document.createElement("div");
                h.dataset.handle = pos;
                var base = "position:absolute;width:16px;height:16px;background:#00c8ff;border:2px solid #fff;box-sizing:border-box;z-index:10;border-radius:2px;";
                if (pos === "tl") h.style.cssText = base + "top:-8px;left:-8px;cursor:nwse-resize;";
                if (pos === "tr") h.style.cssText = base + "top:-8px;right:-8px;cursor:nesw-resize;";
                if (pos === "bl") h.style.cssText = base + "bottom:-8px;left:-8px;cursor:nesw-resize;";
                if (pos === "br") h.style.cssText = base + "bottom:-8px;right:-8px;cursor:nwse-resize;";
                cropBoxEl.appendChild(h);
            });

            ["t", "b", "l", "r"].forEach(function (pos) {
                var h = document.createElement("div");
                h.dataset.handle = pos;
                var base = "position:absolute;background:#00c8ff;border:1px solid #fff;box-sizing:border-box;z-index:10;border-radius:1px;";
                if (pos === "t") h.style.cssText = base + "top:-5px;left:50%;transform:translateX(-50%);width:24px;height:10px;cursor:ns-resize;";
                if (pos === "b") h.style.cssText = base + "bottom:-5px;left:50%;transform:translateX(-50%);width:24px;height:10px;cursor:ns-resize;";
                if (pos === "l") h.style.cssText = base + "left:-5px;top:50%;transform:translateY(-50%);width:10px;height:24px;cursor:ew-resize;";
                if (pos === "r") h.style.cssText = base + "right:-5px;top:50%;transform:translateY(-50%);width:10px;height:24px;cursor:ew-resize;";
                cropBoxEl.appendChild(h);
            });

            var defH = cropDisplayH * 0.85;
            var defW = (aspectRatio > 0) ? defH * aspectRatio : cropDisplayW * 0.85;
            if (defW > cropDisplayW * 0.95) {
                defW = cropDisplayW * 0.95;
                defH = (aspectRatio > 0) ? defW / aspectRatio : defH;
            }
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

    function setAspectRatio(ratio) {
        aspectRatio = (ratio > 0) ? ratio : 0; // 0 = free crop
        if (aspectRatio === 0 || !cropBoxEl || !cropDisplayW) return;

        var cx = cropState.x + cropState.w / 2;
        var cy = cropState.y + cropState.h / 2;
        var newW = cropState.w;
        var newH = newW / aspectRatio;
        if (newH > cropDisplayH * 0.95) {
            newH = cropDisplayH * 0.95;
            newW = newH * aspectRatio;
        }
        if (newW > cropDisplayW * 0.95) {
            newW = cropDisplayW * 0.95;
            newH = newW / aspectRatio;
        }
        cropState.w = newW;
        cropState.h = newH;
        cropState.x = clamp(cx - newW / 2, 0, cropDisplayW - newW);
        cropState.y = clamp(cy - newH / 2, 0, cropDisplayH - newH);
        updateCropUI();
    }

    function swapCropImage(dataUrl) {
        if (!cropImg || !cropWrapper) return Promise.resolve();
        return new Promise(function (resolve) {
            var relX = cropState.x / cropDisplayW;
            var relY = cropState.y / cropDisplayH;
            var relW = cropState.w / cropDisplayW;
            var relH = cropState.h / cropDisplayH;

            cropImg.onload = function () {
                cropNaturalW = cropImg.naturalWidth;
                cropNaturalH = cropImg.naturalHeight;
                cropDisplayW = cropImg.clientWidth;
                cropDisplayH = cropImg.clientHeight;

                cropState.x = relX * cropDisplayW;
                cropState.y = relY * cropDisplayH;
                cropState.w = relW * cropDisplayW;
                cropState.h = relH * cropDisplayH;
                updateCropUI();
                resolve();
            };
            cropImg.onerror = function () { resolve(); };
            cropImg.src = dataUrl;
        });
    }

    function extractCrop(targetWPx, targetHPx) {
        if (!cropImg || !cropImg.naturalWidth) return null;
        var rect = getCropRect();
        var sx = Math.round(rect.x * cropNaturalW);
        var sy = Math.round(rect.y * cropNaturalH);
        var sw = Math.round(rect.w * cropNaturalW);
        var sh = Math.round(rect.h * cropNaturalH);
        if (sw < 1 || sh < 1) return null;

        // If targetW/H are 0, use the natural cropped dimensions
        var outW = (targetWPx > 0) ? targetWPx : sw;
        var outH = (targetHPx > 0) ? targetHPx : sh;

        var offscreen = document.createElement("canvas");
        offscreen.width = outW;
        offscreen.height = outH;
        var ctx = offscreen.getContext("2d");
        ctx.clearRect(0, 0, outW, outH);
        ctx.drawImage(cropImg, sx, sy, sw, sh, 0, 0, outW, outH);
        return offscreen.toDataURL("image/png");
    }

    // ── Mouse handling ──

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

    function onCropMouseUp() {
        dragState = null;
        if (lastPreviewParams) {
            updateOutlinePreview(lastPreviewParams.distancePx, lastPreviewParams.cornerRadiusPx, lastPreviewParams.widthPx);
        }
    }

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

    function onCropTouchEnd() {
        dragState = null;
        if (lastPreviewParams) {
            updateOutlinePreview(lastPreviewParams.distancePx, lastPreviewParams.cornerRadiusPx, lastPreviewParams.widthPx);
        }
    }

    // ── Resize with optional aspect ratio lock (aspectRatio=0 → free) ──

    function resizeCrop(handle, dx, dy, orig) {
        var nw, nh, nx, ny;
        var locked = (aspectRatio > 0);

        if (handle === "br") {
            nw = Math.max(40, orig.w + dx);
            nh = locked ? nw / aspectRatio : Math.max(40, orig.h + dy);
            nx = orig.x; ny = orig.y;
        } else if (handle === "bl") {
            nw = Math.max(40, orig.w - dx);
            nh = locked ? nw / aspectRatio : Math.max(40, orig.h + dy);
            nx = orig.x + orig.w - nw; ny = orig.y;
        } else if (handle === "tr") {
            nw = Math.max(40, orig.w + dx);
            nh = locked ? nw / aspectRatio : Math.max(40, orig.h - dy);
            nx = orig.x; ny = locked ? orig.y + orig.h - nh : orig.y + orig.h - nh;
        } else if (handle === "tl") {
            nw = Math.max(40, orig.w - dx);
            nh = locked ? nw / aspectRatio : Math.max(40, orig.h - dy);
            nx = orig.x + orig.w - nw; ny = orig.y + orig.h - nh;
        } else if (handle === "r") {
            nw = Math.max(40, orig.w + dx);
            nh = locked ? nw / aspectRatio : orig.h;
            nx = orig.x; ny = locked ? orig.y + (orig.h - nh) / 2 : orig.y;
        } else if (handle === "l") {
            nw = Math.max(40, orig.w - dx);
            nh = locked ? nw / aspectRatio : orig.h;
            nx = orig.x + orig.w - nw; ny = locked ? orig.y + (orig.h - nh) / 2 : orig.y;
        } else if (handle === "b") {
            nh = Math.max(40, orig.h + dy);
            nw = locked ? nh * aspectRatio : orig.w;
            nx = locked ? orig.x + (orig.w - nw) / 2 : orig.x; ny = orig.y;
        } else if (handle === "t") {
            nh = Math.max(40, orig.h - dy);
            nw = locked ? nh * aspectRatio : orig.w;
            nx = locked ? orig.x + (orig.w - nw) / 2 : orig.x; ny = orig.y + orig.h - nh;
        } else {
            return;
        }

        if (nx < 0) { nx = 0; }
        if (ny < 0) { ny = 0; }
        if (nx + nw > cropDisplayW) { nw = cropDisplayW - nx; if (locked) nh = nw / aspectRatio; }
        if (ny + nh > cropDisplayH) { nh = cropDisplayH - ny; if (locked) nw = nh * aspectRatio; }
        if (nw < 40) { nw = 40; if (locked) nh = nw / aspectRatio; }
        if (nh < 40) { nh = 40; if (locked) nw = nh * aspectRatio; }

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
            dimEls.top.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;" +
                "left:0;top:0;width:" + cropDisplayW + "px;height:" + Math.max(0, s.y) + "px;";
            dimEls.bottom.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;" +
                "left:0;top:" + (s.y + s.h) + "px;width:" + cropDisplayW + "px;height:" + Math.max(0, cropDisplayH - s.y - s.h) + "px;";
            dimEls.left.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;" +
                "left:0;top:" + s.y + "px;width:" + Math.max(0, s.x) + "px;height:" + s.h + "px;";
            dimEls.right.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;" +
                "left:" + (s.x + s.w) + "px;top:" + s.y + "px;width:" + Math.max(0, cropDisplayW - s.x - s.w) + "px;height:" + s.h + "px;";
        }

        // Refresh outline preview overlay on debounce (pixel computation is heavy)
        if (lastPreviewParams && !dragState) {
            updateOutlinePreview(lastPreviewParams.distancePx, lastPreviewParams.cornerRadiusPx, lastPreviewParams.widthPx);
        } else if (lastPreviewParams && outlineOverlayEl) {
            // During drag, just reposition the existing overlay
            outlineOverlayEl.style.left = cropState.x + "px";
            outlineOverlayEl.style.top = cropState.y + "px";
            outlineOverlayEl.style.width = cropState.w + "px";
            outlineOverlayEl.style.height = cropState.h + "px";
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
        clearOutlinePreview();
        if (cropWrapper) {
            cropWrapper.removeEventListener("mousedown", onCropMouseDown);
            document.removeEventListener("mousemove", onCropMouseMove);
            document.removeEventListener("mouseup", onCropMouseUp);
            cropWrapper.removeEventListener("touchstart", onCropTouchStart);
            document.removeEventListener("touchmove", onCropTouchMove);
            document.removeEventListener("touchend", onCropTouchEnd);
        }
        if (cropContainer) cropContainer.innerHTML = "";
        cropContainer = null;
        cropWrapper = null;
        cropImg = null;
        cropBoxEl = null;
        dimEls = {};
        dragState = null;
    }

    function clamp(v, min, max) { return Math.max(min, Math.min(v, max)); }

    // ══════════════════════════════════════════
    // ── Live Outline Preview on Crop ──
    // ══════════════════════════════════════════

    // Renders a shape-tracing outline preview on the crop area using morphological dilation.
    function updateOutlinePreview(distancePx, cornerRadiusPx, widthPx) {
        lastPreviewParams = { distancePx: distancePx, cornerRadiusPx: cornerRadiusPx, widthPx: widthPx };
        if (!cropBoxEl || !cropWrapper || !cropImg) return;

        if (outlineOverlayEl) {
            outlineOverlayEl.remove();
            outlineOverlayEl = null;
        }

        var rect = getCropRect();
        var sx = Math.round(rect.x * cropNaturalW);
        var sy = Math.round(rect.y * cropNaturalH);
        var sw = Math.round(rect.w * cropNaturalW);
        var sh = Math.round(rect.h * cropNaturalH);
        if (sw < 2 || sh < 2) return;

        // Use moderate resolution preview for speed while maintaining quality
        var maxDim = 300;
        var previewScale = Math.min(1, maxDim / Math.max(sw, sh));
        var pw = Math.max(50, Math.round(sw * previewScale));
        var ph = Math.max(50, Math.round(sh * previewScale));

        // Scale outline params to preview space
        var pDist = distancePx * previewScale;
        var pWidth = Math.max(2, widthPx * previewScale);
        var pCorner = cornerRadiusPx * previewScale;

        // Create silhouette at preview scale
        var silCanvas = document.createElement("canvas");
        silCanvas.width = pw;
        silCanvas.height = ph;
        var silCtx = silCanvas.getContext("2d");
        silCtx.drawImage(cropImg, sx, sy, sw, sh, 0, 0, pw, ph);
        makeSilhouette(silCtx, pw, ph);

        // Compute integer radii and ensure ring is at least 2px wide
        var outerR = Math.round(pDist + pWidth + pCorner);
        var innerR = Math.round(pDist + pCorner);
        if (outerR - innerR < 2) outerR = innerR + 2;

        // Expand for outer and inner boundaries using morphological dilation
        var outerCanvas = expandMask(silCanvas, pw, ph, outerR);
        var innerCanvas = expandMask(silCanvas, pw, ph, innerR);

        // Subtract inner from outer to get the outline ring
        var outCanvas = document.createElement("canvas");
        outCanvas.width = pw;
        outCanvas.height = ph;
        var outCtx = outCanvas.getContext("2d");
        outCtx.drawImage(outerCanvas, 0, 0);
        outCtx.globalCompositeOperation = "destination-out";
        outCtx.drawImage(innerCanvas, 0, 0);
        outCtx.globalCompositeOperation = "source-over";

        // Color the ring magenta for visibility on the crop overlay
        outCtx.globalCompositeOperation = "source-in";
        outCtx.fillStyle = "rgba(255, 0, 100, 0.85)";
        outCtx.fillRect(0, 0, pw, ph);
        outCtx.globalCompositeOperation = "source-over";

        // Create overlay element
        var el = document.createElement("canvas");
        el.width = pw;
        el.height = ph;
        el.style.cssText = "position:absolute;pointer-events:none;z-index:5;" +
            "left:" + cropState.x + "px;top:" + cropState.y + "px;" +
            "width:" + cropState.w + "px;height:" + cropState.h + "px;";
        el.getContext("2d").drawImage(outCanvas, 0, 0);

        cropWrapper.appendChild(el);
        outlineOverlayEl = el;
    }

    function clearOutlinePreview() {
        if (outlineOverlayEl) {
            outlineOverlayEl.remove();
            outlineOverlayEl = null;
        }
        lastPreviewParams = null;
    }

    // ══════════════════════════════════════════
    // ── Outline Generation (Morphological Dilation) ──
    // ══════════════════════════════════════════

    // Convert canvas to a binary white silhouette (solid white where alpha > 25, transparent elsewhere).
    function makeSilhouette(ctx, w, h) {
        var data = ctx.getImageData(0, 0, w, h);
        var d = data.data;
        for (var i = 0; i < w * h; i++) {
            if (d[i * 4 + 3] > 25) {
                d[i * 4] = 255; d[i * 4 + 1] = 255; d[i * 4 + 2] = 255; d[i * 4 + 3] = 255;
            } else {
                d[i * 4] = 0; d[i * 4 + 1] = 0; d[i * 4 + 2] = 0; d[i * 4 + 3] = 0;
            }
        }
        ctx.putImageData(data, 0, 0);
    }

    // Expand a binary mask by radiusPx using iterative 8-connected morphological dilation.
    // Each iteration shifts the image 1px in all 8 directions (cardinal + diagonal) and composites,
    // producing smooth, uniform Chebyshev-distance expansion ideal for cutting machine contours.
    // Uses double-buffer (only 2 canvases) for memory efficiency.
    function expandMask(srcCanvas, w, h, radiusPx) {
        var r = Math.round(radiusPx);
        if (r <= 0) {
            var c = document.createElement("canvas");
            c.width = w; c.height = h;
            c.getContext("2d").drawImage(srcCanvas, 0, 0);
            return c;
        }

        // Double-buffer: alternate between two canvases
        var bufA = document.createElement("canvas");
        bufA.width = w; bufA.height = h;
        var bufB = document.createElement("canvas");
        bufB.width = w; bufB.height = h;
        var ctxA = bufA.getContext("2d");
        var ctxB = bufB.getContext("2d");

        // Seed buffer A with the source mask
        ctxA.drawImage(srcCanvas, 0, 0);

        for (var i = 0; i < r; i++) {
            var readBuf = (i % 2 === 0) ? bufA : bufB;
            var writeCtx = (i % 2 === 0) ? ctxB : ctxA;

            writeCtx.clearRect(0, 0, w, h);
            // 8-connectivity: center + 4 cardinal + 4 diagonal = 9 draws per iteration
            writeCtx.drawImage(readBuf, 0, 0);    // center
            writeCtx.drawImage(readBuf, -1, 0);   // W
            writeCtx.drawImage(readBuf, 1, 0);    // E
            writeCtx.drawImage(readBuf, 0, -1);   // N
            writeCtx.drawImage(readBuf, 0, 1);    // S
            writeCtx.drawImage(readBuf, -1, -1);  // NW
            writeCtx.drawImage(readBuf, 1, -1);   // NE
            writeCtx.drawImage(readBuf, -1, 1);   // SW
            writeCtx.drawImage(readBuf, 1, 1);    // SE
        }

        // Result is in the buffer that was last written to
        return (r % 2 !== 0) ? bufB : bufA;
    }

    // ══════════════════════════════════════════
    // ── Marching Squares Contour Tracer ──
    // ══════════════════════════════════════════

    // Trace contour paths from a binary alpha mask using marching squares.
    // Returns an array of polygons, each polygon is an array of {x, y} points.
    function traceContours(imageData, w, h, threshold) {
        threshold = threshold || 128;
        var d = imageData.data;

        // Build binary grid (1 = opaque, 0 = transparent) with 1px border of 0s
        var gw = w + 1, gh = h + 1;
        function sample(px, py) {
            if (px < 0 || px >= w || py < 0 || py >= h) return 0;
            return d[(py * w + px) * 4 + 3] >= threshold ? 1 : 0;
        }

        var visited = {};
        var contours = [];

        for (var cy = 0; cy < h; cy++) {
            for (var cx = 0; cx < w; cx++) {
                // Look for boundary: current pixel is solid, left neighbor is not
                if (sample(cx, cy) === 1 && sample(cx - 1, cy) === 0) {
                    var key = cx + "," + cy;
                    if (visited[key]) continue;

                    // Trace contour using Moore-Neighbor boundary tracing
                    var contour = traceBoundary(cx, cy, w, h, sample, visited);
                    if (contour && contour.length >= 3) {
                        contours.push(contour);
                    }
                }
            }
        }

        return contours;
    }

    function traceBoundary(startX, startY, w, h, sample, visited) {
        // 8-connected Moore neighborhood boundary tracing
        // Direction offsets: 0=E, 1=SE, 2=S, 3=SW, 4=W, 5=NW, 6=N, 7=NE
        var dx = [1, 1, 0, -1, -1, -1, 0, 1];
        var dy = [0, 1, 1, 1, 0, -1, -1, -1];

        var contour = [];
        var x = startX, y = startY;
        var dir = 4; // start looking west (since we found boundary from left)
        var maxSteps = w * h * 2;
        var steps = 0;

        do {
            contour.push({ x: x, y: y });
            visited[x + "," + y] = true;

            // Find next boundary pixel
            var startDir = (dir + 5) % 8; // backtrack: turn right from incoming direction
            var found = false;

            for (var i = 0; i < 8; i++) {
                var d = (startDir + i) % 8;
                var nx = x + dx[d];
                var ny = y + dy[d];

                if (nx >= 0 && nx < w && ny >= 0 && ny < h && sample(nx, ny) === 1) {
                    dir = d;
                    x = nx;
                    y = ny;
                    found = true;
                    break;
                }
            }

            if (!found) break;
            steps++;
        } while ((x !== startX || y !== startY) && steps < maxSteps);

        return contour;
    }

    // Douglas-Peucker path simplification
    function simplifyPath(points, tolerance) {
        if (points.length <= 2) return points;
        tolerance = tolerance || 1.5;

        var sqTol = tolerance * tolerance;

        function sqDistToSegment(p, a, b) {
            var abx = b.x - a.x, aby = b.y - a.y;
            var t = ((p.x - a.x) * abx + (p.y - a.y) * aby) / (abx * abx + aby * aby);
            t = Math.max(0, Math.min(1, t));
            var dx = p.x - (a.x + t * abx);
            var dy = p.y - (a.y + t * aby);
            return dx * dx + dy * dy;
        }

        function simplifyDP(pts, first, last, result) {
            var maxDist = 0, index = 0;
            for (var i = first + 1; i < last; i++) {
                var dist = sqDistToSegment(pts[i], pts[first], pts[last]);
                if (dist > maxDist) {
                    maxDist = dist;
                    index = i;
                }
            }
            if (maxDist > sqTol) {
                if (index - first > 1) simplifyDP(pts, first, index, result);
                result.push(pts[index]);
                if (last - index > 1) simplifyDP(pts, index, last, result);
            }
        }

        var result = [points[0]];
        simplifyDP(points, 0, points.length - 1, result);
        result.push(points[points.length - 1]);
        return result;
    }

    // Smooth a closed contour using Chaikin's corner-cutting algorithm
    function smoothContour(points, iterations) {
        iterations = iterations || 2;
        var pts = points;
        for (var iter = 0; iter < iterations; iter++) {
            var smoothed = [];
            var n = pts.length;
            for (var i = 0; i < n; i++) {
                var p0 = pts[i];
                var p1 = pts[(i + 1) % n];
                smoothed.push({ x: 0.75 * p0.x + 0.25 * p1.x, y: 0.75 * p0.y + 0.25 * p1.y });
                smoothed.push({ x: 0.25 * p0.x + 0.75 * p1.x, y: 0.25 * p0.y + 0.75 * p1.y });
            }
            pts = smoothed;
        }
        return pts;
    }

    // Generate SVG cut contour from a design image
    // Returns SVG string with vector cut paths at correct physical dimensions
    function generateCutContourSVG(designDataUrl, distancePx, cornerRadiusPx) {
        return new Promise(function (resolve) {
            var img = new Image();
            img.crossOrigin = "anonymous";
            img.onload = function () {
                var w = img.naturalWidth;
                var h = img.naturalHeight;

                // Step 1: Binary silhouette
                var silCanvas = document.createElement("canvas");
                silCanvas.width = w; silCanvas.height = h;
                var silCtx = silCanvas.getContext("2d");
                silCtx.drawImage(img, 0, 0);
                makeSilhouette(silCtx, w, h);

                // Step 2: Consolidate nearby elements then expand
                var closeRadius = Math.max(distancePx, 10);
                var consolidated = closeMask(silCanvas, w, h, closeRadius);
                var expandedCanvas = expandMask(consolidated, w, h, distancePx + cornerRadiusPx);

                // Step 3: Get pixel data and trace contours
                var expCtx = expandedCanvas.getContext("2d");
                var imageData = expCtx.getImageData(0, 0, w, h);
                var contours = traceContours(imageData, w, h, 128);

                // Step 4: Simplify and smooth contour paths
                var svgPaths = [];
                for (var i = 0; i < contours.length; i++) {
                    var simplified = simplifyPath(contours[i], 2.0);
                    var smoothed = smoothContour(simplified, 3);

                    if (smoothed.length < 3) continue;

                    // Build SVG path data
                    var pathD = "M " + smoothed[0].x.toFixed(2) + " " + smoothed[0].y.toFixed(2);
                    for (var j = 1; j < smoothed.length; j++) {
                        pathD += " L " + smoothed[j].x.toFixed(2) + " " + smoothed[j].y.toFixed(2);
                    }
                    pathD += " Z";
                    svgPaths.push(pathD);
                }

                // Step 5: Build SVG document
                // Use physical dimensions: pixels / DPI = inches
                var dpi = 300;
                var widthIn = (w / dpi).toFixed(4);
                var heightIn = (h / dpi).toFixed(4);

                var svg = '<?xml version="1.0" encoding="UTF-8"?>\n';
                svg += '<svg xmlns="http://www.w3.org/2000/svg" ';
                svg += 'width="' + widthIn + 'in" height="' + heightIn + 'in" ';
                svg += 'viewBox="0 0 ' + w + ' ' + h + '">\n';
                for (var k = 0; k < svgPaths.length; k++) {
                    svg += '  <path d="' + svgPaths[k] + '" fill="none" stroke="#000000" stroke-width="1" />\n';
                }
                svg += '</svg>';

                resolve(svg);
            };
            img.onerror = function () { resolve(null); };
            img.src = designDataUrl;
        });
    }

    // Generate a full sheet SVG with multiple design cut contours placed in layout
    function generateCutSheetSVG(designEntries, sheetW, sheetH) {
        if (!designEntries || designEntries.length === 0) return Promise.resolve(null);

        var tileList = buildTileList(designEntries);

        // Collect all unique design URLs
        var urls = [];
        for (var j = 0; j < designEntries.length; j++) {
            urls.push(designEntries[j].designUrl);
        }

        return loadImages(urls).then(function (imageMap) {
            // For each unique design, generate contour paths
            var contourPromises = [];
            var contourMap = {};
            var seen = {};

            for (var i = 0; i < designEntries.length; i++) {
                var entry = designEntries[i];
                var key = entry.designUrl + "|" + (entry.cutGapPx || 15) + "|" + (entry.cornerRadiusPx || 20);
                if (!seen[key]) {
                    seen[key] = true;
                    (function (k, dUrl, gap, cr) {
                        contourPromises.push(
                            generateContourPaths(dUrl, gap, cr).then(function (paths) {
                                contourMap[k] = paths;
                            })
                        );
                    })(key, entry.designUrl, entry.cutGapPx || 15, entry.cornerRadiusPx || 20);
                }
            }

            return Promise.all(contourPromises).then(function () {
                // Layout tiles and build SVG
                var dpi = 300;
                var widthIn = (sheetW / dpi).toFixed(4);
                var heightIn = (sheetH / dpi).toFixed(4);

                var svg = '<?xml version="1.0" encoding="UTF-8"?>\n';
                svg += '<svg xmlns="http://www.w3.org/2000/svg" ';
                svg += 'width="' + widthIn + 'in" height="' + heightIn + 'in" ';
                svg += 'viewBox="0 0 ' + sheetW + ' ' + sheetH + '">\n';

                var x = 0, y = 0, rowHeight = 0, lastSpaceY = 0;

                for (var t = 0; t < tileList.length; t++) {
                    var tile = tileList[t];

                    if (x > 0 && x + tile.w > sheetW) {
                        y += rowHeight + lastSpaceY;
                        x = 0;
                        rowHeight = 0;
                        lastSpaceY = 0;
                    }

                    if (y + tile.h > sheetH) break;

                    var tileKey = tile.designUrl + "|" + (tile.cutGapPx || 15) + "|" + (tile.cornerRadiusPx || 20);
                    var paths = contourMap[tileKey];
                    if (paths && paths.length > 0) {
                        // Get original image dimensions for scaling
                        var imgEl = imageMap[tile.designUrl];
                        var srcW = imgEl ? imgEl.naturalWidth : tile.w;
                        var srcH = imgEl ? imgEl.naturalHeight : tile.h;
                        var scaleX = tile.w / srcW;
                        var scaleY = tile.h / srcH;

                        svg += '  <g transform="translate(' + x + ',' + y + ') scale(' + scaleX.toFixed(6) + ',' + scaleY.toFixed(6) + ')">\n';
                        for (var p = 0; p < paths.length; p++) {
                            svg += '    <path d="' + paths[p] + '" fill="none" stroke="#000000" stroke-width="' + (1 / Math.min(scaleX, scaleY)).toFixed(2) + '" />\n';
                        }
                        svg += '  </g>\n';
                    }

                    rowHeight = Math.max(rowHeight, tile.h);
                    lastSpaceY = Math.max(lastSpaceY, tile.sy);
                    x += tile.w + tile.sx;
                }

                // Add crop mark circles
                var markR = Math.round(3 / 25.4 * 300);
                var inset = Math.round(10 / 25.4 * 300);
                var positions = [
                    [inset, inset], [sheetW - inset, inset],
                    [inset, sheetH - inset], [sheetW - inset, sheetH - inset]
                ];
                for (var m = 0; m < positions.length; m++) {
                    svg += '  <circle cx="' + positions[m][0] + '" cy="' + positions[m][1] + '" r="' + markR + '" fill="#000000" />\n';
                }

                svg += '</svg>';
                return svg;
            });
        });
    }

    // Helper: generate contour paths (returns array of SVG path d-strings) from a design image
    function generateContourPaths(designDataUrl, distancePx, cornerRadiusPx) {
        return new Promise(function (resolve) {
            var img = new Image();
            img.crossOrigin = "anonymous";
            img.onload = function () {
                var w = img.naturalWidth;
                var h = img.naturalHeight;

                var silCanvas = document.createElement("canvas");
                silCanvas.width = w; silCanvas.height = h;
                var silCtx = silCanvas.getContext("2d");
                silCtx.drawImage(img, 0, 0);
                makeSilhouette(silCtx, w, h);

                // Consolidate nearby elements
                var closeRadius = Math.max(distancePx, 10);
                var consolidated = closeMask(silCanvas, w, h, closeRadius);

                var expandedCanvas = expandMask(consolidated, w, h, distancePx + cornerRadiusPx);
                var expCtx = expandedCanvas.getContext("2d");
                var imageData = expCtx.getImageData(0, 0, w, h);
                var contours = traceContours(imageData, w, h, 128);

                var svgPaths = [];
                for (var i = 0; i < contours.length; i++) {
                    var simplified = simplifyPath(contours[i], 2.0);
                    var smoothed = smoothContour(simplified, 3);
                    if (smoothed.length < 3) continue;

                    var pathD = "M " + smoothed[0].x.toFixed(2) + " " + smoothed[0].y.toFixed(2);
                    for (var j = 1; j < smoothed.length; j++) {
                        pathD += " L " + smoothed[j].x.toFixed(2) + " " + smoothed[j].y.toFixed(2);
                    }
                    pathD += " Z";
                    svgPaths.push(pathD);
                }

                resolve(svgPaths);
            };
            img.onerror = function () { resolve([]); };
            img.src = designDataUrl;
        });
    }

    function downloadSvgBlob(svgString, filename) {
        if (!svgString) return;
        var blob = new Blob([svgString], { type: "image/svg+xml" });
        downloadBlob(blob, filename);
    }

    function downloadCutSVGMulti(filename, designEntries, sheetW, sheetH) {
        return generateCutSheetSVG(designEntries, sheetW, sheetH).then(function (svg) {
            downloadSvgBlob(svg, filename);
        });
    }

    // Morphological erode: shrink a binary mask by radiusPx
    // Inverse of expandMask - erodes by checking if ALL pixels in the neighborhood are set
    function erodeMask(srcCanvas, w, h, radiusPx) {
        var r = Math.round(radiusPx);
        if (r <= 0) {
            var c = document.createElement("canvas");
            c.width = w; c.height = h;
            c.getContext("2d").drawImage(srcCanvas, 0, 0);
            return c;
        }

        // Erode = invert -> dilate -> invert
        // Invert alpha: opaque becomes transparent, transparent becomes opaque
        var invCanvas = document.createElement("canvas");
        invCanvas.width = w; invCanvas.height = h;
        var invCtx = invCanvas.getContext("2d");
        invCtx.drawImage(srcCanvas, 0, 0);
        var invData = invCtx.getImageData(0, 0, w, h);
        var invD = invData.data;
        for (var i = 0; i < w * h; i++) {
            var a = invD[i * 4 + 3];
            if (a > 128) {
                invD[i * 4] = 0; invD[i * 4 + 1] = 0; invD[i * 4 + 2] = 0; invD[i * 4 + 3] = 0;
            } else {
                invD[i * 4] = 255; invD[i * 4 + 1] = 255; invD[i * 4 + 2] = 255; invD[i * 4 + 3] = 255;
            }
        }
        invCtx.putImageData(invData, 0, 0);

        // Dilate the inverted mask
        var dilatedInv = expandMask(invCanvas, w, h, r);

        // Invert back
        var resultCanvas = document.createElement("canvas");
        resultCanvas.width = w; resultCanvas.height = h;
        var resultCtx = resultCanvas.getContext("2d");
        resultCtx.drawImage(dilatedInv, 0, 0);
        var resultData = resultCtx.getImageData(0, 0, w, h);
        var rD = resultData.data;
        for (var j = 0; j < w * h; j++) {
            var a2 = rD[j * 4 + 3];
            if (a2 > 128) {
                rD[j * 4] = 0; rD[j * 4 + 1] = 0; rD[j * 4 + 2] = 0; rD[j * 4 + 3] = 0;
            } else {
                rD[j * 4] = 255; rD[j * 4 + 1] = 255; rD[j * 4 + 2] = 255; rD[j * 4 + 3] = 255;
            }
        }
        resultCtx.putImageData(resultData, 0, 0);
        return resultCanvas;
    }

    // Morphological close: dilate then erode by the same radius.
    // Fills gaps between nearby elements, merging them into one connected shape.
    function closeMask(srcCanvas, w, h, radiusPx) {
        var dilated = expandMask(srcCanvas, w, h, radiusPx);
        return erodeMask(dilated, w, h, radiusPx);
    }

    // Smooth a binary mask using blur + threshold for rounded edges.
    // Returns a new canvas with a smooth, rounded binary mask.
    function smoothBinaryMask(srcCanvas, w, h, blurRadius) {
        if (blurRadius <= 0) {
            var c = document.createElement("canvas");
            c.width = w; c.height = h;
            c.getContext("2d").drawImage(srcCanvas, 0, 0);
            return c;
        }
        var smoothCanvas = document.createElement("canvas");
        smoothCanvas.width = w; smoothCanvas.height = h;
        var ctx = smoothCanvas.getContext("2d");
        ctx.filter = "blur(" + blurRadius + "px)";
        ctx.drawImage(srcCanvas, 0, 0);
        ctx.filter = "none";

        // Re-threshold: blur produces soft alpha, snap back to binary
        var imgData = ctx.getImageData(0, 0, w, h);
        var d = imgData.data;
        for (var i = 0; i < w * h; i++) {
            d[i * 4 + 3] = d[i * 4 + 3] > 100 ? 255 : 0;
            if (d[i * 4 + 3] === 255) {
                d[i * 4] = 255; d[i * 4 + 1] = 255; d[i * 4 + 2] = 255;
            } else {
                d[i * 4] = 0; d[i * 4 + 1] = 0; d[i * 4 + 2] = 0;
            }
        }
        ctx.putImageData(imgData, 0, 0);
        return smoothCanvas;
    }

    // Generate the cut outline: a smooth, rounded ring around the design silhouette.
    // Uses blur-based smoothing for naturally rounded contours with no sharp edges.
    function generateOutline(designDataUrl, distancePx, cornerRadiusPx, widthPx) {
        return new Promise(function (resolve) {
            var img = new Image();
            img.crossOrigin = "anonymous";
            img.onload = function () {
                var w = img.naturalWidth;
                var h = img.naturalHeight;

                // Step 1: Binary silhouette
                var silCanvas = document.createElement("canvas");
                silCanvas.width = w; silCanvas.height = h;
                var silCtx = silCanvas.getContext("2d");
                silCtx.drawImage(img, 0, 0);
                makeSilhouette(silCtx, w, h);

                // Step 2: Morphological close to merge nearby elements (icon + text)
                var closeRadius = Math.max(distancePx, 10);
                var consolidatedCanvas = closeMask(silCanvas, w, h, closeRadius);

                // Step 3: Smooth the consolidated mask to round all edges
                var smoothed = smoothBinaryMask(consolidatedCanvas, w, h, cornerRadiusPx);

                // Step 4: Expand for outer boundary
                var outerExpand = expandMask(smoothed, w, h, distancePx + widthPx);
                var outerSmooth = smoothBinaryMask(outerExpand, w, h, Math.max(cornerRadiusPx, 3));

                // Step 5: Expand for inner boundary
                var innerExpand = expandMask(smoothed, w, h, distancePx);
                var innerSmooth = smoothBinaryMask(innerExpand, w, h, Math.max(cornerRadiusPx, 3));

                // Step 6: Subtract inner from outer to get the outline ring
                var outCanvas = document.createElement("canvas");
                outCanvas.width = w; outCanvas.height = h;
                var outCtx = outCanvas.getContext("2d");
                outCtx.drawImage(outerSmooth, 0, 0);
                outCtx.globalCompositeOperation = "destination-out";
                outCtx.drawImage(innerSmooth, 0, 0);
                outCtx.globalCompositeOperation = "source-over";

                // Step 7: Color the ring solid black
                outCtx.globalCompositeOperation = "source-in";
                outCtx.fillStyle = "#000000";
                outCtx.fillRect(0, 0, w, h);
                outCtx.globalCompositeOperation = "source-over";

                resolve(outCanvas.toDataURL("image/png"));
            };
            img.onerror = function () { resolve(null); };
            img.src = designDataUrl;
        });
    }

    // ══════════════════════════════════════════
    // ── Crop Marks ──
    // ══════════════════════════════════════════

    var CROP_MARK_DIA_PX = Math.round(6 / 25.4 * 300);   // 71px at 300 DPI
    var CROP_MARK_INSET_PX = Math.round(10 / 25.4 * 300); // 118px at 300 DPI

    function drawCropMarks(ctx, sheetW, sheetH, markDiaPx, insetPx) {
        var dia = markDiaPx || CROP_MARK_DIA_PX;
        var inset = insetPx || CROP_MARK_INSET_PX;
        var radius = dia / 2;

        var positions = [
            [inset, inset],
            [sheetW - inset, inset],
            [inset, sheetH - inset],
            [sheetW - inset, sheetH - inset]
        ];

        ctx.fillStyle = "#000000";
        for (var i = 0; i < positions.length; i++) {
            ctx.beginPath();
            ctx.arc(positions[i][0], positions[i][1], radius, 0, Math.PI * 2);
            ctx.fill();
        }
    }

    function addCropMarksToFabric(canvas, sheetW, sheetH) {
        var dia = CROP_MARK_DIA_PX;
        var inset = CROP_MARK_INSET_PX;
        var radius = dia / 2;

        var positions = [
            [inset, inset],
            [sheetW - inset, inset],
            [inset, sheetH - inset],
            [sheetW - inset, sheetH - inset]
        ];

        for (var i = 0; i < positions.length; i++) {
            canvas.add(new fabric.Circle({
                left: positions[i][0] - radius,
                top: positions[i][1] - radius,
                radius: radius,
                fill: "#000000",
                selectable: false,
                evented: false,
            }));
        }
    }

    // ══════════════════════════════════════════
    // ── Sheet Rendering (Fabric.js) ──
    // ══════════════════════════════════════════

    function setViewMode(mode) {
        viewMode = mode === "cut" ? "cut" : "print";
        if (lastRenderParams) {
            var p = lastRenderParams;
            if (p.isMulti) {
                renderSheetMulti(p.canvasId, p.designEntries, p.sheetW, p.sheetH);
            } else {
                renderSheet(p.canvasId, p.designDataUrl, p.outlineDataUrl,
                    p.sheetW, p.sheetH, p.designW, p.designH,
                    p.spacing, p.quantity, p.cols, p.rows, p.offsetX, p.offsetY);
            }
        }
    }

    function initSheet(canvasId) {
        if (sheetCanvas) {
            try { sheetCanvas.dispose(); } catch (e) { }
            sheetCanvas = null;
        }
        if (sheetResizeObserver) {
            sheetResizeObserver.disconnect();
            sheetResizeObserver = null;
        }
        var el = document.getElementById(canvasId);
        if (!el) return;

        sheetCanvas = new fabric.Canvas(canvasId, {
            width: 1, height: 1,
            backgroundColor: "#ffffff",
            selection: false, renderOnAddRemove: false,
        });

        var observeTarget = el.closest(".sc-sheet-container") || el.parentElement;
        if (observeTarget) {
            sheetResizeObserver = new ResizeObserver(function () {
                if (sheetCanvas) {
                    var canvasEl = document.getElementById(canvasId);
                    if (canvasEl) fitCanvasToContainer(sheetCanvas, canvasEl);
                }
            });
            sheetResizeObserver.observe(observeTarget);
        }
    }

    function renderSheet(canvasId, designDataUrl, outlineDataUrl, sheetW, sheetH, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY) {
        lastRenderParams = {
            canvasId: canvasId, designDataUrl: designDataUrl, outlineDataUrl: outlineDataUrl,
            sheetW: sheetW, sheetH: sheetH, designW: designW, designH: designH,
            spacing: spacing, quantity: quantity, cols: cols, rows: rows,
            offsetX: offsetX, offsetY: offsetY
        };

        if (!sheetCanvas) initSheet(canvasId);
        if (!sheetCanvas) return Promise.resolve();

        sheetCanvas.setDimensions({ width: sheetW, height: sheetH }, { backstoreOnly: true });
        var sheetEl = document.getElementById(canvasId);
        if (sheetEl) fitCanvasToContainer(sheetCanvas, sheetEl);
        sheetCanvas.clear();
        sheetCanvas.backgroundColor = "#ffffff";

        // Load both images for cut mode (design + outline overlay)
        var urlsToLoad = [designDataUrl, outlineDataUrl].filter(Boolean);
        if (urlsToLoad.length === 0) {
            sheetCanvas.renderAll();
            return Promise.resolve();
        }

        return loadImages(urlsToLoad).then(function (imageMap) {
            var count = 0;
            for (var r = 0; r < rows && count < quantity; r++) {
                for (var c = 0; c < cols && count < quantity; c++) {
                    var x = offsetX + c * (designW + spacing);
                    var y = offsetY + r * (designH + spacing);

                    // Preview: always show design + outline overlay together
                    var dImg = imageMap[designDataUrl];
                    if (dImg) {
                        sheetCanvas.add(new fabric.Image(dImg, {
                            left: x, top: y,
                            scaleX: designW / dImg.naturalWidth,
                            scaleY: designH / dImg.naturalHeight,
                            selectable: false, evented: false,
                        }));
                    }
                    var oImg = imageMap[outlineDataUrl];
                    if (oImg) {
                        sheetCanvas.add(new fabric.Image(oImg, {
                            left: x, top: y,
                            scaleX: designW / oImg.naturalWidth,
                            scaleY: designH / oImg.naturalHeight,
                            selectable: false, evented: false,
                        }));
                    }

                    count++;
                }
            }

            addCropMarksToFabric(sheetCanvas, sheetW, sheetH);
            sheetCanvas.renderAll();
            requestAnimationFrame(function () {
                var canvasEl = document.getElementById(canvasId);
                if (canvasEl && sheetCanvas) fitCanvasToContainer(sheetCanvas, canvasEl);
            });
        });
    }

    // ══════════════════════════════════════════
    // ── Export ──
    // ══════════════════════════════════════════

    function renderOffscreen(mode, sheetW, sheetH, designDataUrl, outlineDataUrl, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY) {
        return new Promise(function (resolve) {
            var canvas = document.createElement("canvas");
            canvas.width = sheetW;
            canvas.height = sheetH;
            var ctx = canvas.getContext("2d");

            // White background
            ctx.fillStyle = "#ffffff";
            ctx.fillRect(0, 0, sheetW, sheetH);

            // Print mode: designs only + crop marks
            // Cut mode: outlines only + crop marks
            var srcUrl = mode === "print" ? designDataUrl : outlineDataUrl;
            if (!srcUrl) {
                drawCropMarks(ctx, sheetW, sheetH);
                canvas.toBlob(function (blob) { resolve(blob); }, "image/png");
                return;
            }

            var img = new Image();
            img.crossOrigin = "anonymous";
            img.onload = function () {
                var count = 0;
                for (var r = 0; r < rows && count < quantity; r++) {
                    for (var c = 0; c < cols && count < quantity; c++) {
                        var x = offsetX + c * (designW + spacing);
                        var y = offsetY + r * (designH + spacing);
                        ctx.drawImage(img, 0, 0, img.naturalWidth, img.naturalHeight, x, y, designW, designH);
                        count++;
                    }
                }
                drawCropMarks(ctx, sheetW, sheetH);
                canvas.toBlob(function (blob) { resolve(blob); }, "image/png");
            };
            img.onerror = function () {
                drawCropMarks(ctx, sheetW, sheetH);
                canvas.toBlob(function (blob) { resolve(blob); }, "image/png");
            };
            img.src = srcUrl;
        });
    }

    function downloadBlob(blob, filename) {
        if (!blob) return;
        var url = URL.createObjectURL(blob);
        var a = document.createElement("a");
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(url); }, 5000);
    }

    function downloadBoth(printFilename, cutFilename, sheetW, sheetH, designDataUrl, outlineDataUrl, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY) {
        // Render both sheets first, then trigger downloads sequentially
        return Promise.all([
            renderOffscreen("print", sheetW, sheetH, designDataUrl, outlineDataUrl, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY),
            renderOffscreen("cut", sheetW, sheetH, designDataUrl, outlineDataUrl, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY)
        ]).then(function (blobs) {
            downloadBlob(blobs[0], printFilename);
            return new Promise(function (resolve) {
                setTimeout(function () {
                    downloadBlob(blobs[1], cutFilename);
                    resolve();
                }, 500);
            });
        });
    }

    function downloadSingle(mode, filename, sheetW, sheetH, designDataUrl, outlineDataUrl, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY) {
        return renderOffscreen(mode, sheetW, sheetH, designDataUrl, outlineDataUrl, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY)
            .then(function (blob) {
                downloadBlob(blob, filename);
            });
    }

    function downloadDesignPng(dataUrl, filename) {
        if (!dataUrl) return;
        var a = document.createElement("a");
        a.href = dataUrl;
        a.download = filename || "design.png";
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    // ── Fit canvas to container ──

    function fitCanvasToContainer(canvas, el) {
        if (!canvas || !el) return;
        var container = el.closest(".sc-sheet-container");
        if (!container) container = el.parentElement;
        if (!container) return;

        var style = getComputedStyle(container);
        var padX = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
        var padY = (parseFloat(style.paddingTop) || 0) + (parseFloat(style.paddingBottom) || 0);
        var availW = container.clientWidth - padX;
        var availH = container.clientHeight - padY;
        if (availW <= 0 || availH <= 0) return;

        var cw = canvas.getWidth();
        var ch = canvas.getHeight();
        if (cw <= 0 || ch <= 0) return;

        var scale = Math.min(availW / cw, availH / ch);
        canvas.setDimensions({ width: Math.round(cw * scale), height: Math.round(ch * scale) }, { cssOnly: true });
    }

    // ══════════════════════════════════════════
    // ── Multi-Image Rendering ──
    // ══════════════════════════════════════════

    function loadImages(urls) {
        var unique = [];
        var seen = {};
        for (var i = 0; i < urls.length; i++) {
            if (!seen[urls[i]]) {
                seen[urls[i]] = true;
                unique.push(urls[i]);
            }
        }
        return Promise.all(unique.map(function (url) {
            return new Promise(function (resolve) {
                var img = new Image();
                img.crossOrigin = "anonymous";
                img.onload = function () { resolve({ url: url, img: img }); };
                img.onerror = function () { resolve({ url: url, img: null }); };
                img.src = url;
            });
        })).then(function (results) {
            var map = {};
            for (var i = 0; i < results.length; i++) {
                map[results[i].url] = results[i].img;
            }
            return map;
        });
    }

    // Row-packing renderSheetMulti — each entry has its own widthPx/heightPx/spaceXPx/spaceYPx
    function renderSheetMulti(canvasId, designEntries, sheetW, sheetH) {
        lastRenderParams = {
            canvasId: canvasId, designEntries: designEntries, isMulti: true,
            sheetW: sheetW, sheetH: sheetH
        };

        if (!sheetCanvas) initSheet(canvasId);
        if (!sheetCanvas) return Promise.resolve();

        sheetCanvas.setDimensions({ width: sheetW, height: sheetH }, { backstoreOnly: true });
        var sheetEl = document.getElementById(canvasId);
        if (sheetEl) fitCanvasToContainer(sheetCanvas, sheetEl);
        sheetCanvas.clear();
        sheetCanvas.backgroundColor = "#ffffff";

        if (!designEntries || designEntries.length === 0) {
            addCropMarksToFabric(sheetCanvas, sheetW, sheetH);
            sheetCanvas.renderAll();
            return Promise.resolve();
        }

        // Build flat tile list with per-tile dimensions
        var tileList = buildTileList(designEntries);

        // Collect all unique URLs for preloading
        var urls = [];
        for (var j = 0; j < designEntries.length; j++) {
            urls.push(designEntries[j].designUrl);
            urls.push(designEntries[j].outlineUrl);
        }

        return loadImages(urls).then(function (imageMap) {
            var x = 0, y = 0, rowHeight = 0, lastSpaceY = 0;

            for (var t = 0; t < tileList.length; t++) {
                var tile = tileList[t];

                if (x > 0 && x + tile.w > sheetW) {
                    y += rowHeight + lastSpaceY;
                    x = 0;
                    rowHeight = 0;
                    lastSpaceY = 0;
                }

                if (y + tile.h > sheetH) break;

                // Preview: always show design + outline overlay together
                var designImg = imageMap[tile.designUrl];
                if (designImg) {
                    sheetCanvas.add(new fabric.Image(designImg, {
                        left: x, top: y,
                        scaleX: tile.w / designImg.naturalWidth,
                        scaleY: tile.h / designImg.naturalHeight,
                        selectable: false, evented: false,
                    }));
                }
                var outlineImg = imageMap[tile.outlineUrl];
                if (outlineImg) {
                    sheetCanvas.add(new fabric.Image(outlineImg, {
                        left: x, top: y,
                        scaleX: tile.w / outlineImg.naturalWidth,
                        scaleY: tile.h / outlineImg.naturalHeight,
                        selectable: false, evented: false,
                    }));
                }

                rowHeight = Math.max(rowHeight, tile.h);
                lastSpaceY = Math.max(lastSpaceY, tile.sy);
                x += tile.w + tile.sx;
            }

            addCropMarksToFabric(sheetCanvas, sheetW, sheetH);
            sheetCanvas.renderAll();
            requestAnimationFrame(function () {
                var canvasEl = document.getElementById(canvasId);
                if (canvasEl && sheetCanvas) fitCanvasToContainer(sheetCanvas, canvasEl);
            });
        });
    }

    function buildTileList(designEntries) {
        var tileList = [];
        for (var i = 0; i < designEntries.length; i++) {
            var e = designEntries[i];
            for (var q = 0; q < e.quantity; q++) {
                tileList.push({
                    designUrl: e.designUrl,
                    outlineUrl: e.outlineUrl,
                    w: e.widthPx || 300,
                    h: e.heightPx || 400,
                    sx: e.spaceXPx || 0,
                    sy: e.spaceYPx || 0,
                    cutGapPx: e.cutGapPx || 15,
                    cornerRadiusPx: e.cornerRadiusPx || 20
                });
            }
        }
        return tileList;
    }

    function renderOffscreenMulti(mode, designEntries, sheetW, sheetH) {
        return new Promise(function (resolve) {
            var canvas = document.createElement("canvas");
            canvas.width = sheetW;
            canvas.height = sheetH;
            var ctx = canvas.getContext("2d");

            ctx.fillStyle = "#ffffff";
            ctx.fillRect(0, 0, sheetW, sheetH);

            if (!designEntries || designEntries.length === 0) {
                drawCropMarks(ctx, sheetW, sheetH);
                canvas.toBlob(function (blob) { resolve(blob); }, "image/png");
                return;
            }

            var tileList = buildTileList(designEntries);

            var urls = [];
            for (var j = 0; j < designEntries.length; j++) {
                urls.push(designEntries[j].designUrl);
                urls.push(designEntries[j].outlineUrl);
            }

            loadImages(urls).then(function (imageMap) {
                var x = 0, y = 0, rowHeight = 0, lastSpaceY = 0;

                for (var t = 0; t < tileList.length; t++) {
                    var tile = tileList[t];

                    if (x > 0 && x + tile.w > sheetW) {
                        y += rowHeight + lastSpaceY;
                        x = 0;
                        rowHeight = 0;
                        lastSpaceY = 0;
                    }

                    if (y + tile.h > sheetH) break;

                    var srcUrl = mode === "print" ? tile.designUrl : tile.outlineUrl;
                    var imgEl = imageMap[srcUrl];
                    if (imgEl) {
                        ctx.drawImage(imgEl, 0, 0, imgEl.naturalWidth, imgEl.naturalHeight, x, y, tile.w, tile.h);
                    }

                    rowHeight = Math.max(rowHeight, tile.h);
                    lastSpaceY = Math.max(lastSpaceY, tile.sy);
                    x += tile.w + tile.sx;
                }

                drawCropMarks(ctx, sheetW, sheetH);
                canvas.toBlob(function (blob) { resolve(blob); }, "image/png");
            });
        });
    }

    function downloadBothMulti(printFilename, cutFilename, designEntries, sheetW, sheetH) {
        return Promise.all([
            renderOffscreenMulti("print", designEntries, sheetW, sheetH),
            renderOffscreenMulti("cut", designEntries, sheetW, sheetH)
        ]).then(function (blobs) {
            downloadBlob(blobs[0], printFilename);
            return new Promise(function (resolve) {
                setTimeout(function () {
                    downloadBlob(blobs[1], cutFilename);
                    resolve();
                }, 500);
            });
        });
    }

    function downloadSingleMulti(mode, filename, designEntries, sheetW, sheetH) {
        return renderOffscreenMulti(mode, designEntries, sheetW, sheetH)
            .then(function (blob) {
                downloadBlob(blob, filename);
            });
    }

    // Show composite preview of design + outline after Apply Crop
    function showCompositePreview(containerId, designDataUrl, outlineDataUrl) {
        var container = document.getElementById(containerId);
        if (!container) return Promise.resolve();
        container.innerHTML = "";

        return new Promise(function (resolve) {
            var designImg = new Image();
            designImg.crossOrigin = "anonymous";
            designImg.onload = function () {
                var outImg = new Image();
                outImg.crossOrigin = "anonymous";
                outImg.onload = function () {
                    var w = designImg.naturalWidth;
                    var h = designImg.naturalHeight;
                    var cvs = document.createElement("canvas");
                    cvs.width = w;
                    cvs.height = h;
                    var ctx = cvs.getContext("2d");

                    // Checkerboard background for transparency
                    var sz = Math.max(4, Math.round(w / 50));
                    for (var cy = 0; cy < h; cy += sz) {
                        for (var cx = 0; cx < w; cx += sz) {
                            ctx.fillStyle = ((Math.floor(cx / sz) + Math.floor(cy / sz)) % 2 === 0) ? "#e8e8e8" : "#d0d0d0";
                            ctx.fillRect(cx, cy, sz, sz);
                        }
                    }

                    // Draw design then outline
                    ctx.drawImage(designImg, 0, 0);
                    ctx.drawImage(outImg, 0, 0);

                    var displayImg = document.createElement("img");
                    var maxH = window.innerWidth <= 480 ? 200 : 300;
                    displayImg.style.cssText = "display:block;max-height:" + maxH + "px;max-width:100%;border-radius:6px;margin:0 auto;";
                    displayImg.src = cvs.toDataURL("image/png");
                    container.appendChild(displayImg);
                    resolve();
                };
                outImg.onerror = function () { resolve(); };
                outImg.src = outlineDataUrl;
            };
            designImg.onerror = function () { resolve(); };
            designImg.src = designDataUrl;
        });
    }

    function dispose() {
        disposeCropTool();
        if (sheetCanvas) { try { sheetCanvas.dispose(); } catch (e) { } sheetCanvas = null; }
        if (sheetResizeObserver) { sheetResizeObserver.disconnect(); sheetResizeObserver = null; }
        lastRenderParams = null;
    }

    // ── Get image dimensions from data URL ──
    function getImageDimensions(dataUrl) {
        return new Promise(function (resolve) {
            var img = new Image();
            img.onload = function () {
                resolve([img.naturalWidth, img.naturalHeight]);
            };
            img.onerror = function () {
                resolve([0, 0]);
            };
            img.src = dataUrl;
        });
    }

    // ── Composite design + outline into single image ──
    function compositeDesignAndOutline(designDataUrl, outlineDataUrl) {
        return new Promise(function (resolve) {
            var designImg = new Image();
            designImg.onload = function () {
                var outlineImg = new Image();
                outlineImg.onload = function () {
                    var w = Math.max(designImg.naturalWidth, outlineImg.naturalWidth);
                    var h = Math.max(designImg.naturalHeight, outlineImg.naturalHeight);
                    var canvas = document.createElement("canvas");
                    canvas.width = w;
                    canvas.height = h;
                    var ctx = canvas.getContext("2d");
                    // Draw checkerboard background for transparency
                    var sz = 8;
                    for (var y = 0; y < h; y += sz) {
                        for (var x = 0; x < w; x += sz) {
                            ctx.fillStyle = ((x / sz + y / sz) % 2 === 0) ? "#2a2a2a" : "#3a3a3a";
                            ctx.fillRect(x, y, sz, sz);
                        }
                    }
                    ctx.drawImage(designImg, 0, 0, w, h);
                    ctx.drawImage(outlineImg, 0, 0, w, h);
                    resolve(canvas.toDataURL("image/png"));
                };
                outlineImg.src = outlineDataUrl;
            };
            designImg.src = designDataUrl;
        });
    }

    // ── Download SVG string as file ──
    function downloadSvgString(svgContent, filename) {
        if (!svgContent) return;
        var blob = new Blob([svgContent], { type: "image/svg+xml" });
        downloadBlob(blob, filename || "cutline.svg");
    }

    // ── Public API ──
    return {
        initCropTool: initCropTool,
        setAspectRatio: setAspectRatio,
        swapCropImage: swapCropImage,
        extractCrop: extractCrop,
        disposeCropTool: disposeCropTool,
        updateOutlinePreview: updateOutlinePreview,
        clearOutlinePreview: clearOutlinePreview,
        generateOutline: generateOutline,
        generateCutContourSVG: generateCutContourSVG,
        generateCutSheetSVG: generateCutSheetSVG,
        downloadCutSVGMulti: downloadCutSVGMulti,
        setViewMode: setViewMode,
        renderSheet: renderSheet,
        renderSheetMulti: renderSheetMulti,
        showCompositePreview: showCompositePreview,
        downloadBoth: downloadBoth,
        downloadSingle: downloadSingle,
        downloadBothMulti: downloadBothMulti,
        downloadSingleMulti: downloadSingleMulti,
        downloadDesignPng: downloadDesignPng,
        getImageDimensions: getImageDimensions,
        compositeDesignAndOutline: compositeDesignAndOutline,
        downloadSvgString: downloadSvgString,
        dispose: dispose,
    };
})();
