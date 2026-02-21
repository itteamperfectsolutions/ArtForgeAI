// ===== ArtForge AI - JavaScript Interop =====

// Theme Management
window.themeManager = {
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        localStorage.setItem('artforge-theme', theme);
    },
    getTheme: function () {
        return localStorage.getItem('artforge-theme') || 'dark';
    },
    initTheme: function () {
        const theme = this.getTheme();
        document.documentElement.setAttribute('data-theme', theme);
        return theme;
    }
};

// File Download
window.downloadFile = async function (url, fileName) {
    try {
        const response = await fetch(url);
        if (!response.ok) throw new Error('Download failed: HTTP ' + response.status);
        const blob = await response.blob();
        const link = document.createElement('a');
        link.href = URL.createObjectURL(blob);
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(link.href);
    } catch (err) {
        console.error('Download failed:', err);
    }
};

// Download from byte array
window.downloadFileFromBytes = function (fileName, contentType, base64Data) {
    try {
        const byteCharacters = atob(base64Data);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
            byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const blob = new Blob([byteArray], { type: contentType });
        const link = document.createElement('a');
        link.href = URL.createObjectURL(blob);
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(link.href);
    } catch (err) {
        console.error('Download failed:', err);
    }
};

// Image Zoom
window.toggleImageZoom = function (imgElement) {
    imgElement.classList.toggle('zoomed');
};

// Sidebar toggle for mobile
window.toggleSidebar = function () {
    const sidebar = document.querySelector('.sidebar');
    if (sidebar) {
        sidebar.classList.toggle('open');
    }
};

// Click outside to close sidebar on mobile
document.addEventListener('click', function (e) {
    const sidebar = document.querySelector('.sidebar');
    const menuBtn = document.querySelector('.mobile-menu-btn');
    if (sidebar && sidebar.classList.contains('open') &&
        !sidebar.contains(e.target) && (!menuBtn || !menuBtn.contains(e.target))) {
        sidebar.classList.remove('open');
    }
});

// Initialize theme on page load
document.addEventListener('DOMContentLoaded', function () {
    window.themeManager.initTheme();
});
