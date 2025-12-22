// ES6 module for Blazor JS interop - SEI HUD mounting and control
export function initializeSeiHud(containerElement, dotNetRef, speedUnit) {
    let currentTelemetry = null;

    // Determine useMph based on speedUnit parameter
    const useMph = speedUnit === "mph";

    // Mount the HUD using the global SeiHud from sei-hud.js
    const hudInstance = window.SeiHud.mount({
        videoEl: containerElement,
        getTelemetry: () => currentTelemetry,
        useMph: useMph
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
