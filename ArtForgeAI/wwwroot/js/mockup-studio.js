window.mockupStudio = (function () {

    function initLogoDrag(containerId, dotnetRef) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const logo = container.querySelector('.ms-logo-draggable');
        if (!logo) return;

        // Remove old listeners if re-initialized
        if (logo._msCleanup) logo._msCleanup();

        let isDragging = false;
        let startX, startY, origCenterX, origCenterY;

        function onDown(e) {
            isDragging = true;
            startX = e.clientX;
            startY = e.clientY;
            // Use getBoundingClientRect for accurate visual position (accounts for transforms)
            const logoRect = logo.getBoundingClientRect();
            const containerRect = container.getBoundingClientRect();
            origCenterX = logoRect.left + logoRect.width / 2 - containerRect.left;
            origCenterY = logoRect.top + logoRect.height / 2 - containerRect.top;
            logo.setPointerCapture(e.pointerId);
            e.preventDefault();
        }

        function onMove(e) {
            if (!isDragging) return;
            const dx = e.clientX - startX;
            const dy = e.clientY - startY;
            const containerRect = container.getBoundingClientRect();
            // Calculate new center as percentage of container
            const pctX = ((origCenterX + dx) / containerRect.width) * 100;
            const pctY = ((origCenterY + dy) / containerRect.height) * 100;
            logo.style.left = pctX + '%';
            logo.style.top = pctY + '%';
        }

        function onUp(e) {
            if (!isDragging) return;
            isDragging = false;
            // Use getBoundingClientRect for accurate normalized position
            const logoRect = logo.getBoundingClientRect();
            const containerRect = container.getBoundingClientRect();
            const normX = (logoRect.left + logoRect.width / 2 - containerRect.left) / containerRect.width;
            const normY = (logoRect.top + logoRect.height / 2 - containerRect.top) / containerRect.height;
            dotnetRef.invokeMethodAsync('OnLogoPositionChanged', normX, normY);
        }

        function onCancel() {
            isDragging = false;
        }

        logo.addEventListener('pointerdown', onDown);
        logo.addEventListener('pointermove', onMove);
        logo.addEventListener('pointerup', onUp);
        logo.addEventListener('pointercancel', onCancel);

        logo._msCleanup = () => {
            logo.removeEventListener('pointerdown', onDown);
            logo.removeEventListener('pointermove', onMove);
            logo.removeEventListener('pointerup', onUp);
            logo.removeEventListener('pointercancel', onCancel);
        };
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
