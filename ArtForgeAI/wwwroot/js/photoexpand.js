/**
 * Photo Size Expand — JS interop module
 * Provides Fabric.js canvas preview with drag-to-reposition,
 * checkerboard empty areas, dimension labels, and download helpers.
 */
window.photoExpand = (function () {
    let canvas = null;
    let sourceImg = null;
    let sourceObj = null;
    let checkerPattern = null;
    let containerEl = null;
    let resizeObserver = null;
    let canvasId = '';

    // Current state (actual image pixels)
    let srcW = 0, srcH = 0;
    let tgtW = 0, tgtH = 0;
    let posX = 0.5, posY = 0.5;
    let wrapPx = 0; // canvas wrap bleed in image pixels

    function createCheckerboard() {
        var size = 10;
        var c = document.createElement('canvas');
        c.width = size * 2;
        c.height = size * 2;
        var ctx = c.getContext('2d');
        ctx.fillStyle = '#555';
        ctx.fillRect(0, 0, size, size);
        ctx.fillRect(size, size, size, size);
        ctx.fillStyle = '#666';
        ctx.fillRect(size, 0, size, size);
        ctx.fillRect(0, size, size, size);
        return c;
    }

    function findPreviewContainer() {
        // Walk up from the canvas to find .pse-preview-container
        var el = document.getElementById(canvasId);
        while (el) {
            if (el.classList && el.classList.contains('pse-preview-container')) return el;
            el = el.parentElement;
        }
        return containerEl;
    }

    function getContainerSize() {
        var el = findPreviewContainer();
        if (!el) {
            // Fallback: use viewport dimensions
            var w = Math.round(window.innerWidth * 0.45);
            var h = window.innerHeight - 180;
            return { w: Math.max(400, w), h: Math.max(400, h) };
        }
        var rect = el.getBoundingClientRect();
        var w = Math.round(rect.width) - 16;
        var h = Math.round(rect.height) - 16;
        if (w < 200) w = Math.round(window.innerWidth * 0.45);
        if (h < 200) h = window.innerHeight - 180;
        return { w: w, h: h };
    }

    function calcDisplayScale() {
        var cs = getContainerSize();
        if (tgtW <= 0 || tgtH <= 0) return 0.1;
        var scaleW = cs.w / tgtW;
        var scaleH = cs.h / tgtH;
        return Math.min(scaleW, scaleH);
    }

    function render() {
        if (!canvas || tgtW <= 0 || tgtH <= 0) return;

        var ds = calcDisplayScale();
        var dw = Math.max(50, Math.round(tgtW * ds));
        var dh = Math.max(50, Math.round(tgtH * ds));

        canvas.setWidth(dw);
        canvas.setHeight(dh);
        canvas.clear();

        // Checkerboard background
        if (!checkerPattern) checkerPattern = createCheckerboard();
        canvas.add(new fabric.Rect({
            left: 0, top: 0, width: dw, height: dh,
            selectable: false, evented: false,
            fill: new fabric.Pattern({ source: checkerPattern, repeat: 'repeat' })
        }));

        // Wrap overlay: dim the wrap zone so it's visually distinct from the target area
        if (wrapPx > 0) {
            var wrapD = Math.round(wrapPx * ds);
            var dimStyle = { fill: 'rgba(0,0,0,0.25)', selectable: false, evented: false };
            // Top strip
            canvas.add(new fabric.Rect(Object.assign({ left: 0, top: 0, width: dw, height: wrapD }, dimStyle)));
            // Bottom strip
            canvas.add(new fabric.Rect(Object.assign({ left: 0, top: dh - wrapD, width: dw, height: wrapD }, dimStyle)));
            // Left strip (between top and bottom)
            canvas.add(new fabric.Rect(Object.assign({ left: 0, top: wrapD, width: wrapD, height: dh - 2 * wrapD }, dimStyle)));
            // Right strip (between top and bottom)
            canvas.add(new fabric.Rect(Object.assign({ left: dw - wrapD, top: wrapD, width: wrapD, height: dh - 2 * wrapD }, dimStyle)));
        }

        // Source photo — best-fit scale to target area, then position within it
        if (sourceImg && srcW > 0 && srcH > 0) {
            var wrapD2 = wrapPx > 0 ? Math.round(wrapPx * ds) : 0;
            var innerW = dw - 2 * wrapD2;  // target display area
            var innerH = dh - 2 * wrapD2;

            // 96% of best-fit: small margin for AI to blend edges
            var fitScale = Math.min(innerW / srcW, innerH / srcH) * 0.96;
            var sW = Math.round(srcW * fitScale);
            var sH = Math.round(srcH * fitScale);

            var sX = wrapD2 + Math.round((innerW - sW) * posX);
            var sY = wrapD2 + Math.round((innerH - sH) * posY);

            // Clamp to target area (not wrap zone)
            sX = Math.max(wrapD2, Math.min(sX, wrapD2 + innerW - sW));
            sY = Math.max(wrapD2, Math.min(sY, wrapD2 + innerH - sH));

            if (sourceObj) canvas.remove(sourceObj);

            sourceObj = new fabric.Image(sourceImg, {
                left: sX, top: sY,
                scaleX: sW / sourceImg.width,
                scaleY: sH / sourceImg.height,
                selectable: true,
                hasControls: false,
                hasBorders: true,
                borderColor: 'rgba(99,102,241,0.8)',
                borderScaleFactor: 2,
                lockScalingX: true,
                lockScalingY: true,
                lockRotation: true,
                hoverCursor: 'grab',
                moveCursor: 'grabbing'
            });

            sourceObj.on('moving', function () {
                var obj = sourceObj;
                var minX = wrapD2;
                var minY = wrapD2;
                var maxX = wrapD2 + innerW - sW;
                var maxY = wrapD2 + innerH - sH;
                obj.left = Math.max(minX, Math.min(obj.left, maxX));
                obj.top = Math.max(minY, Math.min(obj.top, maxY));
                posX = (maxX - minX) > 0 ? (obj.left - minX) / (maxX - minX) : 0.5;
                posY = (maxY - minY) > 0 ? (obj.top - minY) / (maxY - minY) : 0.5;
            });

            canvas.add(sourceObj);

            // Source label
            canvas.add(new fabric.Text(srcW + ' \u00d7 ' + srcH, {
                left: sX + sW / 2, top: sY + sH / 2,
                fontSize: Math.max(10, Math.round(13 * sW / 300)),
                fill: 'rgba(255,255,255,0.8)',
                fontFamily: 'system-ui, sans-serif',
                originX: 'center', originY: 'center',
                selectable: false, evented: false,
                shadow: '0 1px 3px rgba(0,0,0,0.8)'
            }));
        }

        // Canvas wrap guide lines (subtle dashed lines showing target boundary)
        if (wrapPx > 0) {
            var wrapD = Math.round(wrapPx * ds);
            var guideStyle = { stroke: 'rgba(255,255,255,0.35)', strokeWidth: 1, strokeDashArray: [6, 4], selectable: false, evented: false };

            // Top guide
            canvas.add(new fabric.Line([0, wrapD, dw, wrapD], guideStyle));
            // Bottom guide
            canvas.add(new fabric.Line([0, dh - wrapD, dw, dh - wrapD], guideStyle));
            // Left guide
            canvas.add(new fabric.Line([wrapD, 0, wrapD, dh], guideStyle));
            // Right guide
            canvas.add(new fabric.Line([dw - wrapD, 0, dw - wrapD, dh], guideStyle));
        }

        // Target dimension label
        var labelText = wrapPx > 0
            ? tgtW + ' \u00d7 ' + tgtH + ' (incl. wrap)'
            : tgtW + ' \u00d7 ' + tgtH + ' px';
        canvas.add(new fabric.Text(labelText, {
            left: dw / 2, top: dh - 16,
            fontSize: Math.max(10, Math.round(12 * ds * (tgtW / 1000))),
            fill: 'rgba(255,255,255,0.9)',
            fontFamily: 'system-ui, sans-serif',
            originX: 'center',
            selectable: false, evented: false,
            shadow: '0 1px 3px rgba(0,0,0,0.7)'
        }));

        canvas.renderAll();
    }

    return {
        initPreview: function (id, imageDataUrl, sw, sh, tw, th, wp, px, py) {
            canvasId = id;
            srcW = sw; srcH = sh;
            tgtW = tw; tgtH = th;
            wrapPx = wp || 0;
            posX = (px !== undefined && px !== null) ? px : 0.5;
            posY = (py !== undefined && py !== null) ? py : 0.5;

            this.dispose();

            containerEl = document.getElementById(id)?.parentElement;

            canvas = new fabric.Canvas(id, {
                width: 100, height: 100,
                selection: false,
                renderOnAddRemove: false
            });

            var self = this;
            var img = new Image();
            img.onload = function () {
                sourceImg = img;
                render();
            };
            img.src = imageDataUrl;

            // Also render immediately with checkerboard (before image loads)
            render();

            var observeEl = findPreviewContainer() || containerEl;
            if (observeEl) {
                resizeObserver = new ResizeObserver(function () { render(); });
                resizeObserver.observe(observeEl);
            }
        },

        setTargetSize: function (tw, th) {
            tgtW = tw; tgtH = th;
            render();
        },

        setSourceSize: function (sw, sh) {
            srcW = sw; srcH = sh;
            render();
        },

        setPosition: function (x, y) {
            posX = x; posY = y;
            render();
        },

        getPosition: function () {
            return { x: posX, y: posY };
        },

        showResult: function (id, dataUrl, w, h, wp) {
            this.dispose();
            canvasId = id;
            tgtW = w; tgtH = h;
            wrapPx = wp || 0;
            containerEl = document.getElementById(id)?.parentElement;

            canvas = new fabric.Canvas(id, {
                width: 100, height: 100,
                selection: false,
                renderOnAddRemove: false
            });

            var resultImg = null;
            var renderResult = function () {
                if (!canvas || !resultImg) return;
                var ds = calcDisplayScale();
                var dw = Math.max(50, Math.round(w * ds));
                var dh = Math.max(50, Math.round(h * ds));
                canvas.setWidth(dw);
                canvas.setHeight(dh);
                canvas.clear();
                canvas.add(new fabric.Image(resultImg, {
                    left: 0, top: 0,
                    scaleX: dw / resultImg.width,
                    scaleY: dh / resultImg.height,
                    selectable: false, evented: false
                }));

                // Draw wrap guide lines on result
                if (wp > 0) {
                    var wrapD = Math.round(wp * ds);
                    var guideStyle = { stroke: 'rgba(255,255,255,0.3)', strokeWidth: 1, strokeDashArray: [6, 4], selectable: false, evented: false };
                    canvas.add(new fabric.Line([0, wrapD, dw, wrapD], guideStyle));
                    canvas.add(new fabric.Line([0, dh - wrapD, dw, dh - wrapD], guideStyle));
                    canvas.add(new fabric.Line([wrapD, 0, wrapD, dh], guideStyle));
                    canvas.add(new fabric.Line([dw - wrapD, 0, dw - wrapD, dh], guideStyle));
                }

                canvas.renderAll();
            };

            var img = new Image();
            img.onload = function () {
                resultImg = img;
                renderResult();
            };
            img.src = dataUrl;

            var observeEl = findPreviewContainer() || containerEl;
            if (observeEl) {
                resizeObserver = new ResizeObserver(function () { renderResult(); });
                resizeObserver.observe(observeEl);
            }
        },

        downloadResult: function (dataUrl, fileName) {
            var a = document.createElement('a');
            a.href = dataUrl;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
        },

        downloadBytes: function (base64, fileName, mimeType) {
            this.downloadResult('data:' + mimeType + ';base64,' + base64, fileName);
        },

        dispose: function () {
            if (resizeObserver) { resizeObserver.disconnect(); resizeObserver = null; }
            if (canvas) { canvas.dispose(); canvas = null; }
            sourceImg = null;
            sourceObj = null;
            containerEl = null;
        }
    };
})();
