// ===== ArtForge AI - Gang Sheet Module =====
window.gangSheet = (function () {
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
    var aspectRatio = 1; // width / height

    var sheetCanvas = null;
    var sheetResizeObserver = null;
    var transparentMode = true; // default: transparent (no white bg)

    // Cache last render params for re-rendering on bg toggle
    var lastRenderParams = null;

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

            // Dim overlays
            ["top", "bottom", "left", "right"].forEach(function (side) {
                var d = document.createElement("div");
                d.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;transition:none;";
                wrapper.appendChild(d);
                dimEls[side] = d;
            });

            // Crop box
            cropBoxEl = document.createElement("div");
            cropBoxEl.style.cssText = "position:absolute;border:2px dashed #00c8ff;cursor:move;box-sizing:border-box;overflow:hidden;";
            wrapper.appendChild(cropBoxEl);

            // Corner resize handles
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

            // Edge resize handles
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

            // Default crop: maximize within image, centered
            var defH = cropDisplayH * 0.85;
            var defW = defH * aspectRatio;
            if (defW > cropDisplayW * 0.95) {
                defW = cropDisplayW * 0.95;
                defH = defW / aspectRatio;
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
        if (ratio <= 0) return;
        aspectRatio = ratio;
        if (!cropBoxEl || !cropDisplayW) return;

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

        var offscreen = document.createElement("canvas");
        offscreen.width = targetWPx;
        offscreen.height = targetHPx;
        var ctx = offscreen.getContext("2d");
        // Ensure canvas starts fully transparent (no white)
        ctx.clearRect(0, 0, targetWPx, targetHPx);
        ctx.drawImage(cropImg, sx, sy, sw, sh, 0, 0, targetWPx, targetHPx);
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

    // ── Resize with dynamic aspect ratio lock ──

    function resizeCrop(handle, dx, dy, orig) {
        var nw, nh, nx, ny;

        if (handle === "br") {
            nw = Math.max(40, orig.w + dx);
            nh = nw / aspectRatio;
            nx = orig.x; ny = orig.y;
        } else if (handle === "bl") {
            nw = Math.max(40, orig.w - dx);
            nh = nw / aspectRatio;
            nx = orig.x + orig.w - nw; ny = orig.y;
        } else if (handle === "tr") {
            nw = Math.max(40, orig.w + dx);
            nh = nw / aspectRatio;
            nx = orig.x; ny = orig.y + orig.h - nh;
        } else if (handle === "tl") {
            nw = Math.max(40, orig.w - dx);
            nh = nw / aspectRatio;
            nx = orig.x + orig.w - nw; ny = orig.y + orig.h - nh;
        } else if (handle === "r") {
            nw = Math.max(40, orig.w + dx);
            nh = nw / aspectRatio;
            nx = orig.x; ny = orig.y + (orig.h - nh) / 2;
        } else if (handle === "l") {
            nw = Math.max(40, orig.w - dx);
            nh = nw / aspectRatio;
            nx = orig.x + orig.w - nw; ny = orig.y + (orig.h - nh) / 2;
        } else if (handle === "b") {
            nh = Math.max(40, orig.h + dy);
            nw = nh * aspectRatio;
            nx = orig.x + (orig.w - nw) / 2; ny = orig.y;
        } else if (handle === "t") {
            nh = Math.max(40, orig.h - dy);
            nw = nh * aspectRatio;
            nx = orig.x + (orig.w - nw) / 2; ny = orig.y + orig.h - nh;
        } else {
            return;
        }

        if (nx < 0) { nx = 0; }
        if (ny < 0) { ny = 0; }
        if (nx + nw > cropDisplayW) { nw = cropDisplayW - nx; nh = nw / aspectRatio; }
        if (ny + nh > cropDisplayH) { nh = cropDisplayH - ny; nw = nh * aspectRatio; }
        if (nw < 40) { nw = 40; nh = nw / aspectRatio; }

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
        cropContainer = null;
        cropWrapper = null;
        cropImg = null;
        cropBoxEl = null;
        dimEls = {};
        dragState = null;
    }

    function clamp(v, min, max) { return Math.max(min, Math.min(v, max)); }

    // ══════════════════════════════════════════
    // ── Gang Sheet Canvas (Fabric.js) ──
    // ══════════════════════════════════════════

    function setTransparentMode(on) {
        transparentMode = !!on;
        updateContainerCheckerboard();
        if (lastRenderParams) {
            var p = lastRenderParams;
            if (p.isMulti) {
                renderSheetMulti(p.canvasId, p.designEntries, p.sheetW, p.sheetH);
            } else {
                renderSheet(p.canvasId, p.designDataUrl, p.sheetW, p.sheetH, p.designW, p.designH,
                    p.spacing, p.quantity, p.cols, p.rows, p.offsetX, p.offsetY);
            }
        }
    }

    function updateContainerCheckerboard() {
        // No-op: checkerboard is now drawn inside the Fabric.js canvas
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
            backgroundColor: transparentMode ? null : "#ffffff",
            selection: false, renderOnAddRemove: false,
        });

        var observeTarget = el.closest(".gs-sheet-container") || el.parentElement;
        if (observeTarget) {
            sheetResizeObserver = new ResizeObserver(function () {
                if (sheetCanvas) {
                    var canvasEl = document.getElementById(canvasId);
                    if (canvasEl) fitCanvasToContainer(sheetCanvas, canvasEl);
                }
            });
            sheetResizeObserver.observe(observeTarget);
        }

        updateContainerCheckerboard();
    }

    function renderSheet(canvasId, designDataUrl, sheetW, sheetH, designW, designH, spacing, quantity, cols, rows, offsetX, offsetY) {
        // Cache params for re-render on bg toggle
        lastRenderParams = {
            canvasId: canvasId, designDataUrl: designDataUrl,
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

        // In transparent mode, draw checkerboard as canvas background pattern
        if (transparentMode) {
            var tileSize = Math.max(20, Math.round(sheetW / 60));
            var patternCanvas = document.createElement("canvas");
            patternCanvas.width = tileSize * 2;
            patternCanvas.height = tileSize * 2;
            var pctx = patternCanvas.getContext("2d");
            pctx.fillStyle = "#f0f0f0";
            pctx.fillRect(0, 0, tileSize * 2, tileSize * 2);
            pctx.fillStyle = "#d0d0d0";
            pctx.fillRect(0, 0, tileSize, tileSize);
            pctx.fillRect(tileSize, tileSize, tileSize, tileSize);
            sheetCanvas.backgroundColor = new fabric.Pattern({
                source: patternCanvas,
                repeat: "repeat",
            });
        }

        if (!designDataUrl) {
            sheetCanvas.renderAll();
            return Promise.resolve();
        }

        return new Promise(function (resolve) {
            var imgEl = new Image();
            imgEl.crossOrigin = "anonymous";
            imgEl.onload = function () {
                var count = 0;
                for (var r = 0; r < rows && count < quantity; r++) {
                    for (var c = 0; c < cols && count < quantity; c++) {
                        var x = offsetX + c * (designW + spacing);
                        var y = offsetY + r * (designH + spacing);
                        sheetCanvas.add(new fabric.Image(imgEl, {
                            left: x, top: y,
                            scaleX: designW / imgEl.naturalWidth,
                            scaleY: designH / imgEl.naturalHeight,
                            selectable: false, evented: false,
                        }));
                        count++;
                    }
                }
                sheetCanvas.renderAll();
                updateContainerCheckerboard();
                // Re-fit after browser layout reflow so container height is resolved
                requestAnimationFrame(function () {
                    var canvasEl = document.getElementById(canvasId);
                    if (canvasEl && sheetCanvas) fitCanvasToContainer(sheetCanvas, canvasEl);
                });
                resolve();
            };
            imgEl.onerror = function () { resolve(); };
            imgEl.src = designDataUrl;
        });
    }

    // ── Multi-image helpers ──

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

        if (transparentMode) {
            var tileSize = Math.max(20, Math.round(sheetW / 60));
            var patternCanvas = document.createElement("canvas");
            patternCanvas.width = tileSize * 2;
            patternCanvas.height = tileSize * 2;
            var pctx = patternCanvas.getContext("2d");
            pctx.fillStyle = "#f0f0f0";
            pctx.fillRect(0, 0, tileSize * 2, tileSize * 2);
            pctx.fillStyle = "#d0d0d0";
            pctx.fillRect(0, 0, tileSize, tileSize);
            pctx.fillRect(tileSize, tileSize, tileSize, tileSize);
            sheetCanvas.backgroundColor = new fabric.Pattern({
                source: patternCanvas,
                repeat: "repeat",
            });
        }

        if (!designEntries || designEntries.length === 0) {
            sheetCanvas.renderAll();
            return Promise.resolve();
        }

        // Build flat tile list with per-tile dimensions
        var tileList = [];
        for (var i = 0; i < designEntries.length; i++) {
            var e = designEntries[i];
            for (var q = 0; q < e.quantity; q++) {
                tileList.push({
                    dataUrl: e.dataUrl,
                    w: e.widthPx || 300,
                    h: e.heightPx || 400,
                    sx: e.spaceXPx || 0,
                    sy: e.spaceYPx || 0
                });
            }
        }

        // Collect unique URLs
        var urls = [];
        for (var j = 0; j < designEntries.length; j++) {
            urls.push(designEntries[j].dataUrl);
        }

        return loadImages(urls).then(function (imageMap) {
            // Row-packing: place tiles left-to-right, wrap to next row when full
            var x = 0, y = 0, rowHeight = 0, lastSpaceY = 0;

            for (var t = 0; t < tileList.length; t++) {
                var tile = tileList[t];

                // Wrap to new row if tile doesn't fit
                if (x > 0 && x + tile.w > sheetW) {
                    y += rowHeight + lastSpaceY;
                    x = 0;
                    rowHeight = 0;
                    lastSpaceY = 0;
                }

                // Stop if past bottom of sheet
                if (y + tile.h > sheetH) break;

                var imgEl = imageMap[tile.dataUrl];
                if (imgEl) {
                    sheetCanvas.add(new fabric.Image(imgEl, {
                        left: x, top: y,
                        scaleX: tile.w / imgEl.naturalWidth,
                        scaleY: tile.h / imgEl.naturalHeight,
                        selectable: false, evented: false,
                    }));
                }

                rowHeight = Math.max(rowHeight, tile.h);
                lastSpaceY = Math.max(lastSpaceY, tile.sy);
                x += tile.w + tile.sx;
            }

            sheetCanvas.renderAll();
            updateContainerCheckerboard();
            requestAnimationFrame(function () {
                var canvasEl = document.getElementById(canvasId);
                if (canvasEl && sheetCanvas) fitCanvasToContainer(sheetCanvas, canvasEl);
            });
        });
    }

    // Export sheet as PNG — always respects the current transparentMode
    function getSheetDataUrl(forceWhiteBg) {
        if (!sheetCanvas) return null;

        var origVpt = sheetCanvas.viewportTransform.slice();
        sheetCanvas.viewportTransform = [1, 0, 0, 1, 0, 0];

        var origBg = sheetCanvas.backgroundColor;
        if (forceWhiteBg) {
            sheetCanvas.backgroundColor = "#ffffff";
        }
        sheetCanvas.renderAll();

        var dataUrl = sheetCanvas.toDataURL({ format: "png", multiplier: 1 });

        // Restore
        sheetCanvas.backgroundColor = origBg;
        sheetCanvas.viewportTransform = origVpt;
        sheetCanvas.renderAll();
        return dataUrl;
    }

    function downloadSheetPng(filename, withWhiteBg) {
        if (!sheetCanvas) return;

        var origVpt = sheetCanvas.viewportTransform.slice();
        sheetCanvas.viewportTransform = [1, 0, 0, 1, 0, 0];

        var origBg = sheetCanvas.backgroundColor;
        if (withWhiteBg) {
            sheetCanvas.backgroundColor = "#ffffff";
        } else {
            sheetCanvas.backgroundColor = null;
        }
        sheetCanvas.renderAll();

        var fabricLower = sheetCanvas.lowerCanvasEl;
        if (!fabricLower) {
            var dataUrl = sheetCanvas.toDataURL({ format: "png", multiplier: 1 });
            sheetCanvas.backgroundColor = origBg;
            sheetCanvas.viewportTransform = origVpt;
            sheetCanvas.renderAll();
            triggerDownload(dataUrl, filename);
            return;
        }

        try {
            fabricLower.toBlob(function (blob) {
                sheetCanvas.backgroundColor = origBg;
                sheetCanvas.viewportTransform = origVpt;
                sheetCanvas.renderAll();
                if (!blob) return;
                var url = URL.createObjectURL(blob);
                var a = document.createElement("a");
                a.href = url;
                a.download = filename || "gang_sheet.png";
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                setTimeout(function () { URL.revokeObjectURL(url); }, 5000);
            }, "image/png");
        } catch (ex) {
            var dataUrl2 = sheetCanvas.toDataURL({ format: "png", multiplier: 1 });
            sheetCanvas.backgroundColor = origBg;
            sheetCanvas.viewportTransform = origVpt;
            sheetCanvas.renderAll();
            triggerDownload(dataUrl2, filename);
        }
    }

    // Download PDF with the gang sheet at correct physical dimensions
    function downloadSheetPdf(filename, sheetWidthIn, sheetHeightIn, withWhiteBg) {
        if (!sheetCanvas || typeof window.jspdf === "undefined") return;

        var origVpt = sheetCanvas.viewportTransform.slice();
        sheetCanvas.viewportTransform = [1, 0, 0, 1, 0, 0];
        var origBg = sheetCanvas.backgroundColor;
        if (withWhiteBg) {
            sheetCanvas.backgroundColor = "#ffffff";
        } else {
            sheetCanvas.backgroundColor = null;
        }
        sheetCanvas.renderAll();

        var pngDataUrl = sheetCanvas.toDataURL({ format: "png", multiplier: 1 });

        // Restore canvas
        sheetCanvas.backgroundColor = origBg;
        sheetCanvas.viewportTransform = origVpt;
        sheetCanvas.renderAll();

        // Create PDF at exact sheet dimensions (in inches)
        var orientation = sheetWidthIn > sheetHeightIn ? "landscape" : "portrait";
        var pdfW = Math.min(sheetWidthIn, sheetHeightIn);
        var pdfH = Math.max(sheetWidthIn, sheetHeightIn);

        var doc = new window.jspdf.jsPDF({
            orientation: orientation,
            unit: "in",
            format: [pdfW, pdfH],
        });

        // Add the PNG image filling the entire page
        doc.addImage(pngDataUrl, "PNG", 0, 0, sheetWidthIn, sheetHeightIn);
        doc.save(filename || "gang_sheet.pdf");
    }

    function downloadDesignPng(dataUrl, filename) {
        if (!dataUrl) return;
        triggerDownload(dataUrl, filename || "design.png");
    }

    function triggerDownload(dataUrl, filename) {
        var a = document.createElement("a");
        a.href = dataUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    function fitCanvasToContainer(canvas, el) {
        if (!canvas || !el) return;
        var container = el.closest(".gs-sheet-container");
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

        // Scale to fit fully within the container (both width and height)
        var scale = Math.min(availW / cw, availH / ch);
        canvas.setDimensions({ width: Math.round(cw * scale), height: Math.round(ch * scale) }, { cssOnly: true });
    }

    function dispose() {
        disposeCropTool();
        if (sheetCanvas) { try { sheetCanvas.dispose(); } catch (e) { } sheetCanvas = null; }
        if (sheetResizeObserver) { sheetResizeObserver.disconnect(); sheetResizeObserver = null; }
        lastRenderParams = null;
    }

    // ── Public API ──
    return {
        initCropTool: initCropTool,
        setAspectRatio: setAspectRatio,
        swapCropImage: swapCropImage,
        extractCrop: extractCrop,
        disposeCropTool: disposeCropTool,
        setTransparentMode: setTransparentMode,
        renderSheet: renderSheet,
        renderSheetMulti: renderSheetMulti,
        downloadSheetPng: downloadSheetPng,
        downloadSheetPdf: downloadSheetPdf,
        downloadDesignPng: downloadDesignPng,
        dispose: dispose,
    };
})();
