// ── Face Aging Module ──
// Handles timeline strip rendering for the Face Aging tool.
window.faceAging = (() => {

    /**
     * Render all timeline stage images into a single horizontal strip PNG.
     * @param {(string|null)[]} dataUrls - Array of data URLs (null for failed stages)
     * @param {string[]} labels - Array of stage labels
     * @returns {Promise<string>} Base64 PNG string (no prefix)
     */
    async function renderTimelineStrip(dataUrls, labels) {
        const stageW = 300;
        const stageH = 300;
        const labelH = 36;
        const padding = 12;
        const gap = 8;

        const validCount = dataUrls.filter(u => u).length;
        if (validCount === 0) return null;

        const totalW = (stageW * dataUrls.length) + (gap * (dataUrls.length - 1)) + (padding * 2);
        const totalH = stageH + labelH + (padding * 2);

        const canvas = document.createElement('canvas');
        canvas.width = totalW;
        canvas.height = totalH;
        const ctx = canvas.getContext('2d');

        // Background
        ctx.fillStyle = '#1a1a2e';
        ctx.roundRect(0, 0, totalW, totalH, 12);
        ctx.fill();

        // Load all images
        const images = await Promise.all(dataUrls.map(url => {
            if (!url) return Promise.resolve(null);
            return new Promise((resolve) => {
                const img = new Image();
                img.onload = () => resolve(img);
                img.onerror = () => resolve(null);
                img.src = url;
            });
        }));

        for (let i = 0; i < dataUrls.length; i++) {
            const x = padding + i * (stageW + gap);
            const y = padding;

            if (images[i]) {
                // Draw image scaled to fit the stage area (cover)
                const img = images[i];
                const scale = Math.max(stageW / img.width, stageH / img.height);
                const sw = img.width * scale;
                const sh = img.height * scale;
                const sx = x + (stageW - sw) / 2;
                const sy = y + (stageH - sh) / 2;

                ctx.save();
                ctx.beginPath();
                ctx.roundRect(x, y, stageW, stageH, 8);
                ctx.clip();
                ctx.drawImage(img, sx, sy, sw, sh);
                ctx.restore();
            } else {
                // Placeholder for failed stage
                ctx.fillStyle = '#2a2a3e';
                ctx.beginPath();
                ctx.roundRect(x, y, stageW, stageH, 8);
                ctx.fill();
                ctx.fillStyle = '#666';
                ctx.font = '14px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText('Failed', x + stageW / 2, y + stageH / 2);
            }

            // Label
            ctx.fillStyle = '#e0e0e0';
            ctx.font = 'bold 14px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(labels[i] || '', x + stageW / 2, y + stageH + labelH - 10);
        }

        // Arrow connectors between stages
        ctx.fillStyle = '#6366f1';
        for (let i = 0; i < dataUrls.length - 1; i++) {
            const ax = padding + (i + 1) * (stageW + gap) - gap / 2;
            const ay = padding + stageH / 2;
            // Small arrow
            ctx.beginPath();
            ctx.moveTo(ax - 6, ay - 5);
            ctx.lineTo(ax + 2, ay);
            ctx.lineTo(ax - 6, ay + 5);
            ctx.fill();
        }

        return canvas.toDataURL('image/png').split(',')[1];
    }

    return {
        renderTimelineStrip
    };

})();
