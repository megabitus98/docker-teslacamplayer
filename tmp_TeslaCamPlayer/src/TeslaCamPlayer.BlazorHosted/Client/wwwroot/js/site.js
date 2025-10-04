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
