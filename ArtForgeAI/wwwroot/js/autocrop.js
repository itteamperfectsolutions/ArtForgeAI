// ===== ArtForge AI - Free-form Crop Tool for Auto Enhance =====
window.autoCrop = (function () {
    "use strict";

    var cropState = { x: 0, y: 0, w: 100, h: 100 };
    var container = null, wrapper = null, cropImg = null, cropBoxEl = null;
    var dimEls = []; // top, right, bottom, left overlays
    var displayW = 0, displayH = 0, naturalW = 0, naturalH = 0;
    var dragState = null;
    var MIN_SIZE = 30;

    function init(containerId, dataUrl) {
        dispose();
        container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = "";

        wrapper = document.createElement("div");
        wrapper.style.cssText = "position:relative;display:inline-block;max-width:100%;user-select:none;touch-action:none;";
        container.appendChild(wrapper);

        var img = document.createElement("img");
        img.style.cssText = "display:block;max-width:100%;max-height:320px;border-radius:6px;pointer-events:none;";
        img.onload = function () {
            naturalW = img.naturalWidth;
            naturalH = img.naturalHeight;
            displayW = img.clientWidth;
            displayH = img.clientHeight;
            buildCropUI();
        };
        img.src = dataUrl;
        wrapper.appendChild(img);
        cropImg = img;
    }

    function buildCropUI() {
        // dim overlays (darkened areas outside crop)
        dimEls = [];
        for (var i = 0; i < 4; i++) {
            var d = document.createElement("div");
            d.style.cssText = "position:absolute;background:rgba(0,0,0,0.55);pointer-events:none;";
            wrapper.appendChild(d);
            dimEls.push(d);
        }

        // crop box
        cropBoxEl = document.createElement("div");
        cropBoxEl.style.cssText = "position:absolute;border:2px dashed #00c8ff;cursor:move;box-sizing:border-box;overflow:visible;";
        wrapper.appendChild(cropBoxEl);

        // corner handles
        var corners = ["tl","tr","bl","br"];
        var cursors = ["nwse-resize","nesw-resize","nesw-resize","nwse-resize"];
        for (var c = 0; c < corners.length; c++) {
            var h = document.createElement("div");
            h.dataset.handle = corners[c];
            h.style.cssText = "position:absolute;width:14px;height:14px;background:#00c8ff;border-radius:2px;cursor:" + cursors[c] + ";z-index:10;";
            if (corners[c][0] === "t") h.style.top = "-7px"; else h.style.bottom = "-7px";
            if (corners[c][1] === "l") h.style.left = "-7px"; else h.style.right = "-7px";
            h.addEventListener("mousedown", onHandleDown);
            h.addEventListener("touchstart", onHandleTouchStart, { passive: false });
            cropBoxEl.appendChild(h);
        }

        // edge handles
        var edges = [
            { name: "t", css: "top:-5px;left:50%;transform:translateX(-50%);width:30px;height:10px;cursor:ns-resize;" },
            { name: "b", css: "bottom:-5px;left:50%;transform:translateX(-50%);width:30px;height:10px;cursor:ns-resize;" },
            { name: "l", css: "left:-5px;top:50%;transform:translateY(-50%);width:10px;height:30px;cursor:ew-resize;" },
            { name: "r", css: "right:-5px;top:50%;transform:translateY(-50%);width:10px;height:30px;cursor:ew-resize;" }
        ];
        for (var e = 0; e < edges.length; e++) {
            var h = document.createElement("div");
            h.dataset.handle = edges[e].name;
            h.style.cssText = "position:absolute;background:#00c8ff;border-radius:2px;z-index:10;" + edges[e].css;
            h.addEventListener("mousedown", onHandleDown);
            h.addEventListener("touchstart", onHandleTouchStart, { passive: false });
            cropBoxEl.appendChild(h);
        }

        // crop box drag (move)
        cropBoxEl.addEventListener("mousedown", onBoxDown);
        cropBoxEl.addEventListener("touchstart", onBoxTouchStart, { passive: false });

        // global move/up
        document.addEventListener("mousemove", onMouseMove);
        document.addEventListener("mouseup", onMouseUp);
        document.addEventListener("touchmove", onTouchMove, { passive: false });
        document.addEventListener("touchend", onTouchEnd);

        // default crop: full image with 4% inset
        var inset = Math.min(displayW, displayH) * 0.04;
        cropState.x = inset;
        cropState.y = inset;
        cropState.w = displayW - inset * 2;
        cropState.h = displayH - inset * 2;
        updateCropUI();
    }

    function updateCropUI() {
        if (!cropBoxEl) return;
        cropBoxEl.style.left = cropState.x + "px";
        cropBoxEl.style.top = cropState.y + "px";
        cropBoxEl.style.width = cropState.w + "px";
        cropBoxEl.style.height = cropState.h + "px";

        // dim overlays: top, right, bottom, left
        if (dimEls.length === 4) {
            // top
            dimEls[0].style.left = "0"; dimEls[0].style.top = "0";
            dimEls[0].style.width = displayW + "px"; dimEls[0].style.height = cropState.y + "px";
            // bottom
            dimEls[2].style.left = "0"; dimEls[2].style.top = (cropState.y + cropState.h) + "px";
            dimEls[2].style.width = displayW + "px"; dimEls[2].style.height = (displayH - cropState.y - cropState.h) + "px";
            // left
            dimEls[3].style.left = "0"; dimEls[3].style.top = cropState.y + "px";
            dimEls[3].style.width = cropState.x + "px"; dimEls[3].style.height = cropState.h + "px";
            // right
            dimEls[1].style.left = (cropState.x + cropState.w) + "px"; dimEls[1].style.top = cropState.y + "px";
            dimEls[1].style.width = (displayW - cropState.x - cropState.w) + "px"; dimEls[1].style.height = cropState.h + "px";
        }
    }

    // ── Drag: move crop box ──
    function onBoxDown(e) {
        if (e.target !== cropBoxEl) return;
        e.preventDefault();
        startDrag("move", e.clientX, e.clientY);
    }
    function onBoxTouchStart(e) {
        if (e.target !== cropBoxEl) return;
        e.preventDefault();
        var t = e.touches[0];
        startDrag("move", t.clientX, t.clientY);
    }

    // ── Drag: resize handles ──
    function onHandleDown(e) {
        e.preventDefault(); e.stopPropagation();
        startDrag(e.currentTarget.dataset.handle, e.clientX, e.clientY);
    }
    function onHandleTouchStart(e) {
        e.preventDefault(); e.stopPropagation();
        var t = e.touches[0];
        startDrag(e.currentTarget.dataset.handle, t.clientX, t.clientY);
    }

    function startDrag(handle, cx, cy) {
        dragState = {
            handle: handle,
            startX: cx, startY: cy,
            orig: { x: cropState.x, y: cropState.y, w: cropState.w, h: cropState.h }
        };
    }

    function onMouseMove(e) { if (dragState) { e.preventDefault(); doDrag(e.clientX, e.clientY); } }
    function onTouchMove(e) { if (dragState) { e.preventDefault(); var t = e.touches[0]; doDrag(t.clientX, t.clientY); } }
    function onMouseUp() { dragState = null; }
    function onTouchEnd() { dragState = null; }

    function doDrag(cx, cy) {
        if (!dragState) return;
        var dx = cx - dragState.startX;
        var dy = cy - dragState.startY;
        var o = dragState.orig;
        var h = dragState.handle;

        if (h === "move") {
            cropState.x = Math.max(0, Math.min(displayW - o.w, o.x + dx));
            cropState.y = Math.max(0, Math.min(displayH - o.h, o.y + dy));
        } else {
            var nx = o.x, ny = o.y, nw = o.w, nh = o.h;

            // horizontal
            if (h === "l" || h === "tl" || h === "bl") {
                var newX = Math.max(0, o.x + dx);
                nw = o.w + (o.x - newX);
                if (nw < MIN_SIZE) { nw = MIN_SIZE; newX = o.x + o.w - MIN_SIZE; }
                nx = newX;
            }
            if (h === "r" || h === "tr" || h === "br") {
                nw = Math.max(MIN_SIZE, Math.min(displayW - o.x, o.w + dx));
            }
            // vertical
            if (h === "t" || h === "tl" || h === "tr") {
                var newY = Math.max(0, o.y + dy);
                nh = o.h + (o.y - newY);
                if (nh < MIN_SIZE) { nh = MIN_SIZE; newY = o.y + o.h - MIN_SIZE; }
                ny = newY;
            }
            if (h === "b" || h === "bl" || h === "br") {
                nh = Math.max(MIN_SIZE, Math.min(displayH - o.y, o.h + dy));
            }

            cropState.x = nx; cropState.y = ny;
            cropState.w = nw; cropState.h = nh;
        }
        updateCropUI();
    }

    // ── Apply crop: extract cropped region at natural resolution ──
    function applyCrop() {
        if (!cropImg) return null;
        var scaleX = naturalW / displayW;
        var scaleY = naturalH / displayH;
        var sx = Math.round(cropState.x * scaleX);
        var sy = Math.round(cropState.y * scaleY);
        var sw = Math.round(cropState.w * scaleX);
        var sh = Math.round(cropState.h * scaleY);
        // clamp
        if (sx + sw > naturalW) sw = naturalW - sx;
        if (sy + sh > naturalH) sh = naturalH - sy;

        var canvas = document.createElement("canvas");
        canvas.width = sw; canvas.height = sh;
        var ctx = canvas.getContext("2d");
        ctx.drawImage(cropImg, sx, sy, sw, sh, 0, 0, sw, sh);

        return { dataUrl: canvas.toDataURL("image/png"), width: sw, height: sh };
    }

    // ── Reset crop to full image ──
    function resetCrop() {
        var inset = Math.min(displayW, displayH) * 0.04;
        cropState.x = inset;
        cropState.y = inset;
        cropState.w = displayW - inset * 2;
        cropState.h = displayH - inset * 2;
        updateCropUI();
    }

    // ── Dispose ──
    function dispose() {
        document.removeEventListener("mousemove", onMouseMove);
        document.removeEventListener("mouseup", onMouseUp);
        document.removeEventListener("touchmove", onTouchMove);
        document.removeEventListener("touchend", onTouchEnd);
        if (container) container.innerHTML = "";
        container = null; wrapper = null; cropImg = null; cropBoxEl = null;
        dimEls = []; dragState = null;
    }

    return { init: init, applyCrop: applyCrop, resetCrop: resetCrop, dispose: dispose };
})();

