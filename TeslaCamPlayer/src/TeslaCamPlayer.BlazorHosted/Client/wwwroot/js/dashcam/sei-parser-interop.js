// ES6 module for Blazor JS interop - SEI Parser
let SeiMetadata = null;
let enumFields = null;
const DEFAULT_PROTO_URL = new URL("./dashcam.proto", import.meta.url).href;
const parsedCache = new Map();
const PROTO_TEXT = `
syntax = "proto3";

// SEI (Supplemental Enhancement Information) metadata embedded in Tesla dashcam video streams.
message SeiMetadata {
  uint32 version = 1;

  enum Gear {
    GEAR_PARK = 0;
    GEAR_DRIVE = 1;
    GEAR_REVERSE = 2;
    GEAR_NEUTRAL = 3;
  }
  Gear gear_state = 2;

  uint64 frame_seq_no = 3;
  float vehicle_speed_mps = 4;
  float accelerator_pedal_position = 5;
  float steering_wheel_angle = 6;
  bool blinker_on_left = 7;
  bool blinker_on_right = 8;
  bool brake_applied = 9;

  enum AutopilotState {
    NONE = 0;
    SELF_DRIVING = 1;
    AUTOSTEER = 2;
    TACC = 3;
  }
  AutopilotState autopilot_state = 10;
  double latitude_deg = 11;
  double longitude_deg = 12;
  double heading_deg = 13;
  double linear_acceleration_mps2_x = 14;
  double linear_acceleration_mps2_y = 15;
  double linear_acceleration_mps2_z = 16;
}
`;

async function ensureInitialized(protoPath = null) {
    if (SeiMetadata) return;
    await initializeProtobuf(protoPath);
}

export async function initializeProtobuf(protoPath) {
    if (SeiMetadata) return;

    let protoText = PROTO_TEXT;
    if (protoPath) {
        try {
            const response = await fetch(protoPath);
            if (response.ok) {
                protoText = await response.text();
            }
        } catch {
            // fallback to embedded text
        }
    }

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
