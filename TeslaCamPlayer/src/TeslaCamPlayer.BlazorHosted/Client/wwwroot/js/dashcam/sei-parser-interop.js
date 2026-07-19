// ES6 module for Blazor JS interop - SEI telemetry lookup.
// Telemetry is parsed server-side (/Api/SeiData/...) so the browser no longer
// re-downloads the MP4; this module just fetches the JSON and indexes it by time.
const parsedCache = new Map();
const DEFAULT_FRAME_DURATION_MS = 33.333; // ~30fps fallback

function findFrameIndexForMs(startsMs, targetMs) {
    if (!startsMs || !startsMs.length) {
        return null;
    }

    if (targetMs <= startsMs[0]) {
        return 0;
    }

    let low = 0;
    let high = startsMs.length - 1;
    while (low <= high) {
        const mid = (low + high) >>> 1;
        const midStart = startsMs[mid];
        const nextStart = mid + 1 < startsMs.length ? startsMs[mid + 1] : Number.POSITIVE_INFINITY;

        if (targetMs < midStart) {
            high = mid - 1;
            continue;
        }

        if (targetMs >= nextStart) {
            low = mid + 1;
            continue;
        }

        return mid;
    }

    return startsMs.length - 1;
}

export async function parseVideoSeiFromUrl(videoUrl) {
    if (parsedCache.has(videoUrl)) {
        return videoUrl;
    }

    const seiUrl = videoUrl.replace("/Api/Video/", "/Api/SeiData/");
    const response = await fetch(seiUrl);
    if (!response.ok) {
        throw new Error(`Failed to fetch SEI telemetry (${response.status})`);
    }

    const data = await response.json();
    if (!data || !Array.isArray(data.frames) || data.frames.length === 0) {
        return null;
    }

    parsedCache.set(videoUrl, data);
    return videoUrl;
}

export function getSeiForTime(handle, timeSeconds) {
    const parsed = parsedCache.get(handle);
    if (!parsed || !parsed.frames.length) {
        return null;
    }

    const targetMs = timeSeconds * 1000;
    let frameIndex = findFrameIndexForMs(parsed.frameStartsMs, targetMs);
    if (frameIndex === null) {
        frameIndex = Math.floor(targetMs / DEFAULT_FRAME_DURATION_MS);
    }

    // frameStartsMs can be longer than frames when trailing frames carry no SEI — clamp.
    frameIndex = Math.min(Math.max(frameIndex, 0), parsed.frames.length - 1);
    return parsed.frames[frameIndex];
}
