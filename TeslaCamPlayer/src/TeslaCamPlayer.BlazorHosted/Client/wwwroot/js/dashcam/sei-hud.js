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
<circle cx="12" cy="12" r="8" stroke="currentColor" stroke-width="1.4"/>
<path d="M6.8 9.8 H17.2" stroke="currentColor" stroke-width="2" stroke-linecap="round"/>
<path d="M12 9.8 V16.8" stroke="currentColor" stroke-width="2" stroke-linecap="round"/>
<circle cx="12" cy="12" r="1.8" stroke="currentColor" stroke-width="1.4"/>
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
  top:0;
  left:0;
  pointer-events:none;
  z-index:var(--sei-hud-z);
  font-family: system-ui,-apple-system,Segoe UI;
  color:rgba(255,255,255,.95);
  display:flex;
  justify-content:center;
  align-items:flex-start;
  padding: clamp(8px, 2vw, 12px);
  box-sizing:border-box;
  width: 100%;
}

.sei-hud-root.sei-hud-hidden {
  display:none;
}

.sei-hud-bar {
  display:flex;
  align-items:center;
  gap: 6px;
  padding: 7px clamp(12px, 1.8vw, 14px);  /* was 6px clamp(10px...) */
  min-height: 52px;
  height: clamp(54px, 6.4vw, 58px);       /* optional: +2px */
  width: fit-content;                 /* hug content */
  max-width: min(84vw, 600px);        /* still responsive */
  min-width: 0;                       /* remove forced width */
  backdrop-filter: blur(14px);
  background: rgba(0,0,0,.28);
  border: 1px solid rgba(255,255,255,.12);
  box-shadow: 0 8px 20px rgba(0,0,0,.22);
  border-radius: 999px;
  pointer-events: none;
}

.sei-hud-cluster {
  display:flex;
  align-items:center;
  gap: 7px;
}

/* prevent double-box on blinkers */
.sei-hud-chip.blinker-chip{
  border: none;          /* removes the extra square border */
  background: transparent;
  padding: 0;            /* keeps geometry predictable */
}

.sei-hud-cluster.left,
.sei-hud-cluster.right {
  flex: 0 0 auto;
  flex-wrap: wrap;
  justify-content: center;
  min-width: clamp(72px, 16vw, 96px);
}

.sei-hud-cluster.center {
  flex: 0 0 auto;          /* key */
  flex-direction: column;
  align-items: center;
  gap: 2px;
}

.sei-hud-inner {
  flex: 0 0 auto;          /* key */
  display: flex;
  justify-content: center;
}

.sei-hud-inner-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: auto;
}

.sei-hud-chip {
  min-width: 44px;
  min-height: 44px;
  display:grid;
  place-items:center;
  border-radius: 16px;
  padding: 6px 10px;
  background: rgba(255,255,255,.04);
  border: 1px solid rgba(255,255,255,.08);
  color: inherit;
  box-sizing: border-box;
}

.gear-chip {
  font-size: 13px;
  font-weight: 800;
  background: rgba(0,0,0,.32);
  border-color: rgba(255,255,255,.12);
}

.blinker-chip {
  width: 44px;
  height: 44px;
  position: relative;
  display: grid;
  place-items: center;
  border-radius: 14px;
  opacity: .55;
  background: transparent;
}

.blinker-chip.active {
  opacity: 1;
}

.blinker-chip::before {
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  content:"";
  position:absolute;
  width: 52px;
  height: 28px;
  border-radius: 14px;
  background: rgba(255,255,255,.06);
  border: 1px solid rgba(255,255,255,.16);
}

.blinker-chip.active::before {
  background: rgba(120,255,120,.16);
  border-color: rgba(120,255,120,.32);
  animation: blinker-pulse 1s ease-in-out infinite;
}

@keyframes blinker-pulse {
  0%, 100% {
    background: rgba(120,255,120,.16);
    border-color: rgba(120,255,120,.36);
  }
  50% {
    background: rgba(120,255,120,.04);
    border-color: rgba(120,255,120,.18);
  }
}

.wheel-chip {
  width: 44px;
  height: 44px;
  padding: 0;
}

.wheel-chip svg {
  width: 28px;
  height: 28px;
  transform: rotate(var(--sei-wheel-rot, 0deg));
}

.pedal-chip {
  position: relative;
  width: 44px;
  height: 44px;
  padding: 0;
  border-radius: 16px;
  overflow: hidden;
  border: 1px solid rgba(255,255,255,.14);
  background: rgba(255,255,255,.05);
}

.pedal-chip::before {
  content: "";
  position: absolute;
  inset: 6px;
  border-radius: 12px;
  background: rgba(0,0,0,.42);
}

.pedal-chip .fill {
  position:absolute;
  inset:6px;
  overflow:hidden;
  border-radius:12px;
}

.pedal-chip .fill i {
  position:absolute;
  bottom:0;
  left:0;
  right:0;
  height:var(--fill);
  background:var(--c);
}

