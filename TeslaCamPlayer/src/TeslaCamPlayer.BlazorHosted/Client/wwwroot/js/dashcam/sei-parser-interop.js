// ES6 module for Blazor JS interop - SEI Parser
let SeiMetadata = null;
let enumFields = null;
const DEFAULT_PROTO_URL = new URL("./dashcam.proto", import.meta.url).href;
const parsedCache = new Map();
const DEFAULT_FRAME_DURATION_MS = 33.333; // ~30fps fallback

function buildFrameTimeline(frames, config) {
    const frameCount = frames?.length ?? 0;
    const durations = Array.isArray(config?.durations) ? config.durations : [];
    const fallbackDuration = durations[0] || DEFAULT_FRAME_DURATION_MS;

    const startsMs = new Array(frameCount);
    let acc = 0;

    for (let i = 0; i < frameCount; i++) {
        startsMs[i] = acc;
        const duration = durations.length
            ? durations[Math.min(i, durations.length - 1)] || fallbackDuration
            : fallbackDuration;
        acc += duration;
    }

    return {
        startsMs,
        totalMs: acc,
        defaultDurationMs: fallbackDuration
    };
}

function findFrameIndexForMs(timeline, targetMs) {
    const starts = timeline?.startsMs;
    if (!starts || !starts.length) {
        return null;
    }

    if (targetMs <= starts[0]) {
        return 0;
    }

    let low = 0;
    let high = starts.length - 1;
    while (low <= high) {
        const mid = (low + high) >>> 1;
        const midStart = starts[mid];
        const nextStart = mid + 1 < starts.length ? starts[mid + 1] : Number.POSITIVE_INFINITY;

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

    return starts.length - 1;
}

async function ensureInitialized(protoPath = null) {
    if (SeiMetadata) return;
    await initializeProtobuf(protoPath);
}

export async function initializeProtobuf(protoPath) {
    if (SeiMetadata) return;

    const protoUrl = protoPath || DEFAULT_PROTO_URL;
    let response;
    try {
        response = await fetch(protoUrl);
    } catch (error) {
        console.error(`Failed to fetch SEI proto schema from ${protoUrl}`, error);
        throw error;
    }
    if (!response.ok) {
        const message = `Failed to fetch SEI proto schema from ${protoUrl} (HTTP ${response.status})`;
        console.error(message);
        throw new Error(message);
    }
    const protoText = await response.text();

    const proto = (globalThis.protobuf || window?.protobuf);
    if (!proto) {
        throw new Error("protobufjs not loaded");
    }
    const root = proto.parse(protoText).root;

    SeiMetadata = root.lookupType('SeiMetadata');
    enumFields = {
        gearState: SeiMetadata.lookup('Gear'),
        autopilotState: SeiMetadata.lookup('AutopilotState'),
        gear_state: SeiMetadata.lookup('Gear'),
        autopilot_state: SeiMetadata.lookup('AutopilotState')
    };
}

export async function parseVideoSei(arrayBuffer) {
    await ensureInitialized();

    const mp4 = new window.DashcamMP4(arrayBuffer);
    const frames = mp4.parseFrames(SeiMetadata);
    const config = mp4.getConfig();
    const timeline = buildFrameTimeline(frames, config);

    // Check if any frames have SEI data
    const hasValidSei = frames.some(f => f.sei != null);
    if (!hasValidSei) {
        return null;
    }

    return {
        frames: frames,
        config,
        timeline
    };
}

export async function parseVideoSeiFromUrl(videoUrl) {
    await ensureInitialized();

    if (parsedCache.has(videoUrl)) {
        return videoUrl;
    }

    const response = await fetch(videoUrl);
    if (!response.ok) {
        throw new Error(`Failed to fetch video for SEI parsing (${response.status})`);
    }

    const buffer = await response.arrayBuffer();
    const parsed = await parseVideoSei(buffer);
    if (!parsed) return null;
    parsedCache.set(videoUrl, parsed);
    return videoUrl;
}

export function getSeiForTime(handle, timeSeconds) {
    const parsedData = parsedCache.get(handle);
    if (!parsedData || !parsedData.frames) {
        return null;
    }

    const targetMs = timeSeconds * 1000;
    const timeline = parsedData.timeline;

    let frameIndex = findFrameIndexForMs(timeline, targetMs);
    if (frameIndex === null || frameIndex === undefined) {
        const fallbackDuration = timeline?.defaultDurationMs
            ?? parsedData.config?.durations?.[0]
            ?? DEFAULT_FRAME_DURATION_MS;
        frameIndex = Math.floor(targetMs / fallbackDuration);
    }

    if (frameIndex >= 0 && frameIndex < parsedData.frames.length) {
        return parsedData.frames[frameIndex].sei;
    }

    return null;
}
