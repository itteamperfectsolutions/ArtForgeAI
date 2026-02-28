// ===== ArtForge AI - Image Viewer / Print & Cut Module =====
window.imageViewer = (function () {
    "use strict";

    let canvas = null;
    let outerWrapper = null;
    let resizeObserver = null;
    let baseImage = null;
    let outlineImage = null;
    let bboxRect = null;
    let marginRect = null;
    let detectionData = null;
    let croppedImage = null;
    let savedDetectionState = null;

    function init(canvasId) {
        if (canvas) { dispose(); }

        var el = document.getElementById(canvasId);
        if (!el) return;

        outerWrapper = el.closest(".iv-canvas-wrapper");

        canvas = new fabric.Canvas(canvasId, {
            selection: false,
            renderOnAddRemove: false,
            backgroundColor: "transparent",
        });

        if (outerWrapper) {
            resizeObserver = new ResizeObserver(function () {
                fitCanvas();
            });
            resizeObserver.observe(outerWrapper);
        }
    }

    function fitCanvas() {
        if (!canvas || !baseImage || !outerWrapper) return;
        fitCanvasForImage(baseImage);
        canvas.renderAll();
    }

    function fitCanvasForImage(img) {
        if (!canvas || !img || !outerWrapper) return;
        // Use the right-panel parent for available space (not the wrapper itself)
        var container = outerWrapper.parentElement || outerWrapper;
        var ww = container.clientWidth - 2;
        var wh = container.clientHeight || (window.innerHeight - container.getBoundingClientRect().top - 16);
        if (ww < 50 || wh < 50) return;
        var scaleW = ww / img.width;
        var scaleH = wh / img.height;
        var scale = Math.min(scaleW, scaleH);
        canvas.setDimensions({
            width: Math.round(img.width * scale),
            height: Math.round(img.height * scale)
        });
        canvas.setZoom(scale);
    }

    function loadImage(dataUrl) {
        if (!canvas) return Promise.resolve();

        clearAll();

        return fabric.FabricImage.fromURL(dataUrl).then(function (img) {
            baseImage = img;
            baseImage.set({
                left: 0, top: 0,
                selectable: false, evented: false,
                objectCaching: false,
            });
            canvas.add(baseImage);
            canvas.sendObjectToBack(baseImage);

            canvas.setDimensions({ width: img.width, height: img.height });
            canvas.setZoom(1);
            fitCanvas();
            canvas.renderAll();
        });
    }

    // ── Fullscreen ──

    function enterFullscreen() {
        if (!outerWrapper) return;
        if (outerWrapper.requestFullscreen) outerWrapper.requestFullscreen();
        else if (outerWrapper.webkitRequestFullscreen) outerWrapper.webkitRequestFullscreen();
        setTimeout(fitCanvas, 300);
        document.addEventListener("fullscreenchange", onFullscreenChange);
    }

    function exitFullscreen() {
        if (document.exitFullscreen) document.exitFullscreen();
        else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
    }

    function isFullscreen() { return !!document.fullscreenElement; }

    function onFullscreenChange() {
        setTimeout(fitCanvas, 300);
        if (!document.fullscreenElement) {
            document.removeEventListener("fullscreenchange", onFullscreenChange);
        }
    }

    // ── Detection Overlay ──

    function showDetection(outlineUrl, bboxX, bboxY, bboxW, bboxH) {
        if (!canvas) return;

        clearDetection();

        detectionData = { bboxX: bboxX, bboxY: bboxY, bboxW: bboxW, bboxH: bboxH };

        // Cache-buster for server file paths
        var url = outlineUrl;
        if (url && url.charAt(0) === "/") {
            url = url + "?t=" + Date.now();
        }

        fabric.FabricImage.fromURL(url).then(function (img) {
            outlineImage = img;
            outlineImage.set({
                left: 0, top: 0,
                selectable: false, evented: false,
                opacity: 0.85, objectCaching: false,
            });
            canvas.add(outlineImage);
            canvas.renderAll();
        });

        bboxRect = new fabric.Rect({
            left: bboxX, top: bboxY,
            width: bboxW, height: bboxH,
            fill: "transparent",
            stroke: "#00c8ff", strokeWidth: 2,
            strokeUniform: true,
            selectable: false, evented: false,
        });
        canvas.add(bboxRect);

        updateMarginRect(0);
        canvas.renderAll();
    }

    function updateMargin(marginPx) {
        if (!detectionData || !canvas) return null;
        return updateMarginRect(marginPx);
    }

    function updateMarginRect(marginPx) {
        if (!detectionData) return null;

        if (marginRect) {
            canvas.remove(marginRect);
            marginRect = null;
        }

        var imgW = baseImage ? baseImage.width : 0;
        var imgH = baseImage ? baseImage.height : 0;

        var mx = Math.max(0, detectionData.bboxX - marginPx);
        var my = Math.max(0, detectionData.bboxY - marginPx);
        var mx2 = Math.min(imgW, detectionData.bboxX + detectionData.bboxW + marginPx);
        var my2 = Math.min(imgH, detectionData.bboxY + detectionData.bboxH + marginPx);

        marginRect = new fabric.Rect({
            left: mx, top: my,
            width: mx2 - mx, height: my2 - my,
            fill: "transparent",
            stroke: "#00c8ff", strokeWidth: 2,
            strokeDashArray: [8, 4],
            strokeUniform: true,
            selectable: false, evented: false,
        });
        canvas.add(marginRect);
        canvas.renderAll();

        return { x: mx, y: my, width: mx2 - mx, height: my2 - my };
    }

    function clearDetection() {
        if (outlineImage) { canvas.remove(outlineImage); outlineImage = null; }
        if (bboxRect) { canvas.remove(bboxRect); bboxRect = null; }
        if (marginRect) { canvas.remove(marginRect); marginRect = null; }
        detectionData = null;
        if (canvas) canvas.renderAll();
    }

    function clearAll() {
        clearDetection();
        if (croppedImage) { canvas.remove(croppedImage); croppedImage = null; }
        if (baseImage) { canvas.remove(baseImage); baseImage = null; }
        savedDetectionState = null;
        if (canvas) canvas.renderAll();
    }

    // ── Cropped / Generated Result (show in same canvas) ──

    function showCroppedResult(dataUrl) {
        if (!canvas) return;

        // Stash detection objects (hide, don't remove)
        savedDetectionState = [];
        var objectsToHide = [baseImage, outlineImage, bboxRect, marginRect];
        for (var i = 0; i < objectsToHide.length; i++) {
            if (objectsToHide[i]) {
                objectsToHide[i].set({ visible: false });
                savedDetectionState.push(objectsToHide[i]);
            }
        }

        // Remove previous result image
        if (croppedImage) {
            canvas.remove(croppedImage);
            croppedImage = null;
        }

        fabric.FabricImage.fromURL(dataUrl).then(function (img) {
            croppedImage = img;
            croppedImage.set({
                selectable: false, evented: false,
                objectCaching: false,
            });

            // Compute available space from parent container
            var container = outerWrapper.parentElement || outerWrapper;
            var availW = container.clientWidth || (window.innerWidth - 560);
            var availH = container.clientHeight || (window.innerHeight - 100);
            if (availW < 100) availW = window.innerWidth - 560;
            if (availH < 100) availH = window.innerHeight - 100;

            var scaleW = availW / img.width;
            var scaleH = availH / img.height;
            var scale = Math.min(scaleW, scaleH, 4); // cap at 4x to avoid blurry upscale

            canvas.setZoom(1);
            canvas.setDimensions({
                width: Math.round(img.width * scale),
                height: Math.round(img.height * scale)
            });
            canvas.setZoom(scale);
            canvas.add(croppedImage);
            // Checkerboard pattern so white/transparent images are visible
            canvas.backgroundColor = "#f0f0f0";
            canvas.renderAll();
        });
    }

    function restoreDetectionView() {
        if (!canvas) return;

        if (croppedImage) {
            canvas.remove(croppedImage);
            croppedImage = null;
        }

        if (savedDetectionState) {
            for (var i = 0; i < savedDetectionState.length; i++) {
                savedDetectionState[i].set({ visible: true });
            }
            savedDetectionState = null;
        }

        canvas.backgroundColor = "transparent";

        if (baseImage) {
            canvas.setZoom(1);
            canvas.setDimensions({ width: baseImage.width, height: baseImage.height });
            fitCanvas();
        }
        canvas.renderAll();
    }

    // ── Utilities ──

    function triggerDownload(dataUrl, filename) {
        var a = document.createElement("a");
        a.href = dataUrl;
        a.download = filename || "image.png";
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    }

    function dispose() {
        if (resizeObserver) { resizeObserver.disconnect(); resizeObserver = null; }
        document.removeEventListener("fullscreenchange", onFullscreenChange);
        if (canvas) { canvas.dispose(); canvas = null; }
        outerWrapper = null;
        baseImage = null;
        outlineImage = null;
        bboxRect = null;
        marginRect = null;
        detectionData = null;
        croppedImage = null;
        savedDetectionState = null;
    }

    return {
        init: init,
        loadImage: loadImage,
        showDetection: showDetection,
        updateMargin: updateMargin,
        clearDetection: clearDetection,
        clearAll: clearAll,
        showCroppedResult: showCroppedResult,
        restoreDetectionView: restoreDetectionView,
        enterFullscreen: enterFullscreen,
        exitFullscreen: exitFullscreen,
        isFullscreen: isFullscreen,
        triggerDownload: triggerDownload,
        dispose: dispose,
    };
})();
