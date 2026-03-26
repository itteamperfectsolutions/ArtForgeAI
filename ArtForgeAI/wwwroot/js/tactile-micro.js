/**
 * Tactile Maximalism — Micro-Interactions (2026 Trend)
 * Subtle bounce, grow, and color-shift animations for interactive elements.
 * All CSS-driven via class toggles for performance.
 */
(function () {
    'use strict';

    // Respect reduced-motion preference
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    // ── Squishy press effect on all 3D buttons ──
    document.addEventListener('pointerdown', function (e) {
        var btn = e.target.closest(
            '.btn-primary-gradient, .btn-secondary, .btn-icon, .btn-danger, ' +
            '.btn-regenerate, .btn-retry-ai, .btn-enhance, .btn-color-enhance, ' +
            '.btn-transparent-download, .google-sign-in-btn, .size-option, ' +
            '.provider-option, .style-category-tab, .theme-toggle, .nav-item'
        );
        if (!btn || btn.disabled) return;

        btn.style.transition = 'transform 80ms cubic-bezier(0.4, 0, 0.2, 1)';

        function onRelease() {
            btn.style.transition = '';
            document.removeEventListener('pointerup', onRelease);
            document.removeEventListener('pointercancel', onRelease);
        }
        document.addEventListener('pointerup', onRelease);
        document.addEventListener('pointercancel', onRelease);
    });

    // ── Upload area: bounce the icon on dragover ──
    document.addEventListener('dragenter', function (e) {
        var area = e.target.closest('.upload-area');
        if (!area) return;
        area.style.transform = 'scale(1.02)';
        area.style.borderColor = 'var(--accent-primary)';
    });

    document.addEventListener('dragleave', function (e) {
        var area = e.target.closest('.upload-area');
        if (!area) return;
        // Only reset if we actually left the area
        if (area.contains(e.relatedTarget)) return;
        area.style.transform = '';
        area.style.borderColor = '';
    });

    document.addEventListener('drop', function (e) {
        var area = e.target.closest('.upload-area');
        if (!area) return;
        area.style.transform = '';
        area.style.borderColor = '';

        // Quick "squish" feedback on drop
        area.style.transition = 'transform 100ms ease';
        area.style.transform = 'scale(0.97)';
        setTimeout(function () {
            area.style.transition = 'transform 400ms cubic-bezier(0.34, 1.56, 0.64, 1)';
            area.style.transform = 'scale(1)';
        }, 100);
    });

    // ── Staggered card entrance animation ──
    var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
            if (entry.isIntersecting) {
                var el = entry.target;
                var delay = (el.dataset.tmIndex || 0) * 60;
                el.style.animationDelay = delay + 'ms';
                el.classList.add('tm-visible');
                observer.unobserve(el);
            }
        });
    }, { threshold: 0.1 });

    // Observe image cards and pricing cards for staggered entrance
    function observeCards() {
        var cards = document.querySelectorAll('.image-card, .pricing-card');
        cards.forEach(function (card, i) {
            if (!card.classList.contains('tm-observed')) {
                card.classList.add('tm-observed');
                card.dataset.tmIndex = i % 12; // Reset stagger after 12
                observer.observe(card);
            }
        });
    }

    // Run on load and on Blazor page updates
    observeCards();

    // Re-observe after Blazor renders new content
    var mutObs = new MutationObserver(function () {
        observeCards();
    });
    mutObs.observe(document.body, { childList: true, subtree: true });
})();