.pedal-chip.throttle { --c:rgba(120,255,120,.7); }
.pedal-chip.brake { --c:rgba(255,90,90,.75); }

.pedal-chip svg {
  position:absolute;
  z-index:2;
  stroke:rgba(200,200,200,.88);
  fill:none;
}

.speed-val {
  font-size: clamp(26px, 3.2vw, 32px);
  font-weight: 800;
  font-variant-numeric: tabular-nums;
  font-feature-settings: "tnum";
  line-height: 1;
}

.unit-row {
  display:flex;
  align-items:center;
  justify-content:center;
  gap: 2px;
}

.unit-label {
  font-size: 12px;
  opacity: .78;
}

.wheel-chip {
  width: 44px;
  height: 44px;
  padding: 0;
  transition: background .2s ease, border-color .2s ease, color .2s ease;
}

.wheel-chip svg {
  width: 28px;
  height: 28px;
  transform: rotate(var(--sei-wheel-rot, 0deg));
   /* keep stroke tied to computed color */
  stroke: currentColor;
  transition: stroke .2s ease;
}

.wheel-chip.cruise {
  background: rgba(90,160,255,.12);
  border-color: rgba(90,160,255,.25);
}

.wheel-chip.autopilot {
  color: #40C4FF;
}

.wheel-chip.autopilot svg {
  stroke: rgb(130,190,255);
}
`;
    document.head.appendChild(s);
  }

  function createHudDom(opts) {
    const root = document.createElement("div");
    root.className = "sei-hud-root";
    root.style.setProperty("--sei-hud-z", opts.zIndex);

    const bar = document.createElement("div");
    bar.className = "sei-hud-bar";

    // Blinkers as edge chips
    const leftSig = document.createElement("div");
    leftSig.className = "sei-hud-chip blinker-chip left";
    leftSig.textContent = "◀";

    const rightSig = document.createElement("div");
    rightSig.className = "sei-hud-chip blinker-chip right";
    rightSig.textContent = "▶";

    // Left cluster: gear + brake pedal
    const leftCluster = document.createElement("div");
    leftCluster.className = "sei-hud-cluster left";

    const gear = document.createElement("div");
    gear.className = "sei-hud-chip gear-chip";
    gear.textContent = "—";

    const brake = document.createElement("div");
    brake.className = "sei-hud-chip pedal-chip brake";
    brake.innerHTML = `
      <span class="fill"><i></i></span>
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor">${BRAKE_PEDAL_SVG}</svg>
    `;

    leftCluster.append(gear, brake);

    // Center cluster: speed, unit + autopilot chip
    const centerCluster = document.createElement("div");
    centerCluster.className = "sei-hud-cluster center";

    const speedVal = document.createElement("div");
    speedVal.className = "speed-val";
    speedVal.textContent = "0";

    const unitRow = document.createElement("div");
    unitRow.className = "unit-row";

    const unitLabel = document.createElement("span");
    unitLabel.className = "unit-label";
    unitLabel.textContent = opts.useMph ? "mph" : "km/h";

    unitRow.append(unitLabel);
    centerCluster.append(speedVal, unitRow);

    // Right cluster: steering + throttle pedal
    const rightCluster = document.createElement("div");
    rightCluster.className = "sei-hud-cluster right";

    const wheel = document.createElement("div");
    wheel.className = "sei-hud-chip wheel-chip";
    wheel.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" class="steer-wheel-icon">${STEERING_WHEEL_SVG}</svg>`;

    const throttle = document.createElement("div");
    throttle.className = "sei-hud-chip pedal-chip throttle";
    throttle.innerHTML = `
      <span class="fill"><i></i></span>
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor">${THROTTLE_PEDAL_SVG}</svg>
    `;

    rightCluster.append(wheel, throttle);

    const inner = document.createElement("div");
    inner.className = "sei-hud-inner";

    const innerRow = document.createElement("div");
    innerRow.className = "sei-hud-inner-row";
    innerRow.append(leftCluster, centerCluster, rightCluster);
    inner.appendChild(innerRow);

    bar.append(leftSig, inner, rightSig);
    root.appendChild(bar);

    return {
      root,
      speedVal,
      unitLabel,
      leftSig,
      rightSig,
      wheel,
      wheelSvg: wheel.querySelector(".steer-wheel-icon"),
      throttlePedal: throttle,
      brakePedal: brake,
      gear
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

      // Autopilot/Cruise indicator on steering wheel
      const autopilotState = (t.autopilotState || "NONE").toUpperCase();
      const isCruise = autopilotState === "TACC";
      const isAutopilot = autopilotState === "AUTOSTEER" || autopilotState === "SELF_DRIVING" || autopilotState === "FSD" || autopilotState === "AUTOPILOT";
      hud.wheel.classList.toggle("cruise", isCruise);
      hud.wheel.classList.toggle("autopilot", isAutopilot);
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