// ===== Before / After Comparison Slider =====
window.compareSlider = (function () {
    "use strict";

    var wrap = null, clipDiv = null, handle = null, line = null;
    var dragging = false;

    function init(containerId, beforeUrl, afterUrl) {
        dispose();
        var el = document.getElementById(containerId);
        if (!el) return;
        el.innerHTML = "";

        // Calculate max height: fit within viewport with some padding
        var maxH = Math.min(window.innerHeight - 200, 600);

        // wrapper — sized by the after image, capped to viewport
        wrap = document.createElement("div");
        wrap.style.cssText = "position:relative;overflow:hidden;border-radius:8px;user-select:none;touch-action:none;cursor:ew-resize;background:var(--bg-tertiary,#1a1a2e);max-height:" + maxH + "px;";
        el.appendChild(wrap);

        // after image (bottom layer — sets the wrapper size)
        var afterImg = document.createElement("img");
        afterImg.src = afterUrl;
        afterImg.alt = "After";
        afterImg.draggable = false;
        afterImg.style.cssText = "display:block;width:100%;max-height:" + maxH + "px;object-fit:contain;pointer-events:none;";
        wrap.appendChild(afterImg);

        // clip container for before image (top layer, clipped by width)
        clipDiv = document.createElement("div");
        clipDiv.style.cssText = "position:absolute;top:0;left:0;bottom:0;width:50%;overflow:hidden;";
        wrap.appendChild(clipDiv);

        // before image — positioned absolutely, same size as wrapper so it aligns pixel-perfectly
        var beforeImg = document.createElement("img");
        beforeImg.src = beforeUrl;
        beforeImg.alt = "Before";
        beforeImg.draggable = false;
        beforeImg.style.cssText = "position:absolute;top:0;left:0;display:block;pointer-events:none;";
        clipDiv.appendChild(beforeImg);

        // once after image loads, match before image dimensions exactly
        afterImg.onload = function () {
            var w = afterImg.offsetWidth;
            var h = afterImg.offsetHeight;
            // set wrapper explicit height so clip div bottom:0 works
            wrap.style.height = h + "px";
            beforeImg.style.width = w + "px";
            beforeImg.style.height = h + "px";
            beforeImg.style.objectFit = "contain";
        };

        // vertical divider line
        line = document.createElement("div");
        line.style.cssText = "position:absolute;top:0;bottom:0;width:3px;background:#00c8ff;left:50%;transform:translateX(-50%);pointer-events:none;z-index:5;";
        wrap.appendChild(line);

        // drag handle circle
        handle = document.createElement("div");
        handle.style.cssText = "position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);width:40px;height:40px;border-radius:50%;background:#00c8ff;z-index:10;display:flex;align-items:center;justify-content:center;box-shadow:0 2px 8px rgba(0,0,0,0.4);";
        handle.innerHTML = '<svg width="20" height="20" viewBox="0 0 20 20" fill="none"><path d="M7 4L3 10L7 16" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><path d="M13 4L17 10L13 16" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
        wrap.appendChild(handle);

        // labels
        var lblBefore = document.createElement("span");
        lblBefore.textContent = "BEFORE";
        lblBefore.style.cssText = "position:absolute;top:8px;left:10px;font-size:0.7rem;font-weight:700;color:#fff;background:rgba(0,0,0,0.5);padding:2px 8px;border-radius:4px;z-index:6;letter-spacing:0.5px;";
        wrap.appendChild(lblBefore);

        var lblAfter = document.createElement("span");
        lblAfter.textContent = "AFTER";
        lblAfter.style.cssText = "position:absolute;top:8px;right:10px;font-size:0.7rem;font-weight:700;color:#fff;background:rgba(0,0,0,0.5);padding:2px 8px;border-radius:4px;z-index:6;letter-spacing:0.5px;";
        wrap.appendChild(lblAfter);

        // store ref
        wrap._beforeImg = beforeImg;
        wrap._afterImg = afterImg;

        // events
        wrap.addEventListener("mousedown", onDown);
        wrap.addEventListener("touchstart", onTouchDown, { passive: false });
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
        document.addEventListener("touchmove", onTouchMove, { passive: false });
        document.addEventListener("touchend", onUp);

        // resize observer to keep before image sized correctly
        if (window.ResizeObserver) {
            wrap._resizeObs = new ResizeObserver(function () {
                if (wrap && wrap._afterImg) {
                    var w = wrap._afterImg.offsetWidth;
                    var h = wrap._afterImg.offsetHeight;
                    wrap.style.height = h + "px";
                    wrap._beforeImg.style.width = w + "px";
                    wrap._beforeImg.style.height = h + "px";
                }
            });
            wrap._resizeObs.observe(wrap);
        }
    }

    function setPosition(clientX) {
        if (!wrap) return;
        var rect = wrap.getBoundingClientRect();
        var x = clientX - rect.left;
        var pct = Math.max(0, Math.min(1, x / rect.width)) * 100;
        clipDiv.style.width = pct + "%";
        line.style.left = pct + "%";
        handle.style.left = pct + "%";
    }

    function onDown(e) { e.preventDefault(); dragging = true; setPosition(e.clientX); }
    function onTouchDown(e) { e.preventDefault(); dragging = true; setPosition(e.touches[0].clientX); }
    function onMove(e) { if (dragging) { e.preventDefault(); setPosition(e.clientX); } }
    function onTouchMove(e) { if (dragging) { e.preventDefault(); setPosition(e.touches[0].clientX); } }
    function onUp() { dragging = false; }

    function dispose() {
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        document.removeEventListener("touchmove", onTouchMove);
        document.removeEventListener("touchend", onUp);
        if (wrap) {
            if (wrap._resizeObs) wrap._resizeObs.disconnect();
            wrap.removeEventListener("mousedown", onDown);
            wrap.removeEventListener("touchstart", onTouchDown);
            wrap.parentElement && wrap.parentElement.removeChild(wrap);
        }
        wrap = null; clipDiv = null; handle = null; line = null; dragging = false;
    }

    return { init: init, dispose: dispose };
})();
