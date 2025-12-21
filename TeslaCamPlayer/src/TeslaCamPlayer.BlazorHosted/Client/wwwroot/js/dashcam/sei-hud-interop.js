// ES6 module for Blazor JS interop - SEI HUD mounting and control
export function initializeSeiHud(containerElement, dotNetRef) {
    let currentTelemetry = null;

    // Mount the HUD using the global SeiHud from sei-hud.js
    const hudInstance = window.SeiHud.mount({
        videoEl: containerElement,
        getTelemetry: () => currentTelemetry
    });

    return {
        updateTelemetry: (seiData) => {
            currentTelemetry = seiData;
        },
        unmount: () => {
            if (hudInstance) {
                hudInstance.unmount();
            }
        }
    };
}
