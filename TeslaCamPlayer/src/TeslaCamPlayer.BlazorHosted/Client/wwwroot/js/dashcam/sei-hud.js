/**
 * sei-hud.js
 *
 * HUD overlay for Tesla Dashcam SEI metadata.
 * Single card layout with high-salience primary row and supporting telemetry rows.
 */

(function () {
  const DEFAULTS = {
    useMph: false,
    maxThrottlePct: 100,
    maxSteerDeg: 540,
    zIndex: 9999
  };

  const AUTOPILOT_STATE_BY_INDEX = ["NONE", "SELF_DRIVING", "AUTOSTEER", "TACC"];
  const GEAR_BY_INDEX = ["P", "D", "R", "N"];

  const BRAKE_PEDAL_SVG = `
<path d="M6 7 L18 7 L20 16 Q12 19 4 16 Z" stroke-width="2" stroke-linejoin="round"/>
<line x1="8" y1="9" x2="8" y2="14" stroke-width="1.5"/>
<line x1="10" y1="9" x2="10" y2="14" stroke-width="1.5"/>
<line x1="12" y1="9" x2="12" y2="14" stroke-width="1.5"/>
<line x1="14" y1="9" x2="14" y2="14" stroke-width="1.5"/>
<line x1="16" y1="9" x2="16" y2="14" stroke-width="1.5"/>
`;

  const THROTTLE_PEDAL_SVG = `
<path d="M9 4 L15 4 L16 18 Q12 20 8 18 Z" stroke-width="2" stroke-linejoin="round"/>
<rect x="9" y="2" width="6" height="2" rx="1" stroke-width="2"/>
`;

  const STEERING_WHEEL_SVG = `
<circle cx="12" cy="12" r="8" stroke="white" stroke-width="1.4"/>
<path d="M6.8 9.8 H17.2" stroke="white" stroke-width="2" stroke-linecap="round"/>
<path d="M12 9.8 V16.8" stroke="white" stroke-width="2" stroke-linecap="round"/>
<circle cx="12" cy="12" r="1.8" stroke="white" stroke-width="1.4"/>
`;

  const clamp = (n, min, max) =>
    typeof n !== "number" || Number.isNaN(n) ? min : Math.max(min, Math.min(max, n));

  const toNumber = (v, fallback = null) => {
    if (typeof v === "number" && Number.isFinite(v)) return v;
    if (typeof v === "string" && v.trim() !== "" && !Number.isNaN(Number(v))) return Number(v);
    return fallback;
  };

  function pickNumber(obj, keys, fallback = null) {
    for (const k of keys) {
      const v = toNumber(obj?.[k]);
      if (v !== null) return v;
    }
    return fallback;
  }

  function pickBool(obj, keys, fallback = false) {
    for (const k of keys) {
      const v = obj?.[k];
      if (typeof v === "boolean") return v;
      if (typeof v === "number") return v !== 0;
      if (typeof v === "string") {
        const s = v.toLowerCase();
        if (["true", "1", "yes", "on"].includes(s)) return true;
        if (["false", "0", "no", "off"].includes(s)) return false;
      }
    }
    return fallback;
  }

  function normalizeTelemetry(raw, opts) {
    const speedMps = pickNumber(raw, ["vehicleSpeedMps", "vehicle_speed_mps", "speed_mps"], 0);
    const speedMph = speedMps * 2.23694;

    let throttleRaw = pickNumber(raw, ["acceleratorPedalPosition", "accelerator_pedal_position"], 0);
    if (throttleRaw <= 1.2) throttleRaw *= 100; // Some payloads report 0-1 range

    const gearRaw = raw?.gear_state ?? raw?.gearState;
    let gear = null;
    if (typeof gearRaw === "string") {
      const g = gearRaw.toUpperCase();
      gear = { GEAR_DRIVE: "D", GEAR_NEUTRAL: "N", GEAR_PARK: "P", GEAR_REVERSE: "R" }[g] ?? g;
    } else if (typeof gearRaw === "number") {
      gear = GEAR_BY_INDEX[gearRaw] ?? null;
    }

    const apRaw = raw?.autopilot_state ?? raw?.autopilotState;
    let autopilotState = null;
    if (typeof apRaw === "string") {
      autopilotState = apRaw.toUpperCase();
    } else if (typeof apRaw === "number") {
      autopilotState = AUTOPILOT_STATE_BY_INDEX[apRaw] ?? null;
    }
    if (!autopilotState) autopilotState = "NONE";

    const headingRaw = pickNumber(raw, ["heading_deg_norm", "headingDegNorm", "heading_deg", "headingDeg"], null);
    const heading = headingRaw === null ? null : ((headingRaw % 360) + 360) % 360;

    const accelX = pickNumber(raw, ["linear_acceleration_mps2_x", "linearAccelerationMps2X"], null);
    const accelY = pickNumber(raw, ["linear_acceleration_mps2_y", "linearAccelerationMps2Y"], null);
    const accelZ = pickNumber(raw, ["linear_acceleration_mps2_z", "linearAccelerationMps2Z"], null);
    const accelMag =
      accelX !== null && accelY !== null && accelZ !== null
        ? Math.sqrt(accelX * accelX + accelY * accelY + accelZ * accelZ)
        : null;

    return {
      speed: opts.useMph ? speedMph : speedMph * 1.60934,
      unit: opts.useMph ? "mph" : "km/h",
      steerDeg: pickNumber(raw, ["steering_wheel_angle", "steeringWheelAngle"], 0),
      left: pickBool(raw, ["blinker_on_left", "blinkerOnLeft"]),
      right: pickBool(raw, ["blinker_on_right", "blinkerOnRight"]),
      brake: pickBool(raw, ["brake_applied", "brakeApplied"]),
      throttlePct: clamp(throttleRaw, 0, opts.maxThrottlePct),
      autopilotState,
      gear: gear ?? "—",
      latitude: pickNumber(raw, ["latitude_deg", "latitudeDeg"], null),
      longitude: pickNumber(raw, ["longitude_deg", "longitudeDeg"], null),
      heading,
      accelX,
      accelY,
      accelZ,
      accelMag
    };
  }

  function injectStyles() {
    if (document.getElementById("sei-hud-styles")) return;

    const s = document.createElement("style");
    s.id = "sei-hud-styles";
    s.textContent = `
.sei-hud-root {
  position:absolute;
  inset:0;
  pointer-events:none;
  z-index:var(--sei-hud-z);
  font-family: system-ui,-apple-system,Segoe UI;
  color:rgba(255,255,255,.95);
}

.sei-hud-root.sei-hud-hidden {
  display:none;
}

.sei-hud-wrap {
  display:flex;
  justify-content:center;
  padding:12px;
}

.sei-hud-card {
  padding: 6px 10px 8px;
  border-radius: 18px;
  backdrop-filter: blur(14px);
  background: rgba(10,10,12,.38);
  border: 1px solid rgba(255,255,255,.14);
  box-shadow: 0 10px 30px rgba(0,0,0,.25);
}

.sei-hud-grid {
  display:grid;
  grid-template-columns: auto 1fr 32px 3ch 32px 1fr auto;
  grid-template-rows: 1fr 1fr;
  grid-template-areas:
    "gear  . left  speed right . wheel"
    "brake . .     .     .     . throttle";
  column-gap:8px;
  align-items:center;
}

.sei-hud-speed {
  display:flex;
  flex-direction:column;
  align-items:center;
  line-height:1;
  transform: translateY(16px);
}
.sei-hud-speed .val {
  font-size:28px;
  font-weight:800;
  font-variant-numeric: tabular-nums;
  font-feature-settings: "tnum";
}
.sei-hud-speed .unit {
  font-size:12px;
  opacity:.8;
  margin-top:4px;
}

.sei-hud-signal {
  width:32px;
  height:24px;
  display:grid;
  place-items:center;
  border-radius:10px;
  opacity:.4;
  border:1px solid rgba(255,255,255,.15);
  transform: translateY(16px);
}
.sei-hud-signal.active {
  animation: blinker-pulse 1s ease-in-out infinite;
}

@keyframes blinker-pulse {
  0%, 100% {
    opacity:1;
    background:rgba(120,255,120,.18);
    border-color:rgba(120,255,120,.45);
  }
  50% {
    opacity:.3;
    background:rgba(120,255,120,.05);
    border-color:rgba(120,255,120,.2);
  }
}

.sei-hud-wheel {
  width:32px;
  height:32px;
  border-radius:50%;
  display:grid;
  place-items:center;
  border:1px solid rgba(255,255,255,.18);
}
.sei-hud-wheel svg {
  width:26px;
  height:26px;
  transform:rotate(var(--sei-wheel-rot,0deg));
}

.sei-hud-pedal {
  width:32px;
  height:32px;
  border-radius:50%;
  position:relative;
  border:2px solid rgba(255,255,255,.25);
  overflow:hidden;
}
.sei-hud-pedal::before {
  content:"";
  position:absolute;
  inset:4px;
  border-radius:50%;
  background:rgba(0,0,0,.4);
}
.sei-hud-pedal .fill {
  position:absolute;
  inset:4px;
  overflow:hidden;
  border-radius:50%;
}
.sei-hud-pedal .fill i {
  position:absolute;
  bottom:0;
  left:0;
  right:0;
  height:var(--fill);
  background:var(--c);
}
.sei-hud-pedal.throttle { --c:rgba(120,255,120,.7); }
.sei-hud-pedal.brake { --c:rgba(255,90,90,.75); }
.sei-hud-pedal svg {
  position:absolute;
  z-index:2;
  stroke:rgba(136,136,136,.9);
  fill:none;
}

.sei-hud-gear {
  font-size:12px;
  font-weight:800;
  padding:6px 12px;
  border-radius:999px;
  border:1px solid rgba(255,255,255,.14);
}

.sei-hud-ap-status {
  margin-top:6px;
  text-align:center;
  font-size:13px;
  font-weight:600;
  letter-spacing:.3px;
  color:rgb(90,160,255);
  opacity:0;
  max-height:0;
  transform:translateY(-4px);
  transition:
    opacity .25s ease,
    transform .25s ease,
    max-height .25s ease;
}
.sei-hud-ap-status.active {
  opacity:1;
  max-height:24px;
  transform:translateY(0);
}
`;
    document.head.appendChild(s);
  }

  function createHudDom(opts) {
    const root = document.createElement("div");
    root.className = "sei-hud-root";
    root.style.setProperty("--sei-hud-z", opts.zIndex);

    const wrap = document.createElement("div");
    wrap.className = "sei-hud-wrap";

    const card = document.createElement("div");
    card.className = "sei-hud-card";

    // Grid container
    const grid = document.createElement("div");
    grid.className = "sei-hud-grid";

    // Gear indicator
    const gear = document.createElement("div");
    gear.className = "sei-hud-gear";
    gear.style.gridArea = "gear";
    gear.textContent = "—";

    // Brake pedal (circular with fill)
    const brake = document.createElement("div");
    brake.className = "sei-hud-pedal brake";
    brake.style.gridArea = "brake";
    brake.innerHTML = `
      <span class="fill"><i></i></span>
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor">${BRAKE_PEDAL_SVG}</svg>
    `;

    // Left blinker
    const leftSig = document.createElement("div");
    leftSig.className = "sei-hud-signal left";
    leftSig.style.gridArea = "left";
    leftSig.textContent = "◀";

    // Speed display
    const speedBox = document.createElement("div");
    speedBox.className = "sei-hud-speed";
    speedBox.style.gridArea = "speed";
    speedBox.innerHTML = `
      <div class="val">0</div>
      <div class="unit">${opts.useMph ? "mph" : "km/h"}</div>
    `;

    // Right blinker
    const rightSig = document.createElement("div");
    rightSig.className = "sei-hud-signal right";
    rightSig.style.gridArea = "right";
    rightSig.textContent = "▶";

    // Steering wheel
    const wheel = document.createElement("div");
    wheel.className = "sei-hud-wheel";
    wheel.style.gridArea = "wheel";
    wheel.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" class="steer-wheel-icon">${STEERING_WHEEL_SVG}</svg>`;

    // Throttle pedal (circular with fill)
    const throttle = document.createElement("div");
    throttle.className = "sei-hud-pedal throttle";
    throttle.style.gridArea = "throttle";
    throttle.innerHTML = `
      <span class="fill"><i></i></span>
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor">${THROTTLE_PEDAL_SVG}</svg>
    `;

    // Autopilot status (below grid)
    const apStatus = document.createElement("div");
    apStatus.className = "sei-hud-ap-status";

    grid.append(gear, brake, leftSig, speedBox, rightSig, wheel, throttle);
    card.append(grid, apStatus);
    wrap.appendChild(card);
    root.appendChild(wrap);

    return {
      root,
      speedVal: speedBox.querySelector(".val"),
      leftSig,
      rightSig,
      wheel,
      wheelSvg: wheel.querySelector(".steer-wheel-icon"),
      throttlePedal: throttle,
      brakePedal: brake,
      gear,
      apStatus
    };
  }

  function rafLoop(fn) {
    let stop = false;
    (function tick() {
      if (stop) return;
      fn();
      requestAnimationFrame(tick);
    })();
    return () => (stop = true);
  }

  function mount({ videoEl, getTelemetry, useMph }) {
    injectStyles();
    const container = videoEl || videoEl?.parentElement || document.body;
    if (getComputedStyle(container).position === "static") {
      container.style.position = "relative";
    }

    // Merge user-provided options with defaults
    const opts = {
      ...DEFAULTS,
      useMph: useMph !== undefined ? useMph : DEFAULTS.useMph
    };

    const hud = createHudDom(opts);
    hud.root.classList.add("sei-hud-hidden");
    container.appendChild(hud.root);

    let hasTelemetry = false;
    let steerTargetDeg = 0;
    let steerDisplayDeg = 0;
    let steerInitialized = false;
    let lastFrameTs = performance.now();
    let lastTargetChangeTs = performance.now();

    const stop = rafLoop(() => {
      const now = performance.now();
      const dt = now - lastFrameTs;
      lastFrameTs = now;

      const telemetry = typeof getTelemetry === "function" ? getTelemetry() : null;
      const hasData = telemetry != null;
      if (hasTelemetry !== hasData) {
        hud.root.classList.toggle("sei-hud-hidden", !hasData);
        hasTelemetry = hasData;
      }
      if (!hasData) {
        steerInitialized = false;
      }

      const t = normalizeTelemetry(telemetry || {}, opts);

      // Speed
      hud.speedVal.textContent = Math.round(Math.max(0, t.speed || 0));

      // Blinkers - simple active state toggle
      hud.leftSig.classList.toggle("active", t.left);
      hud.rightSig.classList.toggle("active", t.right);

      // Steering wheel rotation
      const steerLimited = clamp(t.steerDeg ?? 0, -DEFAULTS.maxSteerDeg, DEFAULTS.maxSteerDeg);
      if (!steerInitialized) {
        steerTargetDeg = steerLimited;
        steerDisplayDeg = steerLimited;
        steerInitialized = true;
        lastTargetChangeTs = now;
      } else {
        if (steerLimited !== steerTargetDeg) {
          steerTargetDeg = steerLimited;
          lastTargetChangeTs = now;
        }
        const smoothing = 1 - Math.exp(-Math.max(0, dt) / 110); // easing factor tuned for quick catch-up
        steerDisplayDeg += (steerTargetDeg - steerDisplayDeg) * smoothing;
        if (now - lastTargetChangeTs > 500) {
          steerDisplayDeg = steerTargetDeg; // snap if we're still behind after half a second
        }
      }
      hud.wheelSvg.style.transform = `rotate(${steerDisplayDeg}deg)`;

      // Pedal fills - CSS variables for height
      const throttlePct = clamp(t.throttlePct ?? 0, 0, DEFAULTS.maxThrottlePct);
      hud.throttlePedal.style.setProperty("--fill", `${throttlePct}%`);
      hud.brakePedal.style.setProperty("--fill", t.brake ? "100%" : "0%");

      // Gear
      hud.gear.textContent = t.gear || "—";

      // Autopilot status with simplified labels
      const autopilotState = t.autopilotState || "NONE";
      const isAP = autopilotState !== "NONE" && autopilotState !== "OFF";
      hud.apStatus.classList.toggle("active", isAP);
      if (isAP) {
        const apLabels = {
          "TACC": "Traffic-Aware Cruise",
          "AUTOSTEER": "Autopilot",
          "SELF_DRIVING": "Full Self-Driving",
          "FSD": "Full Self-Driving"
        };
        hud.apStatus.textContent = apLabels[autopilotState] || "Autopilot";
      }
    });

    return {
      unmount() {
        stop();
        hud.root.remove();
      }
    };
  }

  window.SeiHud = { mount };
})();
