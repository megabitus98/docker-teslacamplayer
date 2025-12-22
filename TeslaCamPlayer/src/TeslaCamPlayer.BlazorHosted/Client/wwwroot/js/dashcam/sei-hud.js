/**
 * sei-hud.js
 *
 * HUD overlay for Tesla Dashcam SEI metadata.
 * Single card layout with high-salience primary row and supporting telemetry rows.
 */

(function () {
  const DEFAULTS = {
    useMph: true,
    maxThrottlePct: 100,
    maxSteerDeg: 540,
    zIndex: 9999
  };

  const AUTOPILOT_LABELS = {
    NONE: "AP: Off",
    TACC: "AP: TACC",
    AUTOSTEER: "AP: Autosteer",
    SELF_DRIVING: "AP: FSD"
  };

  const AUTOPILOT_COLORS = {
    NONE: "#98a0b6",
    TACC: "#7ad0ff",
    AUTOSTEER: "#8fd4ff",
    SELF_DRIVING: "#92f0c2"
  };

  const AUTOPILOT_STATE_BY_INDEX = ["NONE", "SELF_DRIVING", "AUTOSTEER", "TACC"];
  const GEAR_BY_INDEX = ["P", "D", "R", "N"];

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
      frameSeqNo: pickNumber(raw, ["frame_seq_no", "frameSeqNo"], null),
      latitude: pickNumber(raw, ["latitude_deg", "latitudeDeg"], null),
      longitude: pickNumber(raw, ["longitude_deg", "longitudeDeg"], null),
      heading,
      accelX,
      accelY,
      accelZ,
      accelMag,
      version: pickNumber(raw, ["version"], null)
    };
  }

  function formatNumber(val, digits = 0, fallback = "—") {
    if (val === null || !Number.isFinite(val)) return fallback;
    return val.toFixed(digits);
  }

  function injectStyles() {
    if (document.getElementById("sei-hud-styles")) return;

    const s = document.createElement("style");
    s.id = "sei-hud-styles";
    s.textContent = `
.sei-hud-root {
  position:absolute;
  inset:0;
  display:flex;
  justify-content:center;
  align-items:flex-start;
  pointer-events:none;
  z-index:var(--sei-hud-z);
  font-family: "SF Pro Display","Segoe UI",system-ui,-apple-system,sans-serif;
  color:#f5f7ff;
}

.sei-hud-overlay {
  position:absolute;
  inset:0;
  display:flex;
  justify-content:center;
  align-items:flex-start;
  padding:12px;
  pointer-events:none;
  z-index:100;
  transition:opacity .3s ease;
}
.sei-hud-overlay.hidden { opacity:0; }
.sei-hud-overlay.visible { opacity:1; }

.sei-hud-wrap {
  width:100%;
  display:flex;
  justify-content:center;
  padding:12px;
}

.sei-hud-card {
  width:clamp(420px, 48vw, 520px);
  background:linear-gradient(145deg, rgba(14,18,26,.86), rgba(9,11,16,.82));
  border-radius:18px;
  border:1px solid rgba(255,255,255,.14);
  box-shadow:0 18px 50px rgba(0,0,0,.48);
  backdrop-filter:blur(16px);
  padding:14px 16px 12px;
  display:flex;
  flex-direction:column;
  gap:10px;
}

.sei-row {
  display:grid;
  align-items:center;
  gap:10px;
}

.sei-row.row-primary { grid-template-columns: auto 1fr auto auto; }
.sei-row.row-signals { grid-template-columns: repeat(3, 1fr); }
.sei-row.row-driver { grid-template-columns: repeat(2, 1fr); }
.sei-row.row-location { grid-template-columns: repeat(4, 1fr); }
.sei-row.row-meta { grid-template-columns: 1fr; }

.sei-label {
  font-size:11px;
  letter-spacing:.35px;
  text-transform:uppercase;
  opacity:.78;
}

.gear-pill {
  min-width:72px;
  padding:8px 12px;
  border-radius:14px;
  text-align:center;
  background:linear-gradient(160deg, rgba(255,255,255,.08), rgba(255,255,255,.02));
  border:1px solid rgba(255,255,255,.18);
  box-shadow:inset 0 0 0 1px rgba(255,255,255,.05);
}
.gear-pill .value {
  font-size:22px;
  font-weight:800;
  letter-spacing:1px;
}

.speed-block {
  text-align:center;
}
.speed-block .speed-value {
  font-size:42px;
  font-weight:800;
  line-height:1;
  font-variant-numeric:tabular-nums;
  letter-spacing:-0.5px;
}
.speed-block .speed-unit {
  font-size:14px;
  letter-spacing:.4px;
  opacity:.85;
}

.ap-badge {
  --ap-dot:#98a0b6;
  display:flex;
  align-items:center;
  gap:8px;
  padding:9px 12px;
  border-radius:14px;
  background:rgba(255,255,255,.05);
  border:1px solid rgba(255,255,255,.12);
  min-width:150px;
  font-weight:700;
  transition:all .2s ease;
}
.ap-badge .ap-dot {
  width:12px;
  height:12px;
  border-radius:50%;
  background:var(--ap-dot);
  box-shadow:0 0 0 6px rgba(255,255,255,.08);
}
.ap-badge .ap-text { font-size:13px; letter-spacing:.2px; }
.ap-badge.active { background:rgba(115,170,255,.18); border-color:rgba(140,190,255,.55); box-shadow:0 8px 28px rgba(60,130,255,.24); }
.ap-badge.state-tacc { background:rgba(90,200,255,.16); border-color:rgba(140,210,255,.55); }
.ap-badge.state-autosteer { background:rgba(90,220,255,.16); border-color:rgba(150,230,255,.55); }
.ap-badge.state-self_driving { background:rgba(120,255,210,.16); border-color:rgba(140,255,220,.55); }

.frame-seq {
  text-align:right;
  font-family:"SFMono-Regular",Menlo,Consolas,monospace;
  color:#d5ddf3;
}
.frame-seq .value {
  font-size:14px;
  font-weight:700;
  letter-spacing:.5px;
}

.state-pill {
  display:flex;
  align-items:center;
  gap:10px;
  padding:9px 10px;
  border-radius:12px;
  border:1px solid rgba(255,255,255,.12);
  background:rgba(255,255,255,.05);
  min-height:54px;
}
.state-pill .icon {
  width:26px;
  height:26px;
  display:grid;
  place-items:center;
  border-radius:50%;
  background:rgba(255,255,255,.08);
  font-size:15px;
}
.state-pill .state-body { display:flex; flex-direction:column; gap:2px; }
.state-pill .state-value { font-weight:700; }
.state-pill.active { border-color:rgba(120,200,255,.6); box-shadow:0 6px 18px rgba(80,160,255,.25); background:rgba(90,160,255,.14); }
.state-pill.brake.active { border-color:rgba(255,130,130,.6); background:rgba(255,110,110,.16); box-shadow:0 6px 18px rgba(255,120,120,.25); }

.driver-card {
  padding:10px 12px;
  border-radius:14px;
  border:1px solid rgba(255,255,255,.12);
  background:rgba(255,255,255,.04);
  display:flex;
  flex-direction:column;
  gap:8px;
}
.driver-card .value { font-weight:700; font-size:14px; }

.throttle .bar {
  position:relative;
  height:12px;
  border-radius:999px;
  background:rgba(255,255,255,.07);
  border:1px solid rgba(255,255,255,.12);
  overflow:hidden;
}
.throttle .bar-fill {
  position:absolute;
  left:0; top:0; bottom:0;
  width:0%;
  background:linear-gradient(90deg, #6efab4, #7bd2ff);
  box-shadow:0 4px 14px rgba(110,250,180,.35);
}

.steering .steer-layout {
  display:flex;
  align-items:center;
  justify-content:space-between;
  gap:12px;
}
.steer-dial {
  position:relative;
  width:64px;
  height:64px;
  border-radius:50%;
  border:1px solid rgba(255,255,255,.18);
  background:radial-gradient(circle at 50% 50%, rgba(255,255,255,.1), rgba(255,255,255,.02));
}
.steer-needle {
  position:absolute;
  width:2px;
  height:30px;
  background:linear-gradient(#c8e6ff, #5ab4ff);
  top:6px;
  left:50%;
  transform-origin:50% 24px;
  transform:translateX(-50%) rotate(0deg);
  border-radius:3px;
  box-shadow:0 0 10px rgba(90,180,255,.5);
}
.steer-center {
  position:absolute;
  width:10px;
  height:10px;
  border-radius:50%;
  background:#cfe5ff;
  border:1px solid rgba(255,255,255,.6);
  top:50%;
  left:50%;
  transform:translate(-50%, -50%);
}

.stat-card {
  padding:8px 10px;
  border-radius:12px;
  border:1px solid rgba(255,255,255,.1);
  background:rgba(255,255,255,.04);
  display:flex;
  flex-direction:column;
  gap:4px;
}
.stat-card .value {
  font-weight:700;
  font-size:14px;
}
.stat-card .muted {
  opacity:.8;
  font-size:12px;
}

.accel-block .vector-grid {
  display:grid;
  grid-template-columns: repeat(2, minmax(0,1fr));
  gap:4px 8px;
}
.accel-block .vector-item {
  display:flex;
  align-items:baseline;
  gap:6px;
  font-family:"SFMono-Regular",Menlo,Consolas,monospace;
}
.accel-block .dim { opacity:.8; font-size:12px; }
.accel-block .unit { opacity:.7; font-size:12px; }

.meta-pill {
  display:inline-flex;
  align-items:center;
  gap:8px;
  padding:8px 12px;
  border-radius:12px;
  border:1px solid rgba(255,255,255,.12);
  background:rgba(255,255,255,.05);
  width:fit-content;
}
.meta-pill .value { font-weight:700; }
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

    // Primary row
    const rowPrimary = document.createElement("div");
    rowPrimary.className = "sei-row row-primary";

    const gear = document.createElement("div");
    gear.className = "gear-pill";
    gear.innerHTML = `<div class="sei-label">Gear</div><div class="value">—</div>`;

    const speed = document.createElement("div");
    speed.className = "speed-block";
    speed.innerHTML = `<div class="sei-label">Speed</div><div class="speed-value">0</div><div class="speed-unit">${opts.useMph ? "mph" : "km/h"}</div>`;

    const ap = document.createElement("div");
    ap.className = "ap-badge off";
    ap.innerHTML = `<span class="ap-dot"></span><div class="ap-text">${AUTOPILOT_LABELS.NONE}</div>`;

    const frame = document.createElement("div");
    frame.className = "frame-seq";
    frame.innerHTML = `<div class="sei-label">Frame</div><div class="value">—</div>`;

    rowPrimary.append(gear, speed, ap, frame);

    // Signals row
    const rowSignals = document.createElement("div");
    rowSignals.className = "sei-row row-signals";
    const stateCell = (label, icon, extraClass = "") => {
      const cell = document.createElement("div");
      cell.className = `state-pill ${extraClass}`.trim();
      cell.innerHTML = `
        <div class="icon">${icon}</div>
        <div class="state-body">
          <div class="sei-label">${label}</div>
          <div class="state-value">Off</div>
        </div>
      `;
      return { cell, valueEl: cell.querySelector(".state-value") };
    };
    const leftState = stateCell("Left blinker", "◀");
    const rightState = stateCell("Right blinker", "▶");
    const brakeState = stateCell("Brake applied", "■", "brake");
    rowSignals.append(leftState.cell, rightState.cell, brakeState.cell);

    // Driver input row
    const rowDriver = document.createElement("div");
    rowDriver.className = "sei-row row-driver";

    const throttle = document.createElement("div");
    throttle.className = "driver-card throttle";
    throttle.innerHTML = `
      <div class="sei-label">Accelerator</div>
      <div class="bar"><div class="bar-fill"></div></div>
      <div class="value">0%</div>
    `;

    const steering = document.createElement("div");
    steering.className = "driver-card steering";
    steering.innerHTML = `
      <div class="sei-label">Steering wheel</div>
      <div class="steer-layout">
        <div class="steer-readout">
          <div class="value steer-value">0°</div>
        </div>
        <div class="steer-dial">
          <span class="steer-needle"></span>
          <span class="steer-center"></span>
        </div>
      </div>
    `;

    rowDriver.append(throttle, steering);

    // Location and motion row
    const rowLocation = document.createElement("div");
    rowLocation.className = "sei-row row-location";

    const statCard = (label, extraClass = "") => {
      const cardEl = document.createElement("div");
      cardEl.className = `stat-card ${extraClass}`.trim();
      cardEl.innerHTML = `<div class="sei-label">${label}</div><div class="value">—</div>`;
      return { cardEl, valueEl: cardEl.querySelector(".value") };
    };

    const latCard = statCard("Latitude");
    const lonCard = statCard("Longitude");
    const headingCard = statCard("Heading");

    const accelCard = document.createElement("div");
    accelCard.className = "stat-card accel-block";
    accelCard.innerHTML = `
      <div class="sei-label">Linear accel</div>
      <div class="vector-grid">
        <div class="vector-item x"><span class="dim">X</span><span class="value">0.00</span><span class="unit">m/s²</span></div>
        <div class="vector-item y"><span class="dim">Y</span><span class="value">0.00</span><span class="unit">m/s²</span></div>
        <div class="vector-item z"><span class="dim">Z</span><span class="value">0.00</span><span class="unit">m/s²</span></div>
        <div class="vector-item mag"><span class="dim">|a|</span><span class="value">0.00</span><span class="unit">m/s²</span></div>
      </div>
    `;

    rowLocation.append(latCard.cardEl, lonCard.cardEl, headingCard.cardEl, accelCard);

    // Metadata row
    const rowMeta = document.createElement("div");
    rowMeta.className = "sei-row row-meta";
    const versionCard = document.createElement("div");
    versionCard.className = "meta-pill";
    versionCard.innerHTML = `<span class="sei-label">Version</span><span class="value">—</span>`;
    rowMeta.append(versionCard);

    card.append(rowPrimary, rowSignals, rowDriver, rowLocation, rowMeta);
    wrap.appendChild(card);
    root.appendChild(wrap);

    return {
      root,
      speedValue: speed.querySelector(".speed-value"),
      speedUnit: speed.querySelector(".speed-unit"),
      gearValue: gear.querySelector(".value"),
      apBadge: ap,
      apText: ap.querySelector(".ap-text"),
      frameValue: frame.querySelector(".value"),
      leftState,
      rightState,
      brakeState,
      throttleValue: throttle.querySelector(".value"),
      throttleFill: throttle.querySelector(".bar-fill"),
      steerValue: steering.querySelector(".steer-value"),
      steerNeedle: steering.querySelector(".steer-needle"),
      latValue: latCard.valueEl,
      lonValue: lonCard.valueEl,
      headingValue: headingCard.valueEl,
      accelXValue: accelCard.querySelector(".vector-item.x .value"),
      accelYValue: accelCard.querySelector(".vector-item.y .value"),
      accelZValue: accelCard.querySelector(".vector-item.z .value"),
      accelMagValue: accelCard.querySelector(".vector-item.mag .value"),
      versionValue: versionCard.querySelector(".value")
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

  function mount({ videoEl, getTelemetry }) {
    injectStyles();
    const container = videoEl || videoEl?.parentElement || document.body;
    if (getComputedStyle(container).position === "static") {
      container.style.position = "relative";
    }

    const hud = createHudDom(DEFAULTS);
    container.appendChild(hud.root);

    const stop = rafLoop(() => {
      const t = normalizeTelemetry(getTelemetry() || {}, DEFAULTS);

      hud.speedValue.textContent = Math.round(Math.max(0, t.speed || 0));
      hud.speedUnit.textContent = t.unit;
      hud.gearValue.textContent = t.gear || "—";

      const autopilotState = t.autopilotState || "NONE";
      hud.apText.textContent = AUTOPILOT_LABELS[autopilotState] ?? AUTOPILOT_LABELS.NONE;
      hud.apBadge.classList.toggle("active", autopilotState !== "NONE");
      hud.apBadge.classList.remove("state-none", "state-tacc", "state-autosteer", "state-self_driving");
      hud.apBadge.classList.add(`state-${autopilotState.toLowerCase()}`);
      hud.apBadge.style.setProperty("--ap-dot", AUTOPILOT_COLORS[autopilotState] || AUTOPILOT_COLORS.NONE);

      hud.frameValue.textContent = t.frameSeqNo != null ? t.frameSeqNo.toString() : "—";

      hud.leftState.cell.classList.toggle("active", t.left);
      hud.leftState.valueEl.textContent = t.left ? "On" : "Off";
      hud.rightState.cell.classList.toggle("active", t.right);
      hud.rightState.valueEl.textContent = t.right ? "On" : "Off";
      hud.brakeState.cell.classList.toggle("active", t.brake);
      hud.brakeState.valueEl.textContent = t.brake ? "Applied" : "Off";

      const throttlePct = clamp(t.throttlePct ?? 0, 0, DEFAULTS.maxThrottlePct);
      hud.throttleValue.textContent = `${Math.round(throttlePct)}%`;
      hud.throttleFill.style.width = `${throttlePct}%`;

      const steerLimited = clamp(t.steerDeg ?? 0, -DEFAULTS.maxSteerDeg, DEFAULTS.maxSteerDeg);
      hud.steerValue.textContent = `${steerLimited.toFixed(1)}°`;
      hud.steerNeedle.style.transform = `translateX(-50%) rotate(${steerLimited}deg)`;

      hud.latValue.textContent = t.latitude != null ? t.latitude.toFixed(5) : "—";
      hud.lonValue.textContent = t.longitude != null ? t.longitude.toFixed(5) : "—";
      hud.headingValue.textContent = t.heading != null ? `${t.heading.toFixed(0)}°` : "—";

      hud.accelXValue.textContent = formatNumber(t.accelX, 2, "—");
      hud.accelYValue.textContent = formatNumber(t.accelY, 2, "—");
      hud.accelZValue.textContent = formatNumber(t.accelZ, 2, "—");
      hud.accelMagValue.textContent = formatNumber(t.accelMag, 2, "—");

      hud.versionValue.textContent = t.version != null ? t.version.toString() : "—";
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
