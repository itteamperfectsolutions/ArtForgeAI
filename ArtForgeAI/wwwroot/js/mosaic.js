// ===== ArtForge AI - Mosaic Poster File Helpers =====
window.mosaicFileHelper = {
    // Register a plain <input type="file"> to call back into .NET on change
    register: function (inputId, dotNetRef, methodName) {
        var el = document.getElementById(inputId);
        if (!el) return;
        el.addEventListener("change", async function () {
            var files = el.files;
            if (!files || files.length === 0) return;
            var results = [];
            for (var i = 0; i < files.length; i++) {
                var file = files[i];
                var base64 = await new Promise(function (resolve) {
                    var reader = new FileReader();
                    reader.onload = function () {
                        // result is "data:image/...;base64,XXXX"
                        resolve(reader.result.split(",")[1]);
                    };
                    reader.readAsDataURL(file);
                });
                results.push({ name: file.name, base64: base64 });
            }
            // Reset input so same file can be re-selected
            el.value = "";
            await dotNetRef.invokeMethodAsync(methodName, results);
        });
    },
    click: function (inputId) {
        var el = document.getElementById(inputId);
        if (el) el.click();
    }
};

// ===== ArtForge AI - Mosaic Poster Module =====
window.mosaicPoster = (function () {
    "use strict";

    let canvas = null;
    let mainImage = null;
    let bgImages = [];       // { img: fabric.Image, origW, origH }
    let bgTiles = [];         // fabric objects on canvas
    let foregroundGroup = null;
    let canvasW = 3000, canvasH = 2000;
    let resizeObserver = null;

    // Current settings
    let settings = {
        layout: "grid",
        tileGutter: 4,
        bgOpacity: 0.85,
        bgBlurPx: 0,
        bgSaturation: 0,
        bgBrightness: 0,
        bgTintColor: "",
        bgTintOpacity: 0,
        foregroundScale: 0.45,
        borderWidth: 12,
        cornerRadius: 16,
        shadowBlur: 40,
        shadowOffsetX: 0,
        shadowOffsetY: 8,
        shadowColor: "rgba(0,0,0,0.5)"
    };

    // ── Public API ──

    function init(canvasId, w, h) {
        canvasW = w || 3000;
        canvasH = h || 2000;

        if (canvas) {
            canvas.dispose();
        }

        canvas = new fabric.Canvas(canvasId, {
            width: canvasW,
            height: canvasH,
            backgroundColor: "#ffffff",
            selection: false,
            renderOnAddRemove: false
        });

        // Scale canvas element to fit container
        _fitCanvasToContainer();

        // Observe container resizes for mobile responsiveness
        if (resizeObserver) {
            resizeObserver.disconnect();
        }
        const wrapper = canvas.getElement().parentElement;
        if (wrapper && typeof ResizeObserver !== "undefined") {
            resizeObserver = new ResizeObserver(function () {
                _fitCanvasToContainer();
            });
            resizeObserver.observe(wrapper);
        }

        return true;
    }

    function _fitCanvasToContainer() {
        if (!canvas) return;
        const el = canvas.getElement();
        const wrapper = el.parentElement;
        if (!wrapper) return;
        const maxW = wrapper.clientWidth;
        const maxH = window.innerHeight * 0.7;
        const scale = Math.min(maxW / canvasW, maxH / canvasH, 1);
        canvas.setZoom(scale);
        canvas.setDimensions({
            width: canvasW * scale,
            height: canvasH * scale
        });
    }

    async function setMainImage(imageUrl) {
        return new Promise((resolve, reject) => {
            const imgEl = new Image();
            imgEl.crossOrigin = "anonymous";
            imgEl.onload = function () {
                mainImage = {
                    element: imgEl,
                    width: imgEl.naturalWidth,
                    height: imgEl.naturalHeight,
                    url: imageUrl
                };
                resolve(true);
            };
            imgEl.onerror = function () {
                reject("Failed to load main image");
            };
            imgEl.src = imageUrl;
        });
    }

    async function setBgImages(imageUrls) {
        bgImages = [];
        const BATCH = 4;
        const MAX_DIM = 800;
        let loaded = 0;

        for (let i = 0; i < imageUrls.length; i += BATCH) {
            const batch = imageUrls.slice(i, i + BATCH);
            const promises = batch.map(url => _loadAndResize(url, MAX_DIM));
            const results = await Promise.allSettled(promises);
            for (const r of results) {
                if (r.status === "fulfilled" && r.value) {
                    bgImages.push(r.value);
                    loaded++;
                }
            }
        }
        return { loaded: loaded };
    }

    function _loadAndResize(url, maxDim) {
        return new Promise((resolve) => {
            const imgEl = new Image();
            imgEl.crossOrigin = "anonymous";
            imgEl.onload = function () {
                // Resize if needed via offscreen canvas
                let w = imgEl.naturalWidth;
                let h = imgEl.naturalHeight;
                if (w > maxDim || h > maxDim) {
                    const scale = Math.min(maxDim / w, maxDim / h);
                    w = Math.round(w * scale);
                    h = Math.round(h * scale);
                }
                const offscreen = document.createElement("canvas");
                offscreen.width = w;
                offscreen.height = h;
                const ctx = offscreen.getContext("2d");
                ctx.drawImage(imgEl, 0, 0, w, h);

                resolve({
                    dataUrl: offscreen.toDataURL("image/jpeg", 0.85),
                    origW: w,
                    origH: h
                });
            };
            imgEl.onerror = function () {
                resolve(null);
            };
            imgEl.src = url;
        });
    }

    function setLayout(mode, newSettings) {
        if (newSettings) {
            Object.assign(settings, newSettings);
        }
        settings.layout = mode || settings.layout;
        _render();
    }

    function updateSettings(newSettings) {
        if (newSettings) {
            Object.assign(settings, newSettings);
        }
        _render();
    }

    function applyPreset(presetName) {
        const presets = _getPresets();
        const preset = presets[presetName];
        if (preset) {
            Object.assign(settings, preset);
            _render();
        }
    }

    function exportComposite() {
        if (!canvas) return null;
        const oldZoom = canvas.getZoom();
        try {
            canvas.setZoom(1);
            canvas.setDimensions({ width: canvasW, height: canvasH });
            canvas.renderAll();
            return canvas.toDataURL({ format: "png", multiplier: 1 });
        } finally {
            canvas.setZoom(oldZoom);
            canvas.setDimensions({ width: canvasW * oldZoom, height: canvasH * oldZoom });
            canvas.renderAll();
        }
    }

    async function exportNormalizedZip() {
        if (typeof JSZip === "undefined") {
            throw new Error("JSZip not loaded");
        }
        const zip = new JSZip();
        for (let i = 0; i < bgImages.length; i++) {
            const bg = bgImages[i];
            const base64 = bg.dataUrl.split(",")[1];
            const name = "bg_" + String(i + 1).padStart(3, "0") + ".png";
            zip.file(name, base64, { base64: true });
        }
        const blob = await zip.generateAsync({ type: "base64" });
        return blob;
    }

    function exportMetadata() {
        const tiles = [];
        for (let i = 0; i < bgTiles.length; i++) {
            const t = bgTiles[i];
            tiles.push({
                index: i,
                left: Math.round(t.left),
                top: Math.round(t.top),
                width: Math.round(t.width * (t.scaleX || 1)),
                height: Math.round(t.height * (t.scaleY || 1)),
                angle: Math.round((t.angle || 0) * 100) / 100
            });
        }

        const meta = {
            canvasWidth: canvasW,
            canvasHeight: canvasH,
            layout: settings.layout,
            settings: { ...settings },
            foreground: mainImage ? {
                originalWidth: mainImage.width,
                originalHeight: mainImage.height
            } : null,
            tiles: tiles,
            tileCount: bgTiles.length,
            exportedAt: new Date().toISOString()
        };
        return JSON.stringify(meta, null, 2);
    }

    function dispose() {
        if (resizeObserver) {
            resizeObserver.disconnect();
            resizeObserver = null;
        }
        if (canvas) {
            canvas.dispose();
            canvas = null;
        }
        mainImage = null;
        bgImages = [];
        bgTiles = [];
        foregroundGroup = null;
    }

    // ── Render Pipeline ──

    function _render() {
        if (!canvas || bgImages.length === 0) return;

        canvas.clear();
        canvas.backgroundColor = "#ffffff";
        bgTiles = [];
        foregroundGroup = null;

        // 1. Compute tile positions based on layout
        const positions = _computeLayout(settings.layout, bgImages.length);

        // 2. Place BG tiles
        _placeBgTiles(positions);

        // 3. Place foreground
        if (mainImage) {
            _placeForeground();
        }

        canvas.renderAll();
    }

    // ── Layout Algorithms ──

    function _computeLayout(mode, n) {
        switch (mode) {
            case "masonry": return _layoutMasonry(n);
            case "polaroid": return _layoutPolaroid(n);
            case "hex": return _layoutHex(n);
            case "centerCircle": return _layoutCenterCircle(n);
            case "editorialMix": return _layoutEditorialMix(n);
            case "featureLeft": return _layoutFeatureLeft(n);
            case "featureTop": return _layoutFeatureTop(n);
            case "triptych": return _layoutTriptych(n);
            case "storyboard": return _layoutStoryboard(n);
            case "panoramicStrip": return _layoutPanoramicStrip(n);
            default: return _layoutGrid(n);
        }
    }

    function _layoutGrid(n) {
        const aspect = canvasW / canvasH;
        const cols = Math.ceil(Math.sqrt(n * aspect));
        const rows = Math.ceil(n / cols);
        const g = settings.tileGutter;
        const tileW = (canvasW - g * (cols + 1)) / cols;
        const tileH = (canvasH - g * (rows + 1)) / rows;
        const positions = [];

        for (let i = 0; i < n; i++) {
            const col = i % cols;
            const row = Math.floor(i / cols);
            positions.push({
                left: g + col * (tileW + g),
                top: g + row * (tileH + g),
                width: tileW,
                height: tileH,
                angle: 0
            });
        }
        return positions;
    }

    function _layoutMasonry(n) {
        const cols = canvasW > canvasH ? 4 : 3;
        const g = settings.tileGutter;
        const colW = (canvasW - g * (cols + 1)) / cols;
        const colHeights = new Array(cols).fill(g);
        const positions = [];

        // Deterministic pseudo-random based on index
        function pseudoRandom(seed) {
            let x = Math.sin(seed * 9301 + 49297) * 49297;
            return x - Math.floor(x);
        }

        for (let i = 0; i < n; i++) {
            // Find shortest column
            let minCol = 0;
            for (let c = 1; c < cols; c++) {
                if (colHeights[c] < colHeights[minCol]) minCol = c;
            }

            const aspectRatio = 0.6 + pseudoRandom(i + 1) * 1.2; // 0.6 - 1.8
            const tileH = colW * aspectRatio;

            positions.push({
                left: g + minCol * (colW + g),
                top: colHeights[minCol],
                width: colW,
                height: tileH,
                angle: 0
            });

            colHeights[minCol] += tileH + g;
        }
        return positions;
    }

    function _layoutPolaroid(n) {
        const tileSize = 200;
        const positions = [];
        const centerX = canvasW / 2;
        const centerY = canvasH / 2;
        const exclusionRadius = Math.min(canvasW, canvasH) * 0.15;

        function pseudoRandom(seed) {
            let x = Math.sin(seed * 9301 + 49297) * 49297;
            return x - Math.floor(x);
        }

        for (let i = 0; i < n; i++) {
            let left, top, tries = 0;
            do {
                left = pseudoRandom(i * 3 + tries) * (canvasW - tileSize);
                top = pseudoRandom(i * 7 + tries + 100) * (canvasH - tileSize);
                tries++;
            } while (
                tries < 20 &&
                Math.hypot(left + tileSize / 2 - centerX, top + tileSize / 2 - centerY) < exclusionRadius
            );

            const angle = (pseudoRandom(i * 5 + 50) - 0.5) * 30; // ±15°

            positions.push({
                left: left,
                top: top,
                width: tileSize,
                height: tileSize,
                angle: angle
            });
        }
        return positions;
    }

    function _layoutHex(n) {
        const g = settings.tileGutter;
        // Calculate hex tile size to fill canvas
        const approxCols = Math.ceil(Math.sqrt(n * (canvasW / canvasH)));
        const tileW = (canvasW - g * (approxCols + 1)) / approxCols;
        const tileH = tileW * 0.866; // hex ratio
        const rows = Math.ceil(n / approxCols);
        const positions = [];

        for (let i = 0; i < n; i++) {
            const col = i % approxCols;
            const row = Math.floor(i / approxCols);
            const offsetX = (row % 2 === 1) ? tileW / 2 : 0;

            positions.push({
                left: g + col * (tileW + g) + offsetX,
                top: g + row * (tileH + g),
                width: tileW,
                height: tileH,
                angle: 0
            });
        }
        return positions;
    }

    // ── Collage Layout Algorithms ──

    function _layoutCenterCircle(n) {
        var g = settings.tileGutter;
        var halfW = (canvasW - g) / 2;
        var halfH = (canvasH - g) / 2;
        var radius = Math.min(canvasW, canvasH) * 0.22;

        return [
            { left: 0, top: 0, width: halfW, height: halfH, angle: 0 },
            { left: halfW + g, top: 0, width: halfW, height: halfH, angle: 0 },
            { left: 0, top: halfH + g, width: halfW, height: halfH, angle: 0 },
            { left: halfW + g, top: halfH + g, width: halfW, height: halfH, angle: 0 },
            {
                left: canvasW / 2 - radius, top: canvasH / 2 - radius,
                width: radius * 2, height: radius * 2, angle: 0, shape: "circle"
            }
        ];
    }

    function _layoutEditorialMix(n) {
        var g = settings.tileGutter;
        var r1H = canvasH * 0.38;
        var r2H = canvasH * 0.28;
        var r3H = canvasH - r1H - r2H - g * 2;

        return [
            // Row 1: 2 photos (slightly asymmetric widths)
            { left: 0, top: 0, width: canvasW * 0.52 - g / 2, height: r1H, angle: 0 },
            { left: canvasW * 0.52 + g / 2, top: 0, width: canvasW * 0.48 - g / 2, height: r1H, angle: 0 },
            // Row 2: 3 photos
            { left: 0, top: r1H + g, width: canvasW * 0.30 - g, height: r2H, angle: 0 },
            { left: canvasW * 0.30, top: r1H + g, width: canvasW * 0.35, height: r2H, angle: 0 },
            { left: canvasW * 0.65 + g, top: r1H + g, width: canvasW * 0.35 - g, height: r2H, angle: 0 },
            // Row 3: 2 photos
            { left: 0, top: r1H + r2H + g * 2, width: canvasW * 0.58 - g / 2, height: r3H, angle: 0 },
            { left: canvasW * 0.58 + g / 2, top: r1H + r2H + g * 2, width: canvasW * 0.42 - g / 2, height: r3H, angle: 0 }
        ];
    }

    function _layoutFeatureLeft(n) {
        var g = settings.tileGutter;
        var leftW = canvasW * 0.6 - g / 2;
        var rightW = canvasW * 0.4 - g / 2;
        var rightH = (canvasH - g * 2) / 3;

        return [
            { left: 0, top: 0, width: leftW, height: canvasH, angle: 0 },
            { left: leftW + g, top: 0, width: rightW, height: rightH, angle: 0 },
            { left: leftW + g, top: rightH + g, width: rightW, height: rightH, angle: 0 },
            { left: leftW + g, top: (rightH + g) * 2, width: rightW, height: rightH, angle: 0 }
        ];
    }

    function _layoutFeatureTop(n) {
        var g = settings.tileGutter;
        var topH = canvasH * 0.6 - g / 2;
        var botH = canvasH * 0.4 - g / 2;
        var colW = (canvasW - g * 2) / 3;

        return [
            { left: 0, top: 0, width: canvasW, height: topH, angle: 0 },
            { left: 0, top: topH + g, width: colW, height: botH, angle: 0 },
            { left: colW + g, top: topH + g, width: colW, height: botH, angle: 0 },
            { left: (colW + g) * 2, top: topH + g, width: colW, height: botH, angle: 0 }
        ];
    }

    function _layoutTriptych(n) {
        var g = settings.tileGutter;
        var colW = (canvasW - g * 2) / 3;

        return [
            { left: 0, top: 0, width: colW, height: canvasH, angle: 0 },
            { left: colW + g, top: 0, width: colW, height: canvasH, angle: 0 },
            { left: (colW + g) * 2, top: 0, width: colW, height: canvasH, angle: 0 }
        ];
    }

    function _layoutStoryboard(n) {
        var g = settings.tileGutter;
        var topH = canvasH * 0.55;
        var botH = canvasH - topH - g;
        var rightTopH1 = topH * 0.55 - g / 2;
        var rightTopH2 = topH - rightTopH1 - g;

        return [
            // Hero (top-left)
            { left: 0, top: 0, width: canvasW * 0.6 - g / 2, height: topH, angle: 0 },
            // Top-right upper
            { left: canvasW * 0.6 + g / 2, top: 0, width: canvasW * 0.4 - g / 2, height: rightTopH1, angle: 0 },
            // Top-right lower
            { left: canvasW * 0.6 + g / 2, top: rightTopH1 + g, width: canvasW * 0.4 - g / 2, height: rightTopH2, angle: 0 },
            // Bottom-left
            { left: 0, top: topH + g, width: canvasW * 0.4 - g / 2, height: botH, angle: 0 },
            // Bottom-right
            { left: canvasW * 0.4 + g / 2, top: topH + g, width: canvasW * 0.6 - g / 2, height: botH, angle: 0 }
        ];
    }

    function _layoutPanoramicStrip(n) {
        var g = settings.tileGutter;
        var cols = Math.min(Math.max(n, 3), 8);
        var colW = (canvasW - g * (cols - 1)) / cols;
        var positions = [];

        for (var i = 0; i < cols; i++) {
            positions.push({
                left: i * (colW + g),
                top: 0,
                width: colW,
                height: canvasH,
                angle: 0
            });
        }
        return positions;
    }

    // ── Tile Placement ──

    function _placeBgTiles(positions) {
        for (let i = 0; i < positions.length; i++) {
            const bgIdx = i % bgImages.length;
            const bg = bgImages[bgIdx];
            const pos = positions[i];

            const imgEl = new Image();
            imgEl.src = bg.dataUrl;

            const fabricImg = new fabric.Image(imgEl, {
                selectable: false,
                evented: false
            });

            // Cover-crop: scale to fill tile, then clip
            const scaleX = pos.width / bg.origW;
            const scaleY = pos.height / bg.origH;
            const coverScale = Math.max(scaleX, scaleY);

            fabricImg.set({
                scaleX: coverScale,
                scaleY: coverScale,
                left: pos.left + pos.width / 2,
                top: pos.top + pos.height / 2,
                originX: "center",
                originY: "center",
                angle: pos.angle || 0
            });

            // Clip to tile shape
            if (pos.shape === "circle") {
                var circleR = Math.min(pos.width, pos.height) / 2;
                // White border ring behind circle
                canvas.add(new fabric.Circle({
                    radius: circleR + settings.tileGutter + 2,
                    left: pos.left + pos.width / 2,
                    top: pos.top + pos.height / 2,
                    originX: "center",
                    originY: "center",
                    fill: "#ffffff",
                    selectable: false,
                    evented: false
                }));
                fabricImg.clipPath = new fabric.Circle({
                    radius: circleR,
                    originX: "center",
                    originY: "center",
                    absolutePositioned: false
                });
            } else {
                fabricImg.clipPath = new fabric.Rect({
                    width: pos.width,
                    height: pos.height,
                    originX: "center",
                    originY: "center",
                    absolutePositioned: false
                });
            }

            // Apply filters
            _applyBgFilters(fabricImg);

            canvas.add(fabricImg);
            bgTiles.push(fabricImg);
        }
    }

    function _applyBgFilters(img) {
        img.filters = [];

        if (settings.bgOpacity < 1) {
            img.set("opacity", settings.bgOpacity);
        } else {
            img.set("opacity", 1);
        }

        if (settings.bgBlurPx > 0) {
            img.filters.push(new fabric.filters.Blur({
                blur: settings.bgBlurPx / 100
            }));
        }

        if (settings.bgSaturation !== 0) {
            img.filters.push(new fabric.filters.Saturation({
                saturation: settings.bgSaturation
            }));
        }

        if (settings.bgBrightness !== 0) {
            img.filters.push(new fabric.filters.Brightness({
                brightness: settings.bgBrightness
            }));
        }

        if (settings.bgTintColor && settings.bgTintOpacity > 0) {
            img.filters.push(new fabric.filters.BlendColor({
                color: settings.bgTintColor,
                mode: "tint",
                alpha: settings.bgTintOpacity
            }));
        }

        img.applyFilters();
    }

    // ── Foreground Placement ──

    function _placeForeground() {
        if (!mainImage || settings.foregroundScale <= 0) return;

        const imgEl = new Image();
        imgEl.crossOrigin = "anonymous";
        imgEl.src = mainImage.url;

        const fabricImg = new fabric.Image(imgEl, {
            selectable: false,
            evented: false
        });

        // Scale to foregroundScale × min(canvasW, canvasH)
        const targetSize = settings.foregroundScale * Math.min(canvasW, canvasH);
        const imgAspect = mainImage.width / mainImage.height;
        let fgW, fgH;
        if (imgAspect >= 1) {
            fgW = targetSize;
            fgH = targetSize / imgAspect;
        } else {
            fgH = targetSize;
            fgW = targetSize * imgAspect;
        }

        const scaleImg = fgW / mainImage.width;
        fabricImg.set({
            scaleX: scaleImg,
            scaleY: scaleImg,
            left: canvasW / 2,
            top: canvasH / 2,
            originX: "center",
            originY: "center"
        });

        // Rounded corners via clipPath
        if (settings.cornerRadius > 0) {
            fabricImg.clipPath = new fabric.Rect({
                width: fgW,
                height: fgH,
                rx: settings.cornerRadius,
                ry: settings.cornerRadius,
                originX: "center",
                originY: "center",
                absolutePositioned: false
            });
        }

        // Shadow
        fabricImg.set("shadow", new fabric.Shadow({
            color: settings.shadowColor || "rgba(0,0,0,0.5)",
            blur: settings.shadowBlur || 40,
            offsetX: settings.shadowOffsetX || 0,
            offsetY: settings.shadowOffsetY || 8
        }));

        // White border - draw behind the image
        if (settings.borderWidth > 0) {
            const border = new fabric.Rect({
                width: fgW + settings.borderWidth * 2,
                height: fgH + settings.borderWidth * 2,
                rx: settings.cornerRadius + settings.borderWidth / 2,
                ry: settings.cornerRadius + settings.borderWidth / 2,
                fill: "#ffffff",
                left: canvasW / 2,
                top: canvasH / 2,
                originX: "center",
                originY: "center",
                selectable: false,
                evented: false,
                shadow: new fabric.Shadow({
                    color: settings.shadowColor || "rgba(0,0,0,0.5)",
                    blur: settings.shadowBlur || 40,
                    offsetX: settings.shadowOffsetX || 0,
                    offsetY: settings.shadowOffsetY || 8
                })
            });
            canvas.add(border);
        }

        canvas.add(fabricImg);
        foregroundGroup = fabricImg;
    }

    // ── Presets ──

    function _getPresets() {
        return {
            classicGrid: {
                layout: "grid",
                tileGutter: 4,
                bgOpacity: 0.9,
                bgBlurPx: 0,
                bgSaturation: 0,
                bgBrightness: 0,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0.45,
                borderWidth: 12,
                cornerRadius: 12,
                shadowBlur: 40,
                shadowOffsetY: 8
            },
            premiumMasonry: {
                layout: "masonry",
                tileGutter: 6,
                bgOpacity: 0.6,
                bgBlurPx: 15,
                bgSaturation: -0.3,
                bgBrightness: -0.1,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0.5,
                borderWidth: 16,
                cornerRadius: 20,
                shadowBlur: 60,
                shadowOffsetY: 12
            },
            festivePolaroid: {
                layout: "polaroid",
                tileGutter: 0,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: 0.2,
                bgBrightness: 0.05,
                bgTintColor: "#ffd700",
                bgTintOpacity: 0.1,
                foregroundScale: 0.4,
                borderWidth: 20,
                cornerRadius: 8,
                shadowBlur: 30,
                shadowOffsetY: 6
            },
            natureCircle: {
                layout: "centerCircle",
                tileGutter: 8,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: 0.1,
                bgBrightness: 0,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0,
                borderWidth: 0,
                cornerRadius: 0,
                shadowBlur: 0,
                shadowOffsetY: 0
            },
            editorialSpread: {
                layout: "editorialMix",
                tileGutter: 6,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: 0,
                bgBrightness: 0,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0,
                borderWidth: 0,
                cornerRadius: 0,
                shadowBlur: 0,
                shadowOffsetY: 0
            },
            cleanFeature: {
                layout: "featureLeft",
                tileGutter: 4,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: 0,
                bgBrightness: 0,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0,
                borderWidth: 0,
                cornerRadius: 0,
                shadowBlur: 0,
                shadowOffsetY: 0
            },
            warmStoryboard: {
                layout: "storyboard",
                tileGutter: 5,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: 0.15,
                bgBrightness: 0.03,
                bgTintColor: "#f5e6d3",
                bgTintOpacity: 0.08,
                foregroundScale: 0,
                borderWidth: 0,
                cornerRadius: 0,
                shadowBlur: 0,
                shadowOffsetY: 0
            },
            elegantTriptych: {
                layout: "triptych",
                tileGutter: 10,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: -0.2,
                bgBrightness: 0,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0,
                borderWidth: 0,
                cornerRadius: 0,
                shadowBlur: 0,
                shadowOffsetY: 0
            },
            boldPanoramic: {
                layout: "panoramicStrip",
                tileGutter: 3,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: 0.1,
                bgBrightness: 0.02,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0,
                borderWidth: 0,
                cornerRadius: 0,
                shadowBlur: 0,
                shadowOffsetY: 0
            },
            spotlightTop: {
                layout: "featureTop",
                tileGutter: 5,
                bgOpacity: 1,
                bgBlurPx: 0,
                bgSaturation: 0,
                bgBrightness: 0,
                bgTintColor: "",
                bgTintOpacity: 0,
                foregroundScale: 0,
                borderWidth: 0,
                cornerRadius: 0,
                shadowBlur: 0,
                shadowOffsetY: 0
            }
        };
    }

    function resize() {
        _fitCanvasToContainer();
    }

    // ── Expose public API ──
    return {
        init: init,
        setMainImage: setMainImage,
        setBgImages: setBgImages,
        setLayout: setLayout,
        updateSettings: updateSettings,
        applyPreset: applyPreset,
        exportComposite: exportComposite,
        exportNormalizedZip: exportNormalizedZip,
        exportMetadata: exportMetadata,
        dispose: dispose,
        resize: resize
    };
})();
