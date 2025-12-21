// ES6 module for Blazor JS interop - SEI Parser
let SeiMetadata = null;
let enumFields = null;

export async function initializeProtobuf(protoPath) {
    if (SeiMetadata) return;

    const response = await fetch(protoPath);
    const protoText = await response.text();
    const root = protobuf.parse(protoText).root;

    SeiMetadata = root.lookupType('SeiMetadata');
    enumFields = {
        gearState: SeiMetadata.lookup('Gear'),
        autopilotState: SeiMetadata.lookup('AutopilotState'),
        gear_state: SeiMetadata.lookup('Gear'),
        autopilot_state: SeiMetadata.lookup('AutopilotState')
    };
}

export async function parseVideoSei(arrayBuffer) {
    if (!SeiMetadata) {
        throw new Error('Protobuf not initialized - call initializeProtobuf first');
    }

    const mp4 = new window.DashcamMP4(arrayBuffer);
    const frames = mp4.parseFrames(SeiMetadata);

    // Check if any frames have SEI data
    const hasValidSei = frames.some(f => f.sei != null);
    if (!hasValidSei) {
        return null;
    }

    return {
        frames: frames,
        config: mp4.getConfig()
    };
}

export function getSeiForTime(parsedData, timeSeconds) {
    if (!parsedData || !parsedData.frames) {
        return null;
    }

    const config = parsedData.config;
    // Calculate frame index based on frame durations
    const frameDurationMs = config.durations && config.durations[0]
        ? config.durations[0]
        : 33.333; // Default to 30fps

    const frameIndex = Math.floor((timeSeconds * 1000) / frameDurationMs);

    if (frameIndex >= 0 && frameIndex < parsedData.frames.length) {
        return parsedData.frames[frameIndex].sei;
    }

    return null;
}
