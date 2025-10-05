function getProperty(object, property) {
    return object[property];
}

function setProperty(object, property, value) {
    object[property] = value;
}

// Fullscreen ESC key handling for grid tiles
let __escHandler = null;
function registerEscHandler(dotNetRef) {
    unregisterEscHandler();
    __escHandler = function (e) {
        if (e && (e.key === 'Escape' || e.key === 'Esc')) {
            try { dotNetRef.invokeMethodAsync('ExitFullscreenFromJs'); } catch { }
        }
    };
    document.addEventListener('keydown', __escHandler, { passive: true });
}

function unregisterEscHandler() {
    if (__escHandler) {
        document.removeEventListener('keydown', __escHandler);
        __escHandler = null;
    }
}

function prefersReducedMotion() {
    return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

window.clipViewer = window.clipViewer || {};

window.clipViewer.captureTileRect = function (tileEl) {
    if (!tileEl) {
        return null;
    }

    const rect = tileEl.getBoundingClientRect();
    return [rect.left, rect.top, rect.width, rect.height];
};

function playFlipAnimation(tileEl, fromRect, toRect, options) {
    if (!tileEl || !fromRect || !toRect) {
        return false;
    }

    if (!toRect.width || !toRect.height) {
        return false;
    }

    const removePendingClass = !!(options && options.removePendingClass);

    const translateX = fromRect.left - toRect.left;
    const translateY = fromRect.top - toRect.top;
    const scaleX = fromRect.width / toRect.width;
    const scaleY = fromRect.height / toRect.height;

    tileEl.style.transformOrigin = 'top left';
    tileEl.style.willChange = 'transform';
    tileEl.style.transition = 'none';
    tileEl.style.transform = `translate(${translateX}px, ${translateY}px) scale(${scaleX}, ${scaleY})`;
    tileEl.style.zIndex = '20';
    tileEl.style.pointerEvents = 'none';

    if (removePendingClass && tileEl.classList.contains('is-transition-pending')) {
        tileEl.classList.remove('is-transition-pending');
        tileEl.style.opacity = '1';
        tileEl.style.visibility = 'visible';
    }

    // Force layout so the initial transform is committed before animating.
    tileEl.getBoundingClientRect();

    return new Promise(resolve => {
        const finish = () => {
            tileEl.style.transition = '';
            tileEl.style.transform = '';
            tileEl.style.willChange = '';
            tileEl.style.zIndex = '';
            tileEl.style.pointerEvents = '';
            tileEl.style.opacity = '';
            tileEl.style.visibility = '';
            tileEl.classList.remove('is-transition-pending');
            tileEl.removeEventListener('transitionend', onEnd);
            resolve(true);
        };

        const onEnd = (event) => {
            if (!event || event.propertyName === 'transform') {
                finish();
            }
        };

        requestAnimationFrame(() => {
            tileEl.style.transition = 'transform 300ms cubic-bezier(0.22, 1, 0.36, 1)';
            tileEl.style.transform = 'translate(0px, 0px) scale(1, 1)';
        });

        tileEl.addEventListener('transitionend', onEnd, { once: true });

        setTimeout(finish, 400);
    });
}

window.clipViewer.animateFullscreenEnter = async function (gridEl, tileEl, startRectArray) {
    if (!gridEl || !tileEl || prefersReducedMotion()) {
        return false;
    }

    const alreadyFullscreen = tileEl.classList.contains('is-fullscreen');
    if (!alreadyFullscreen) {
        return false;
    }

    let startRect;

    if (Array.isArray(startRectArray) && startRectArray.length === 4) {
        startRect = {
            left: startRectArray[0],
            top: startRectArray[1],
            width: startRectArray[2],
            height: startRectArray[3]
        };
    } else {
        startRect = tileEl.getBoundingClientRect();
    }

    const endRect = tileEl.getBoundingClientRect();

    return await playFlipAnimation(tileEl, startRect, endRect, { removePendingClass: true });
};

window.clipViewer.animateFullscreenExit = async function (gridEl, tileEl) {
    if (!gridEl || !tileEl || prefersReducedMotion()) {
        return false;
    }

    const isFullscreen = tileEl.classList.contains('is-fullscreen');
    if (!isFullscreen) {
        return false;
    }

    const startRect = tileEl.getBoundingClientRect();

    tileEl.classList.remove('is-fullscreen');
    gridEl.classList.remove('fullscreen-active');

    const endRect = tileEl.getBoundingClientRect();

    return await playFlipAnimation(tileEl, startRect, endRect, { removePendingClass: true });
};
