// ── Emotion Transfer Module ──
// Renders all-emotions grid as a downloadable PNG.
window.emotionTransfer = (() => {

    /**
     * Render emotion results into a grid PNG (2 rows x 5 columns).
     * @param {(string|null)[]} dataUrls - Array of data URLs (null for failed)
     * @param {string[]} labels - Array of emotion labels
     * @returns {Promise<string>} Base64 PNG string (no prefix)
     */
    async function renderEmotionGrid(dataUrls, labels) {
        const cols = 5;
        const rows = Math.ceil(dataUrls.length / cols);
        const cellW = 260;
        const cellH = 260;
        const labelH = 32;
        const padding = 16;
        const gap = 10;

        const totalW = (cellW * cols) + (gap * (cols - 1)) + (padding * 2);
        const totalH = ((cellH + labelH) * rows) + (gap * (rows - 1)) + (padding * 2);

        const canvas = document.createElement('canvas');
        canvas.width = totalW;
        canvas.height = totalH;
        const ctx = canvas.getContext('2d');

        // Background
        ctx.fillStyle = '#1a1a2e';
        ctx.beginPath();
        ctx.roundRect(0, 0, totalW, totalH, 12);
        ctx.fill();

        // Load images
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
            const col = i % cols;
            const row = Math.floor(i / cols);
            const x = padding + col * (cellW + gap);
            const y = padding + row * (cellH + labelH + gap);

            if (images[i]) {
                const img = images[i];
                const scale = Math.max(cellW / img.width, cellH / img.height);
                const sw = img.width * scale;
                const sh = img.height * scale;
                const sx = x + (cellW - sw) / 2;
                const sy = y + (cellH - sh) / 2;

                ctx.save();
                ctx.beginPath();
                ctx.roundRect(x, y, cellW, cellH, 8);
                ctx.clip();
                ctx.drawImage(img, sx, sy, sw, sh);
                ctx.restore();
            } else {
                ctx.fillStyle = '#2a2a3e';
                ctx.beginPath();
                ctx.roundRect(x, y, cellW, cellH, 8);
                ctx.fill();
                ctx.fillStyle = '#666';
                ctx.font = '14px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText('Failed', x + cellW / 2, y + cellH / 2);
            }

            // Label
            ctx.fillStyle = '#e0e0e0';
            ctx.font = 'bold 13px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillText(labels[i] || '', x + cellW / 2, y + cellH + labelH - 8);
        }

        return canvas.toDataURL('image/png').split(',')[1];
    }

    /**
     * Download all emotion images as a ZIP file using JSZip.
     * @param {string[]} base64Images - Array of base64 PNG strings (no prefix)
     * @param {string[]} labels - Array of emotion labels for filenames
     */
    async function downloadAllAsZip(base64Images, labels) {
        const zip = new JSZip();
        const timestamp = new Date().toISOString().slice(0, 10).replace(/-/g, '');

        for (let i = 0; i < base64Images.length; i++) {
            const b64 = base64Images[i];
            if (!b64) continue;
            const fileName = `emotion_${labels[i].replace(/\s+/g, '_')}_${timestamp}.png`;
            zip.file(fileName, b64, { base64: true });
        }

        const blob = await zip.generateAsync({ type: 'blob' });
        const link = document.createElement('a');
        link.href = URL.createObjectURL(blob);
        link.download = `emotions_all_${timestamp}.zip`;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(link.href);
    }

    return {
        renderEmotionGrid,
        downloadAllAsZip
    };

})();
