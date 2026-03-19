// ===== ArtForge AI - Passport Photo Module =====
window.passportPhoto = (function () {
    "use strict";

    let previewCanvas = null;
    let sheetCanvas = null;
    let photoDataUrl = null;
    let resizeObserver = null;
    let sheetResizeObserver = null;

    // Paper sizes in px at 300 DPI
    const paperSizes = {
        '4\u00d76"':   { w: 1205, h: 1795 },
        'A4':     { w: 2480, h: 3508 },
        'A3':     { w: 3508, h: 4961 },
        '12\u00d718"': { w: 3602, h: 5398 },
        '13\u00d719"': { w: 3898, h: 5704 },
    };
    let customPaperSize = null;

    const photoW = 413;
    const photoH = 531;
    const pxPerMm = 300 / 25.4;
    const PASSPORT_ASPECT = 413 / 531;

    // Current state
    let currentPaper = '4\u00d76"';
    let spacingMm = 2;
    let marginMm = 3;
    let cutMarksOn = true;
    let cropMarksOn = false;
    let landscapeOn = false;

    // Face grid spec (ICAO fractions of photo)
    const faceGrid = {
        headTopMin: 3 / 45,
        headTopMax: 5 / 45,
        chinMin: 28 / 45,
        chinMax: 33 / 45,
        eyeMin: 18 / 45,
        eyeMax: 22 / 45,
        faceWMin: 24 / 35,
        faceWMax: 30 / 35,
    };

    // ══════════════════════════════════════════
    // ── DOM-based Interactive Crop Tool ──
    // ══════════════════════════════════════════
    var cropState = { x: 0, y: 0, w: 100, h: 128 };
    var cropContainer = null;
    var cropWrapper = null;
    var cropImg = null;
    var cropBoxEl = null;
    var dimEls = {};
    var cropDisplayW = 0, cropDisplayH = 0;
    var cropNaturalW = 0, cropNaturalH = 0;
    var dragState = null;
    var cropChangedCallback = null;

    function initCropTool(containerId, dataUrl) {
        disposeCropTool();

        var container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = "";

        // Wrapper holds the image + overlays
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

            // Dim overlays (4 semi-transparent strips)
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

            // Edge resize handles (mid-sides)
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

            // ICAO face guide lines inside crop box
            addGuideLines(cropBoxEl);

            // Default crop: maximize height, center horizontally
            var defH = cropDisplayH * 0.92;
            var defW = defH * PASSPORT_ASPECT;
            if (defW > cropDisplayW * 0.95) {
                defW = cropDisplayW * 0.95;
                defH = defW / PASSPORT_ASPECT;
            }
            cropState.x = (cropDisplayW - defW) / 2;
            cropState.y = (cropDisplayH - defH) / 2;
            cropState.w = defW;
            cropState.h = defH;

            updateCropUI();

            // Mouse events
            wrapper.addEventListener("mousedown", onCropMouseDown);
            document.addEventListener("mousemove", onCropMouseMove);
            document.addEventListener("mouseup", onCropMouseUp);
            // Touch events
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

    function addGuideLines(box) {
        var guides = [
            { frac: faceGrid.eyeMin, color: "rgba(0,200,255,0.5)", label: "Eyes" },
            { frac: faceGrid.eyeMax, color: "rgba(0,200,255,0.5)", label: null },
            { frac: faceGrid.headTopMin, color: "rgba(255,200,0,0.4)", label: null },
            { frac: faceGrid.headTopMax, color: "rgba(255,200,0,0.4)", label: "Head" },
            { frac: faceGrid.chinMin, color: "rgba(0,200,255,0.5)", label: "Chin" },
            { frac: faceGrid.chinMax, color: "rgba(0,200,255,0.5)", label: null },
        ];
        guides.forEach(function (g) {
            var line = document.createElement("div");
            line.style.cssText = "position:absolute;left:0;right:0;height:0;border-top:1px dashed " + g.color + ";pointer-events:none;top:" + (g.frac * 100) + "%;";
            box.appendChild(line);
            if (g.label) {
                var lbl = document.createElement("span");
                lbl.textContent = g.label;
                lbl.style.cssText = "position:absolute;left:4px;top:-12px;font-size:9px;font-family:monospace;color:" + g.color + ";pointer-events:none;";
                line.appendChild(lbl);
            }
        });

        // Vertical center line (nose alignment)
        var vLine = document.createElement("div");
        vLine.style.cssText = "position:absolute;top:0;bottom:0;width:0;border-left:1px dashed rgba(255,100,100,0.5);pointer-events:none;left:50%;";
        box.appendChild(vLine);
        var vLbl = document.createElement("span");
        vLbl.textContent = "Nose";
        vLbl.style.cssText = "position:absolute;left:4px;bottom:4px;font-size:9px;font-family:monospace;color:rgba(255,100,100,0.6);pointer-events:none;";
        vLine.appendChild(vLbl);
    }

    // ── Mouse handling ──

    function onCropMouseDown(e) {
        e.preventDefault();
        var rect = cropWrapper.getBoundingClientRect();
        var mx = e.clientX - rect.left;
        var my = e.clientY - rect.top;

        // Check if on a resize handle
        var handle = e.target.dataset ? e.target.dataset.handle : null;
        if (handle) {
            dragState = { type: "resize", handle: handle, startX: e.clientX, startY: e.clientY, orig: { x: cropState.x, y: cropState.y, w: cropState.w, h: cropState.h } };
            return;
        }

        // Check if inside crop box → move
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
        if (dragState && cropChangedCallback) {
            dragState = null;
            cropChangedCallback();
        } else {
            dragState = null;
        }
    }

    // ── Touch handling (delegates to same logic) ──

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
        if (dragState && cropChangedCallback) {
            dragState = null;
            cropChangedCallback();
        } else {
            dragState = null;
        }
    }

    // ── Resize with aspect ratio lock ──

    function resizeCrop(handle, dx, dy, orig) {
        var nw, nh, nx, ny;

        // Corner handles: derive both dimensions from whichever axis moved more
        if (handle === "br") {
            nw = Math.max(40, orig.w + dx);
            nh = nw / PASSPORT_ASPECT;
            nx = orig.x; ny = orig.y;
        } else if (handle === "bl") {
            nw = Math.max(40, orig.w - dx);
            nh = nw / PASSPORT_ASPECT;
            nx = orig.x + orig.w - nw; ny = orig.y;
        } else if (handle === "tr") {
            nw = Math.max(40, orig.w + dx);
            nh = nw / PASSPORT_ASPECT;
            nx = orig.x; ny = orig.y + orig.h - nh;
        } else if (handle === "tl") {
            nw = Math.max(40, orig.w - dx);
            nh = nw / PASSPORT_ASPECT;
            nx = orig.x + orig.w - nw; ny = orig.y + orig.h - nh;
        }
        // Edge handles: resize from that edge, keep aspect
        else if (handle === "r") {
            nw = Math.max(40, orig.w + dx);
            nh = nw / PASSPORT_ASPECT;
            nx = orig.x; ny = orig.y + (orig.h - nh) / 2;
        } else if (handle === "l") {
            nw = Math.max(40, orig.w - dx);
            nh = nw / PASSPORT_ASPECT;
            nx = orig.x + orig.w - nw; ny = orig.y + (orig.h - nh) / 2;
        } else if (handle === "b") {
            nh = Math.max(40, orig.h + dy);
            nw = nh * PASSPORT_ASPECT;
            nx = orig.x + (orig.w - nw) / 2; ny = orig.y;
        } else if (handle === "t") {
            nh = Math.max(40, orig.h - dy);
            nw = nh * PASSPORT_ASPECT;
            nx = orig.x + (orig.w - nw) / 2; ny = orig.y + orig.h - nh;
        } else {
            return;
        }

        // Clamp within image bounds
        if (nx < 0) { nx = 0; }
        if (ny < 0) { ny = 0; }
        if (nx + nw > cropDisplayW) { nw = cropDisplayW - nx; nh = nw / PASSPORT_ASPECT; }
        if (ny + nh > cropDisplayH) { nh = cropDisplayH - ny; nw = nh * PASSPORT_ASPECT; }
        if (nw < 40) { nw = 40; nh = nw / PASSPORT_ASPECT; }

        cropState.x = nx;
        cropState.y = ny;
        cropState.w = nw;
        cropState.h = nh;
    }

    // ── Update crop box and dim overlay positions ──

    function updateCropUI() {
        if (!cropBoxEl) return;
        var s = cropState;

        cropBoxEl.style.left = s.x + "px";
        cropBoxEl.style.top = s.y + "px";
        cropBoxEl.style.width = s.w + "px";
        cropBoxEl.style.height = s.h + "px";

        // Dim overlays
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

    function setCropBoxFromFaceDetection(faceTop, faceBottom, faceLeft, faceRight, eyeLineY) {
        if (!cropImg || !cropDisplayW || !cropDisplayH) return;

        // Face dimensions in display pixels
        var fTopPx = faceTop * cropDisplayH;
        var fBottomPx = faceBottom * cropDisplayH;
        var fLeftPx = faceLeft * cropDisplayW;
        var fRightPx = faceRight * cropDisplayW;
        var eyePx = eyeLineY * cropDisplayH;
        var faceH = fBottomPx - fTopPx;
        var faceCX = (fLeftPx + fRightPx) / 2;

        if (faceH < 5) return; // face too small, keep default crop

        // Face should fill ~80% of passport photo height (user requirement, ICAO says 70-80%)
        var cropH = faceH / 0.80;
        var cropW = cropH * PASSPORT_ASPECT;

        // Ensure crop fits within the image
        if (cropW > cropDisplayW) { cropW = cropDisplayW; cropH = cropW / PASSPORT_ASPECT; }
        if (cropH > cropDisplayH) { cropH = cropDisplayH; cropW = cropH * PASSPORT_ASPECT; }

        // Position: align eyes with ICAO eye zone center (~44% from top)
        var eyeTargetFrac = (faceGrid.eyeMin + faceGrid.eyeMax) / 2;
        var cropY = eyePx - cropH * eyeTargetFrac;

        // Center horizontally on the face
        var cropX = faceCX - cropW / 2;

        // Clamp to image bounds
        if (cropX < 0) cropX = 0;
        if (cropY < 0) cropY = 0;
        if (cropX + cropW > cropDisplayW) cropX = cropDisplayW - cropW;
        if (cropY + cropH > cropDisplayH) cropY = cropDisplayH - cropH;

        cropState.x = cropX;
        cropState.y = cropY;
        cropState.w = cropW;
        cropState.h = cropH;
        updateCropUI();
    }

    function setCropChangedCallback(enabled) {
        if (enabled) {
            cropChangedCallback = function () {
                // Do crop + sheet render entirely client-side for instant feedback
                cropAndRenderSheet("ppSheetCanvas");
            };
        } else {
            cropChangedCallback = null;
        }
    }

    // Swap the crop tool image (e.g., after background removal) while preserving crop box position
    function swapCropImage(dataUrl) {
        if (!cropImg || !cropWrapper) return Promise.resolve();
        return new Promise(function (resolve) {
            // Store current crop state (relative fractions)
            var relX = cropState.x / cropDisplayW;
            var relY = cropState.y / cropDisplayH;
            var relW = cropState.w / cropDisplayW;
            var relH = cropState.h / cropDisplayH;

            cropImg.onload = function () {
                cropNaturalW = cropImg.naturalWidth;
                cropNaturalH = cropImg.naturalHeight;
                cropDisplayW = cropImg.clientWidth;
                cropDisplayH = cropImg.clientHeight;

                // Restore crop box at same relative position
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
        cropChangedCallback = null;
    }

    function clamp(v, min, max) { return Math.max(min, Math.min(v, max)); }

    // ══════════════════════════════════════════
    // ── Preview Canvas (face grid overlay) ──
    // ══════════════════════════════════════════

    function initPreview(canvasId) {
        if (previewCanvas) {
            try { previewCanvas.dispose(); } catch (e) { }
            previewCanvas = null;
        }
        var el = document.getElementById(canvasId);
        if (!el) return;
        previewCanvas = new fabric.Canvas(canvasId, {
            width: photoW,
            height: photoH,
            backgroundColor: "#f0f0f0",
            selection: false,
            renderOnAddRemove: false,
        });
        fitCanvasToContainer(previewCanvas, el);
        if (resizeObserver) {
            resizeObserver.disconnect();
        }
        var observeTarget = el.closest(".pp-preview-container, .pp-sheet-container") || el.parentElement;
        resizeObserver = new ResizeObserver(function () {
            if (previewCanvas) {
                var canvasEl = document.getElementById(canvasId);
                if (canvasEl) fitCanvasToContainer(previewCanvas, canvasEl);
            }
        });
        if (observeTarget) resizeObserver.observe(observeTarget);
    }

    function renderInputPreview(canvasId, dataUrl) {
        if (!previewCanvas) initPreview(canvasId);
        if (!previewCanvas) return;
        previewCanvas.clear();
        previewCanvas.backgroundColor = "#f0f0f0";

        var imgEl = new Image();
        imgEl.crossOrigin = "anonymous";
        imgEl.onload = function () {
            var fabricImg = new fabric.Image(imgEl, {
                selectable: false,
                evented: false,
            });
            var scale = Math.min(photoW / fabricImg.width, photoH / fabricImg.height);
            fabricImg.set({
                scaleX: scale,
                scaleY: scale,
                left: (photoW - fabricImg.width * scale) / 2,
                top: (photoH - fabricImg.height * scale) / 2,
            });
            previewCanvas.add(fabricImg);
            drawFaceGuides(previewCanvas);
            previewCanvas.renderAll();
        };
        imgEl.src = dataUrl;
    }

    function drawFaceGuides(canvas) {
        var w = photoW, h = photoH;
        var guideColor = "rgba(0, 200, 255, 0.6)";
        var warnColor = "rgba(255, 200, 0, 0.5)";

        var eyeRect = new fabric.Rect({
            left: 0, top: h * faceGrid.eyeMin,
            width: w, height: h * (faceGrid.eyeMax - faceGrid.eyeMin),
            fill: "rgba(0, 200, 255, 0.08)",
            selectable: false, evented: false,
        });
        canvas.add(eyeRect);

        addDashedLine(canvas, 0, h * faceGrid.eyeMin, w, h * faceGrid.eyeMin, guideColor);
        addDashedLine(canvas, 0, h * faceGrid.eyeMax, w, h * faceGrid.eyeMax, guideColor);
        addDashedLine(canvas, 0, h * faceGrid.headTopMin, w, h * faceGrid.headTopMin, warnColor);
        addDashedLine(canvas, 0, h * faceGrid.headTopMax, w, h * faceGrid.headTopMax, warnColor);
        addDashedLine(canvas, 0, h * faceGrid.chinMin, w, h * faceGrid.chinMin, guideColor);
        addDashedLine(canvas, 0, h * faceGrid.chinMax, w, h * faceGrid.chinMax, guideColor);

        var fwLeft = (w - w * faceGrid.faceWMax) / 2;
        var fwRight = w - fwLeft;
        addDashedLine(canvas, fwLeft, 0, fwLeft, h, warnColor);
        addDashedLine(canvas, fwRight, 0, fwRight, h, warnColor);

        var ovalRx = (w * (faceGrid.faceWMin + faceGrid.faceWMax) / 2) / 2;
        var ovalRy = (h * ((faceGrid.chinMin + faceGrid.chinMax) / 2 - (faceGrid.headTopMin + faceGrid.headTopMax) / 2)) / 2;
        var ovalCy = h * ((faceGrid.headTopMin + faceGrid.headTopMax + faceGrid.chinMin + faceGrid.chinMax) / 4);
        var oval = new fabric.Ellipse({
            rx: ovalRx, ry: ovalRy,
            left: w / 2 - ovalRx, top: ovalCy - ovalRy,
            fill: "transparent",
            stroke: "rgba(0, 200, 255, 0.35)",
            strokeWidth: 1.5,
            strokeDashArray: [6, 4],
            selectable: false, evented: false,
        });
        canvas.add(oval);

        // Vertical center line (nose alignment)
        var noseColor = "rgba(255,100,100,0.5)";
        addDashedLine(canvas, w / 2, 0, w / 2, h, noseColor);
        addLabel(canvas, "Nose", w / 2 + 4, h * faceGrid.chinMax + 4, noseColor);

        addLabel(canvas, "Eyes", 4, h * faceGrid.eyeMin - 12, guideColor);
        addLabel(canvas, "Chin", 4, h * faceGrid.chinMin - 12, guideColor);
        addLabel(canvas, "Head Top", 4, h * faceGrid.headTopMax + 2, warnColor);
    }

    function addDashedLine(canvas, x1, y1, x2, y2, color) {
        canvas.add(new fabric.Line([x1, y1, x2, y2], {
            stroke: color, strokeWidth: 1, strokeDashArray: [5, 3],
            selectable: false, evented: false,
        }));
    }

    function addLabel(canvas, text, left, top, color) {
        canvas.add(new fabric.Text(text, {
            left: left, top: top, fontSize: 10, fill: color,
            fontFamily: "monospace", selectable: false, evented: false,
        }));
    }

    // ══════════════════════════════════════════
    // ── Sheet Canvas (multi-up preview) ──
    // ══════════════════════════════════════════

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

        // Create at 1×1 to avoid layout push — renderSheet sets proper dimensions
        sheetCanvas = new fabric.Canvas(canvasId, {
            width: 1, height: 1,
            backgroundColor: "#ffffff",
            selection: false, renderOnAddRemove: false,
        });

        // Re-fit on container resize
        var observeTarget = el.closest(".pp-sheet-container") || el.parentElement;
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

    function setPhoto(dataUrl) { photoDataUrl = dataUrl; }
    function setPaperSize(name) { currentPaper = name; }
    function setCustomPaperSize(wPx, hPx) { customPaperSize = { w: wPx, h: hPx }; currentPaper = 'Custom'; }
    function setSpacing(mm) { spacingMm = mm; }
    function setMargin(mm) { marginMm = mm; }
    function setCutMarks(on) { cutMarksOn = on; }
    function setCropMarks(on) { cropMarksOn = on; }
    function setLandscape(on) { landscapeOn = on; }

    // Crop from source image + render sheet entirely client-side (no server call)
    function cropAndRenderSheet(canvasId) {
        if (!cropImg || !cropImg.naturalWidth) return Promise.resolve(0);

        // Get crop rect in natural image pixels
        var rect = getCropRect();
        var sx = Math.round(rect.x * cropNaturalW);
        var sy = Math.round(rect.y * cropNaturalH);
        var sw = Math.round(rect.w * cropNaturalW);
        var sh = Math.round(rect.h * cropNaturalH);
        if (sw < 1 || sh < 1) return Promise.resolve(0);

        // Crop to passport size using an offscreen canvas
        var offscreen = document.createElement("canvas");
        offscreen.width = photoW;
        offscreen.height = photoH;
        var octx = offscreen.getContext("2d");
        octx.drawImage(cropImg, sx, sy, sw, sh, 0, 0, photoW, photoH);

        var croppedDataUrl = offscreen.toDataURL("image/png");
        photoDataUrl = croppedDataUrl;

        return renderSheet(canvasId);
    }

    function renderSheet(canvasId) {
        if (!sheetCanvas) initSheet(canvasId);
        if (!sheetCanvas) return Promise.resolve(0);

        var paper = (currentPaper === 'Custom' && customPaperSize) ? customPaperSize : (paperSizes[currentPaper] || paperSizes['4\u00d76"']);
        var pw = landscapeOn ? paper.h : paper.w;
        var ph = landscapeOn ? paper.w : paper.h;

        // Set internal buffer to full paper dimensions (no CSS change — prevents layout push)
        sheetCanvas.setDimensions({ width: pw, height: ph }, { backstoreOnly: true });
        // Scale CSS to fit the container
        var sheetEl = document.getElementById(canvasId);
        if (sheetEl) fitCanvasToContainer(sheetCanvas, sheetEl);
        sheetCanvas.clear();
        sheetCanvas.backgroundColor = "#ffffff";

        if (!photoDataUrl) {
            sheetCanvas.renderAll();
            return Promise.resolve(0);
        }

        return new Promise(function (resolve) {
            var imgEl = new Image();
            imgEl.crossOrigin = "anonymous";
            imgEl.onload = function () {
                var spacingPx = Math.round(spacingMm * pxPerMm);
                var marginPx = Math.round(marginMm * pxPerMm);
                var usableW = pw - 2 * marginPx;
                var usableH = ph - 2 * marginPx;
                var cols = Math.max(1, Math.floor((usableW + spacingPx) / (photoW + spacingPx)));
                var rows = Math.max(1, Math.floor((usableH + spacingPx) / (photoH + spacingPx)));
                var gridW = cols * photoW + (cols - 1) * spacingPx;
                var gridH = rows * photoH + (rows - 1) * spacingPx;
                var offsetX = marginPx + Math.floor((usableW - gridW) / 2);
                var offsetY = marginPx + Math.floor((usableH - gridH) / 2);

                for (var r = 0; r < rows; r++) {
                    for (var c = 0; c < cols; c++) {
                        var x = offsetX + c * (photoW + spacingPx);
                        var y = offsetY + r * (photoH + spacingPx);
                        sheetCanvas.add(new fabric.Image(imgEl, {
                            left: x, top: y,
                            scaleX: photoW / imgEl.naturalWidth,
                            scaleY: photoH / imgEl.naturalHeight,
                            selectable: false, evented: false,
                        }));
                        if (cutMarksOn) {
                            sheetCanvas.add(new fabric.Rect({
                                left: x, top: y, width: photoW, height: photoH,
                                fill: "transparent", stroke: "#000000", strokeWidth: 1,
                                selectable: false, evented: false,
                            }));
                        }
                    }
                }
                if (cropMarksOn) {
                    var armLen = Math.round(Math.min(spacingPx * 0.8, 5 * pxPerMm));
                    armLen = Math.max(armLen, Math.round(2 * pxPerMm));
                    for (var cr = 0; cr < rows; cr++) {
                        for (var cc = 0; cc < cols; cc++) {
                            var px = offsetX + cc * (photoW + spacingPx);
                            var py = offsetY + cr * (photoH + spacingPx);
                            drawLMark(sheetCanvas, px, py, armLen, true, true);
                            drawLMark(sheetCanvas, px + photoW, py, armLen, false, true);
                            drawLMark(sheetCanvas, px, py + photoH, armLen, true, false);
                            drawLMark(sheetCanvas, px + photoW, py + photoH, armLen, false, false);
                        }
                    }
                }
                sheetCanvas.renderAll();
                resolve(cols * rows);
            };
            imgEl.onerror = function () { resolve(0); };
            imgEl.src = photoDataUrl;
        });
    }

    function drawLMark(canvas, x, y, len, leftSide, topSide) {
        var hDir = leftSide ? -1 : 1;
        var vDir = topSide ? -1 : 1;
        canvas.add(new fabric.Line([x, y, x + hDir * len, y], {
            stroke: "#000", strokeWidth: 2.5, selectable: false, evented: false,
        }));
        canvas.add(new fabric.Line([x, y, x, y + vDir * len], {
            stroke: "#000", strokeWidth: 2.5, selectable: false, evented: false,
        }));
    }

    function drawCrosshair(canvas, cx, cy, armLen) {
        canvas.add(new fabric.Line([cx - armLen, cy, cx + armLen, cy], {
            stroke: "#000", strokeWidth: 1.5, selectable: false, evented: false,
        }));
        canvas.add(new fabric.Line([cx, cy - armLen, cx, cy + armLen], {
            stroke: "#000", strokeWidth: 1.5, selectable: false, evented: false,
        }));
    }

    function exportPng() {
        if (!sheetCanvas) return null;
        var origVpt = sheetCanvas.viewportTransform.slice();
        sheetCanvas.viewportTransform = [1, 0, 0, 1, 0, 0];
        var dataUrl = sheetCanvas.toDataURL({ format: "png", multiplier: 1 });
        sheetCanvas.viewportTransform = origVpt;
        sheetCanvas.renderAll();
        return dataUrl;
    }

    // Export the cropped passport photo (413×531) as a data URL
    function exportPassportPhoto(format) {
        if (!cropImg || !cropImg.naturalWidth) return null;
        var rect = getCropRect();
        var sx = Math.round(rect.x * cropNaturalW);
        var sy = Math.round(rect.y * cropNaturalH);
        var sw = Math.round(rect.w * cropNaturalW);
        var sh = Math.round(rect.h * cropNaturalH);
        if (sw < 1 || sh < 1) return null;
        var offscreen = document.createElement("canvas");
        offscreen.width = photoW;
        offscreen.height = photoH;
        var octx = offscreen.getContext("2d");
        octx.drawImage(cropImg, sx, sy, sw, sh, 0, 0, photoW, photoH);
        var mime = (format === "jpg" || format === "jpeg") ? "image/jpeg" : "image/png";
        return offscreen.toDataURL(mime, 0.95);
    }

    // Export cropped passport photo as transparent PNG (preserving alpha channel)
    function exportPassportPhotoTransparent() {
        if (!cropImg || !cropImg.naturalWidth) return null;
        var rect = getCropRect();
        var sx = Math.round(rect.x * cropNaturalW);
        var sy = Math.round(rect.y * cropNaturalH);
        var sw = Math.round(rect.w * cropNaturalW);
        var sh = Math.round(rect.h * cropNaturalH);
        if (sw < 1 || sh < 1) return null;
        var offscreen = document.createElement("canvas");
        offscreen.width = photoW;
        offscreen.height = photoH;
        var octx = offscreen.getContext("2d");
        // Do NOT fill background — keep transparent
        octx.drawImage(cropImg, sx, sy, sw, sh, 0, 0, photoW, photoH);
        return offscreen.toDataURL("image/png");
    }

    // Trigger a browser download from a data URL
    function triggerDownload(dataUrl, filename) {
        var a = document.createElement("a");
        a.href = dataUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    // Download the print sheet as PNG
    function downloadSheetPng(filename) {
        var dataUrl = exportPng();
        if (dataUrl) triggerDownload(dataUrl, filename || "passport_print_sheet.png");
    }

    // Download the passport photo in given format
    function downloadPassportPhoto(format, filename) {
        var dataUrl = exportPassportPhoto(format);
        if (dataUrl) triggerDownload(dataUrl, filename || ("passport_photo_35x45mm." + (format || "png")));
    }

    function getGridInfo() {
        var paper = (currentPaper === 'Custom' && customPaperSize) ? customPaperSize : (paperSizes[currentPaper] || paperSizes['4\u00d76"']);
        var spacingPx = Math.round(spacingMm * pxPerMm);
        var marginPx = Math.round(marginMm * pxPerMm);
        var usableW = paper.w - 2 * marginPx;
        var usableH = paper.h - 2 * marginPx;
        var cols = Math.max(1, Math.floor((usableW + spacingPx) / (photoW + spacingPx)));
        var rows = Math.max(1, Math.floor((usableH + spacingPx) / (photoH + spacingPx)));
        return { cols: cols, rows: rows, total: cols * rows, paper: currentPaper };
    }

    function dispose() {
        disposeCropTool();
        if (previewCanvas) { try { previewCanvas.dispose(); } catch (e) { } previewCanvas = null; }
        if (sheetCanvas) { try { sheetCanvas.dispose(); } catch (e) { } sheetCanvas = null; }
        if (resizeObserver) { resizeObserver.disconnect(); resizeObserver = null; }
        if (sheetResizeObserver) { sheetResizeObserver.disconnect(); sheetResizeObserver = null; }
        photoDataUrl = null;
    }

    function fitCanvasToContainer(canvas, el) {
        if (!canvas || !el) return;
        // Navigate past Fabric.js canvas-container wrapper to the actual layout container
        var container = el.closest(".pp-sheet-container, .pp-preview-container");
        if (!container) container = el.parentElement;
        if (!container) return;
        var style = getComputedStyle(container);
        var containerW = container.clientWidth
            - (parseFloat(style.paddingLeft) || 0)
            - (parseFloat(style.paddingRight) || 0);
        var containerH = container.clientHeight
            - (parseFloat(style.paddingTop) || 0)
            - (parseFloat(style.paddingBottom) || 0);
        if (containerW <= 0) return;
        var cw = canvas.getWidth();
        var ch = canvas.getHeight();
        // Fit to the more constrained dimension
        var scaleW = containerW / cw;
        var scaleH = containerH > 0 ? containerH / ch : scaleW;
        var scale = Math.min(scaleW, scaleH);
        if (canvas === previewCanvas && scale > 1) scale = 1;
        canvas.setDimensions({ width: cw * scale, height: ch * scale }, { cssOnly: true });
    }

    // ── Public API ──
    return {
        initPreview: initPreview,
        renderInputPreview: renderInputPreview,
        initCropTool: initCropTool,
        getCropRect: getCropRect,
        setCropBoxFromFaceDetection: setCropBoxFromFaceDetection,
        setCropChangedCallback: setCropChangedCallback,
        swapCropImage: swapCropImage,
        disposeCropTool: disposeCropTool,
        initSheet: initSheet,
        setPhoto: setPhoto,
        setPaperSize: setPaperSize,
        setCustomPaperSize: setCustomPaperSize,
        setSpacing: setSpacing,
        setMargin: setMargin,
        setCutMarks: setCutMarks,
        setCropMarks: setCropMarks,
        setLandscape: setLandscape,
        cropAndRenderSheet: cropAndRenderSheet,
        renderSheet: renderSheet,
        exportPng: exportPng,
        exportPassportPhoto: exportPassportPhoto,
        exportPassportPhotoTransparent: exportPassportPhotoTransparent,
        downloadSheetPng: downloadSheetPng,
        downloadPassportPhoto: downloadPassportPhoto,
        getGridInfo: getGridInfo,
        dispose: dispose,
    };
})();
