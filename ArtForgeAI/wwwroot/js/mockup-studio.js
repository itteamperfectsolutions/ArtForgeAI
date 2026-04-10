window.mockupStudio = (function () {

    function initLogoDrag(containerId, dotnetRef) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const logo = container.querySelector('.ms-logo-draggable');
        if (!logo) return;

        let isDragging = false;
        let startX, startY, origLeft, origTop;

        logo.addEventListener('pointerdown', (e) => {
            isDragging = true;
            startX = e.clientX;
            startY = e.clientY;
            origLeft = logo.offsetLeft;
            origTop = logo.offsetTop;
            logo.setPointerCapture(e.pointerId);
            e.preventDefault();
        });

        logo.addEventListener('pointermove', (e) => {
            if (!isDragging) return;
            const dx = e.clientX - startX;
            const dy = e.clientY - startY;
            const newLeft = origLeft + dx;
            const newTop = origTop + dy;
            logo.style.left = newLeft + 'px';
            logo.style.top = newTop + 'px';
        });

        logo.addEventListener('pointerup', (e) => {
            if (!isDragging) return;
            isDragging = false;

            const rect = container.getBoundingClientRect();
            const normX = (logo.offsetLeft + logo.offsetWidth / 2) / rect.width;
            const normY = (logo.offsetTop + logo.offsetHeight / 2) / rect.height;

            dotnetRef.invokeMethodAsync('OnLogoPositionChanged', normX, normY);
        });

        logo.addEventListener('pointercancel', (e) => {
            isDragging = false;
        });
    }

    async function downloadAllAsZip(base64Images, names) {
        const zip = new JSZip();
        const timestamp = new Date().toISOString().slice(0, 10).replace(/-/g, '');

        for (let i = 0; i < base64Images.length; i++) {
            const bytes = Uint8Array.from(atob(base64Images[i]), c => c.charCodeAt(0));
            zip.file(`${names[i]}_mockup.png`, bytes);
        }

        const blob = await zip.generateAsync({ type: 'blob' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `mockups_${timestamp}.zip`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    return {
        initLogoDrag,
        downloadAllAsZip
    };

})();
