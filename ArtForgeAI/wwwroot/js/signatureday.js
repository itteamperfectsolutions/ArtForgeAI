(function () {
    "use strict";

    var canvas = null;
    var ctx = null;
    var W = 1800, H = 2400;
    var currentTemplate = null;
    var loadedImages = [];
    var bgColor = "#0d1117";
    var borderColor = "rgba(255,255,255,0.25)";
    var emptyFill = "rgba(255,255,255,0.06)";
    var emptyBorder = "rgba(255,255,255,0.12)";

    // ── Shape path helpers ──

    function shapePath(ctx, cx, cy, r, shape) {
        ctx.beginPath();
        var i, angle, x, y, sides, offset;
        switch (shape) {
            case "hexagon":
                for (i = 0; i < 6; i++) {
                    angle = (Math.PI / 3) * i - Math.PI / 2;
                    x = cx + r * Math.cos(angle);
                    y = cy + r * Math.sin(angle);
                    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
                }
                ctx.closePath();
                break;
            case "circle":
                ctx.arc(cx, cy, r, 0, Math.PI * 2);
                break;
            case "diamond":
                ctx.moveTo(cx, cy - r); ctx.lineTo(cx + r * 0.85, cy);
                ctx.lineTo(cx, cy + r); ctx.lineTo(cx - r * 0.85, cy);
                ctx.closePath();
                break;
            case "octagon":
                for (i = 0; i < 8; i++) {
                    angle = (Math.PI / 4) * i - Math.PI / 8;
                    x = cx + r * Math.cos(angle);
                    y = cy + r * Math.sin(angle);
                    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
                }
                ctx.closePath();
                break;
            case "star":
                for (i = 0; i < 10; i++) {
                    angle = (Math.PI / 5) * i - Math.PI / 2;
                    var sr = (i % 2 === 0) ? r : r * 0.42;
                    x = cx + sr * Math.cos(angle);
                    y = cy + sr * Math.sin(angle);
                    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
                }
                ctx.closePath();
                break;
            case "heart":
                var hw = r * 0.95, hh = r * 1.1;
                ctx.moveTo(cx, cy + hh * 0.42);
                ctx.bezierCurveTo(cx - hw, cy - hh * 0.05, cx - hw * 0.55, cy - hh * 0.55, cx, cy - hh * 0.2);
                ctx.bezierCurveTo(cx + hw * 0.55, cy - hh * 0.55, cx + hw, cy - hh * 0.05, cx, cy + hh * 0.42);
                break;
            case "square":
                var s = r * 0.88;
                var cr = r * 0.08;
                ctx.moveTo(cx - s + cr, cy - s);
                ctx.lineTo(cx + s - cr, cy - s);
                ctx.arcTo(cx + s, cy - s, cx + s, cy - s + cr, cr);
                ctx.lineTo(cx + s, cy + s - cr);
                ctx.arcTo(cx + s, cy + s, cx + s - cr, cy + s, cr);
                ctx.lineTo(cx - s + cr, cy + s);
                ctx.arcTo(cx - s, cy + s, cx - s, cy + s - cr, cr);
                ctx.lineTo(cx - s, cy - s + cr);
                ctx.arcTo(cx - s, cy - s, cx - s + cr, cy - s, cr);
                ctx.closePath();
                break;
        }
    }

    function drawSlot(cx, cy, r, shape, img) {
        // Draw filled/clipped slot
        ctx.save();
        shapePath(ctx, cx, cy, r, shape);
        if (img) {
            ctx.clip();
            var scale = Math.max(2 * r / img.naturalWidth, 2 * r / img.naturalHeight);
            var dw = img.naturalWidth * scale;
            var dh = img.naturalHeight * scale;
            ctx.drawImage(img, cx - dw / 2, cy - dh / 2, dw, dh);
        } else {
            ctx.fillStyle = emptyFill;
            ctx.fill();
        }
        ctx.restore();

        // Border (outside clip)
        shapePath(ctx, cx, cy, r, shape);
        ctx.strokeStyle = img ? borderColor : emptyBorder;
        ctx.lineWidth = img ? 3 : 2;
        ctx.stroke();
    }

    function render() {
        if (!canvas || !currentTemplate) return;
        ctx.clearRect(0, 0, W, H);

        // Background
        ctx.fillStyle = bgColor;
        ctx.fillRect(0, 0, W, H);

        var slots = currentTemplate.slots;
        var tShape = currentTemplate.shape;

        for (var i = 0; i < slots.length; i++) {
            var s = slots[i];
            var shape = s.shape || tShape;
            var img = (i < loadedImages.length) ? loadedImages[i] : null;
            drawSlot(s.cx, s.cy, s.r, shape, img);
        }
    }

    // ── Template definitions (coordinates on 1800x2400 canvas) ──

    var templates = {
        hexHoneycomb: {
            name: "Hexagon Honeycomb",
            shape: "hexagon",
            bgColor: "#0d1117",
            slots: [
                // Row 1
                { cx: 170, cy: 130, r: 120 },
                { cx: 420, cy: 120, r: 135 },
                { cx: 690, cy: 110, r: 145 },
                { cx: 970, cy: 100, r: 150 },
                { cx: 1250, cy: 110, r: 145 },
                { cx: 1510, cy: 120, r: 130 },
                { cx: 1720, cy: 135, r: 100 },
                // Row 2
                { cx: 210, cy: 330, r: 95 },
                { cx: 420, cy: 320, r: 108 },
                { cx: 650, cy: 310, r: 115 },
                { cx: 1150, cy: 310, r: 115 },
                { cx: 1380, cy: 320, r: 108 },
                { cx: 1590, cy: 330, r: 95 },
                // Flanking small
                { cx: 160, cy: 530, r: 78 },
                { cx: 1640, cy: 530, r: 78 },
                { cx: 160, cy: 710, r: 78 },
                { cx: 1640, cy: 710, r: 78 },
                { cx: 370, cy: 560, r: 85 },
                { cx: 1430, cy: 560, r: 85 },
                // Hero center
                { cx: 900, cy: 620, r: 270 },
                // Lower flanking
                { cx: 370, cy: 760, r: 85 },
                { cx: 1430, cy: 760, r: 85 },
                // Row 3
                { cx: 210, cy: 950, r: 95 },
                { cx: 420, cy: 940, r: 108 },
                { cx: 650, cy: 930, r: 115 },
                { cx: 1150, cy: 930, r: 115 },
                { cx: 1380, cy: 940, r: 108 },
                { cx: 1590, cy: 950, r: 95 },
                // Row 4
                { cx: 300, cy: 1130, r: 110 },
                { cx: 550, cy: 1120, r: 125 },
                { cx: 830, cy: 1110, r: 135 },
                { cx: 1100, cy: 1110, r: 135 },
                { cx: 1350, cy: 1120, r: 125 },
                { cx: 1580, cy: 1130, r: 100 },
                // Row 5
                { cx: 350, cy: 1320, r: 90 },
                { cx: 570, cy: 1310, r: 100 },
                { cx: 800, cy: 1305, r: 105 },
                { cx: 1020, cy: 1305, r: 105 },
                { cx: 1240, cy: 1310, r: 100 },
                { cx: 1460, cy: 1320, r: 90 },
            ]
        },

        circleGalaxy: {
            name: "Circle Galaxy",
            shape: "circle",
            bgColor: "#0f0a1a",
            slots: [
                // Hero center
                { cx: 900, cy: 700, r: 280 },
                // Inner ring (6)
                { cx: 500, cy: 380, r: 150 },
                { cx: 900, cy: 300, r: 160 },
                { cx: 1300, cy: 380, r: 150 },
                { cx: 1350, cy: 750, r: 140 },
                { cx: 1300, cy: 1050, r: 150 },
                { cx: 500, cy: 1050, r: 150 },
                { cx: 450, cy: 750, r: 140 },
                // Outer ring (8)
                { cx: 200, cy: 200, r: 110 },
                { cx: 900, cy: 100, r: 100 },
                { cx: 1600, cy: 200, r: 110 },
                { cx: 1700, cy: 550, r: 95 },
                { cx: 1700, cy: 900, r: 95 },
                { cx: 1600, cy: 1200, r: 110 },
                { cx: 200, cy: 1200, r: 110 },
                { cx: 100, cy: 550, r: 95 },
                { cx: 100, cy: 900, r: 95 },
            ]
        },

        diamondMosaic: {
            name: "Diamond Mosaic",
            shape: "diamond",
            bgColor: "#0d1117",
            slots: [
                // Row 1
                { cx: 250, cy: 200, r: 150 },
                { cx: 600, cy: 200, r: 150 },
                { cx: 950, cy: 200, r: 150 },
                { cx: 1300, cy: 200, r: 150 },
                { cx: 1600, cy: 200, r: 140 },
                // Row 2 (offset)
                { cx: 420, cy: 450, r: 160 },
                { cx: 780, cy: 450, r: 160 },
                { cx: 1130, cy: 450, r: 160 },
                { cx: 1470, cy: 450, r: 150 },
                // Hero
                { cx: 900, cy: 750, r: 260 },
                // Row 3
                { cx: 250, cy: 750, r: 140 },
                { cx: 1550, cy: 750, r: 140 },
                // Row 4
                { cx: 350, cy: 1020, r: 155 },
                { cx: 700, cy: 1020, r: 155 },
                { cx: 1100, cy: 1020, r: 155 },
                { cx: 1450, cy: 1020, r: 155 },
                // Row 5
                { cx: 250, cy: 1260, r: 140 },
                { cx: 550, cy: 1260, r: 145 },
                { cx: 900, cy: 1260, r: 155 },
                { cx: 1250, cy: 1260, r: 145 },
                { cx: 1550, cy: 1260, r: 140 },
            ]
        },

        mixedMosaic: {
            name: "Mixed Mosaic",
            shape: "circle",
            bgColor: "#111318",
            slots: [
                // Top row - hexagons
                { cx: 250, cy: 180, r: 140, shape: "hexagon" },
                { cx: 580, cy: 180, r: 140, shape: "hexagon" },
                { cx: 900, cy: 170, r: 150, shape: "hexagon" },
                { cx: 1220, cy: 180, r: 140, shape: "hexagon" },
                { cx: 1550, cy: 180, r: 140, shape: "hexagon" },
                // Middle - circles
                { cx: 300, cy: 500, r: 130, shape: "circle" },
                { cx: 1500, cy: 500, r: 130, shape: "circle" },
                // Hero - octagon
                { cx: 900, cy: 600, r: 260, shape: "octagon" },
                // Sides - diamonds
                { cx: 300, cy: 780, r: 120, shape: "diamond" },
                { cx: 1500, cy: 780, r: 120, shape: "diamond" },
                // Bottom - stars + circles
                { cx: 250, cy: 1020, r: 130, shape: "star" },
                { cx: 560, cy: 1020, r: 140, shape: "circle" },
                { cx: 900, cy: 1020, r: 150, shape: "hexagon" },
                { cx: 1240, cy: 1020, r: 140, shape: "circle" },
                { cx: 1550, cy: 1020, r: 130, shape: "star" },
                // Bottom row
                { cx: 350, cy: 1250, r: 125, shape: "hexagon" },
                { cx: 700, cy: 1250, r: 135, shape: "diamond" },
                { cx: 1100, cy: 1250, r: 135, shape: "diamond" },
                { cx: 1450, cy: 1250, r: 125, shape: "hexagon" },
            ]
        },

        heartFrame: {
            name: "Heart Frame",
            shape: "heart",
            bgColor: "#1a0a10",
            slots: [
                // Heart outline arrangement
                { cx: 450, cy: 250, r: 150 },
                { cx: 750, cy: 130, r: 140 },
                { cx: 1050, cy: 130, r: 140 },
                { cx: 1350, cy: 250, r: 150 },
                { cx: 250, cy: 480, r: 140 },
                { cx: 1550, cy: 480, r: 140 },
                { cx: 200, cy: 750, r: 130 },
                { cx: 1600, cy: 750, r: 130 },
                // Hero center
                { cx: 900, cy: 600, r: 250 },
                // Lower
                { cx: 350, cy: 970, r: 140 },
                { cx: 1450, cy: 970, r: 140 },
                { cx: 550, cy: 1150, r: 130 },
                { cx: 1250, cy: 1150, r: 130 },
                { cx: 900, cy: 1250, r: 140 },
            ]
        }
    };

    // ── Public API ──

    function init(canvasId) {
        canvas = document.getElementById(canvasId);
        if (!canvas) return;
        canvas.width = W;
        canvas.height = H;
        ctx = canvas.getContext("2d");
        render();
    }

    function loadTemplate(key, bg) {
        var tmpl = templates[key];
        if (!tmpl) return;
        currentTemplate = tmpl;
        bgColor = bg || tmpl.bgColor || "#0d1117";
        render();
    }

    function setBackground(color) {
        bgColor = color || "#0d1117";
        render();
    }

    function setPhotos(dataUrls) {
        loadedImages = [];
        if (!dataUrls || dataUrls.length === 0) {
            render();
            return;
        }

        var toLoad = dataUrls.length;
        var loaded = 0;

        for (var i = 0; i < toLoad; i++) {
            (function (idx) {
                var img = new Image();
                img.crossOrigin = "anonymous";
                img.onload = function () {
                    loadedImages[idx] = img;
                    loaded++;
                    if (loaded >= toLoad) render();
                };
                img.onerror = function () {
                    loadedImages[idx] = null;
                    loaded++;
                    if (loaded >= toLoad) render();
                };
                img.src = dataUrls[idx];
            })(i);
        }
    }

    function removePhoto(index) {
        if (index >= 0 && index < loadedImages.length) {
            loadedImages.splice(index, 1);
        }
        render();
    }

    function getSlotCount() {
        return currentTemplate ? currentTemplate.slots.length : 0;
    }

    function getTemplateList() {
        var list = [];
        for (var key in templates) {
            list.push({ key: key, name: templates[key].name, slotCount: templates[key].slots.length });
        }
        return list;
    }

    function downloadPng(filename) {
        if (!canvas) return;
        canvas.toBlob(function (blob) {
            var url = URL.createObjectURL(blob);
            var a = document.createElement("a");
            a.href = url;
            a.download = filename || "signature_day.png";
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(function () { URL.revokeObjectURL(url); }, 5000);
        }, "image/png");
    }

    function downloadJpg(filename) {
        if (!canvas) return;
        // For JPG, draw on white background first
        var tmpCanvas = document.createElement("canvas");
        tmpCanvas.width = W;
        tmpCanvas.height = H;
        var tmpCtx = tmpCanvas.getContext("2d");
        tmpCtx.fillStyle = bgColor;
        tmpCtx.fillRect(0, 0, W, H);
        tmpCtx.drawImage(canvas, 0, 0);

        tmpCanvas.toBlob(function (blob) {
            var url = URL.createObjectURL(blob);
            var a = document.createElement("a");
            a.href = url;
            a.download = filename || "signature_day.jpg";
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(function () { URL.revokeObjectURL(url); }, 5000);
        }, "image/jpeg", 0.95);
    }

    // ── Generate template thumbnail (small preview) ──

    function renderThumbnail(canvasId, key) {
        var tmpl = templates[key];
        if (!tmpl) return;

        var thumbCanvas = document.getElementById(canvasId);
        if (!thumbCanvas) return;

        var tw = thumbCanvas.width;
        var th = thumbCanvas.height;
        var tctx = thumbCanvas.getContext("2d");
        var sx = tw / W;
        var sy = th / H;

        tctx.clearRect(0, 0, tw, th);
        tctx.fillStyle = tmpl.bgColor || "#0d1117";
        tctx.fillRect(0, 0, tw, th);

        var tShape = tmpl.shape;
        for (var i = 0; i < tmpl.slots.length; i++) {
            var s = tmpl.slots[i];
            var shape = s.shape || tShape;
            var cx = s.cx * sx;
            var cy = s.cy * sy;
            var r = s.r * sx;

            tctx.save();
            shapePath(tctx, cx, cy, r, shape);
            tctx.fillStyle = "rgba(255,255,255,0.08)";
            tctx.fill();
            tctx.restore();

            shapePath(tctx, cx, cy, r, shape);
            tctx.strokeStyle = "rgba(255,255,255,0.18)";
            tctx.lineWidth = 1;
            tctx.stroke();
        }
    }

    window.signatureDayHelper = {
        init: init,
        loadTemplate: loadTemplate,
        setBackground: setBackground,
        setPhotos: setPhotos,
        removePhoto: removePhoto,
        getSlotCount: getSlotCount,
        getTemplateList: getTemplateList,
        downloadPng: downloadPng,
        downloadJpg: downloadJpg,
        renderThumbnail: renderThumbnail
    };
})();
