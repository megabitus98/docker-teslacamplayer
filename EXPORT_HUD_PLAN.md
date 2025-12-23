# Plan: Replicate Web Telemetry UI in Export HUD Overlay

## Current State Analysis

### Web UI (SeiHud.razor + sei-hud.js)
**Layout**: Horizontal pill-shaped bar with 5 elements:
1. **Left blinker** (◀) - Animated when active
2. **Left cluster**: Gear chip (P/D/R/N) + Brake pedal (vertical fill)
3. **Center cluster**: Large speed value + unit label (mph/km/h)
4. **Right cluster**: Steering wheel (rotates) + Throttle pedal (vertical fill)
5. **Right blinker** (▶) - Animated when active

**Styling**:
- Pill-shaped container: `border-radius: 999px`, blur backdrop filter
- Semi-transparent background: `rgba(0,0,0,.28)` with `backdrop-filter: blur(14px)`
- Responsive sizing: `clamp()` for fonts/spacing, adapts to viewport
- Autopilot indicators: Blue tint on steering wheel when active
- Smooth animations: Steering wheel rotation, pedal fills, blinker pulse

**Data Displayed**:
- Speed (primary), unit (mph/km/h)
- Steering angle (visual rotation -540° to +540°)
- Gear state (P/D/R/N)
- Throttle % (0-100%, green fill)
- Brake state (boolean, red fill)
- Blinkers (boolean, green pulse)
- Autopilot state (NONE/TACC/AUTOSTEER/SELF_DRIVING)

### Current Export HUD (hud-render-template.html)
**Status**: Uses **exact same sei-hud.js code** embedded in template
**Issue**: Plain text overlay instead of graphical UI (contradicts user's description)

**Wait, let me verify this...**

Looking at the code:
- `hud-render-template.html` DOES contain the full sei-hud.js implementation
- The template creates a transparent HTML page with the HUD
- Puppeteer screenshots this to PNG/RGBA frames

**Actual Current State**: The export HUD **already renders the graphical UI**!

## Problem Clarification Needed

The user states "SEI data is output as plain text" but the code shows:
1. `hud-render-template.html` contains the full sei-hud.js implementation
2. Puppeteer renders this as a visual overlay
3. The layout/styling should match the web UI

**Possible Interpretations**:
1. ✅ **Most Likely**: The current HUD is missing styling differences or uses outdated sei-hud.js
2. The export is broken and not showing any HUD at all
3. User wants ADDITIONAL telemetry data (GPS, acceleration, etc.) not currently shown
4. User wants a different layout/position for export vs web

## Investigation Required

Before proceeding with implementation, verify:

```bash
# Compare web UI sei-hud.js vs export template
diff \
  TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/wwwroot/js/dashcam/sei-hud.js \
  <(docker exec teslacamplayer cat /app/teslacamplayer/lib/hud-render-template.html | sed -n '/<script>/,/<\/script>/p')
```

**If they differ**: Sync the template with latest sei-hud.js
**If they match**: Determine what "plain text" means (screenshot needed)

---

## Implementation Plan (Assuming Out-of-Sync Code)

### Phase 1: Sync Export Template with Web UI (1-2 hours)

**Goal**: Ensure hud-render-template.html uses identical sei-hud.js code as web UI

#### 1.1 Extract Web UI Code
```bash
# Copy current sei-hud.js styling and logic
cp TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/wwwroot/js/dashcam/sei-hud.js \
   /tmp/sei-hud-reference.js
```

#### 1.2 Update Template
**File**: Create new `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/lib/hud-render-template.html`

**Structure**:
```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=1920, initial-scale=1.0">
  <title>SEI HUD Export Renderer</title>
  <style>
    /* Reset and container setup */
    * { margin: 0; padding: 0; box-sizing: border-box; }
    html, body { width: 100%; height: 100%; background: transparent; overflow: hidden; }
    #hud-container { width: 100%; height: 100%; position: relative; }

    /* CRITICAL: Replace backdrop-filter with solid background for rendering */
    .sei-hud-bar {
      backdrop-filter: none !important;
      background: rgba(0,0,0,0.6) !important; /* Opaque for export */
    }
  </style>
</head>
<body>
  <div id="hud-container"></div>

  <script>
    // PASTE FULL sei-hud.js HERE (lines 8-583 from sei-hud.js)
    // ...

    // Export-specific initialization
    let hudInstance = null;
    let currentTelemetry = null;

    window.setSpeedUnit = function(useMph) {
      // Reinitialize HUD with new unit
      if (hudInstance) hudInstance.unmount();
      hudInstance = window.SeiHud.mount({
        videoEl: null,
        getTelemetry: () => currentTelemetry,
        useMph: useMph
      });
    };

    window.updateTelemetry = function(data) {
      currentTelemetry = data;
    };

    window.setFastRender = function(enabled) {
      // No RAF wait in fast mode (already implemented in hud-renderer.js)
    };

    // Initialize HUD on load
    window.addEventListener('DOMContentLoaded', () => {
      hudInstance = window.SeiHud.mount({
        videoEl: document.getElementById('hud-container'),
        getTelemetry: () => currentTelemetry,
        useMph: false // Default, overridden by setSpeedUnit
      });
    });
  </script>
</body>
</html>
```

#### 1.3 Build System Integration
**Dockerfile.aarch64** (and Dockerfile):
```dockerfile
# Ensure template is copied during build
COPY TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/lib/ /app/teslacamplayer/lib/
```

**Current Status**: ✅ Already in place (line 36 of Dockerfile.aarch64)

#### 1.4 Verification
1. Build Docker image: `docker compose build`
2. Start export with HUD overlay
3. Verify exported video shows graphical HUD matching web UI

**Expected Differences (Export vs Web)**:
- No backdrop-filter blur (replaced with solid background for rendering)
- Slightly higher opacity for readability in exports
- Frozen animations (no RAF loop during screenshot)

---

### Phase 2: Enhanced Telemetry Display (If Requested)

**If user wants additional data beyond current HUD:**

#### 2.1 Extended Data Fields
Add secondary telemetry row below main HUD:

**New Elements**:
- GPS coordinates (lat/lon with 4 decimal places)
- Heading (compass direction, 0-360°)
- Acceleration (X/Y/Z m/s², magnitude)
- Timestamp (formatted from SEI data)

**Layout**:
```
┌─────────────────────────────────────────────┐
│  ◀  [P] [Brake]  125 km/h  [Wheel] [Gas]  ▶ │  ← Existing HUD
└─────────────────────────────────────────────┘
┌─────────────────────────────────────────────┐
│  📍 44.4668, 26.0807  │  🧭 182°  │  ⚡ 0.8g  │  ← New row
└─────────────────────────────────────────────┘
```

#### 2.2 Implementation
**File**: `sei-hud.js` (both web and export)

Add below main bar:
```javascript
// In createHudDom()
const secondaryRow = document.createElement('div');
secondaryRow.className = 'sei-hud-secondary';
secondaryRow.innerHTML = `
  <div class="telem-chip">
    <span class="telem-icon">📍</span>
    <span class="telem-value coords">—</span>
  </div>
  <div class="telem-chip">
    <span class="telem-icon">🧭</span>
    <span class="telem-value heading">—</span>
  </div>
  <div class="telem-chip">
    <span class="telem-icon">⚡</span>
    <span class="telem-value accel">—</span>
  </div>
`;
root.appendChild(secondaryRow);

// In rafLoop()
if (t.latitude !== null && t.longitude !== null) {
  hud.coords.textContent = `${t.latitude.toFixed(4)}, ${t.longitude.toFixed(4)}`;
}
if (t.heading !== null) {
  hud.heading.textContent = `${Math.round(t.heading)}°`;
}
if (t.accelMag !== null) {
  hud.accel.textContent = `${(t.accelMag / 9.81).toFixed(1)}g`;
}
```

**CSS**:
```css
.sei-hud-secondary {
  display: flex;
  gap: 8px;
  margin-top: 8px;
  justify-content: center;
}

.telem-chip {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  background: rgba(0,0,0,.4);
  border: 1px solid rgba(255,255,255,.12);
  border-radius: 12px;
  font-size: 13px;
}

.telem-icon {
  opacity: 0.7;
}

.telem-value {
  font-variant-numeric: tabular-nums;
  font-weight: 500;
}
```

---

### Phase 3: Configuration Options

#### 3.1 Export Settings UI
**File**: `ExportSettingsDialog.razor`

Add checkboxes:
```razor
<MudCheckBox @bind-Checked="ShowExtendedTelemetry" Label="Show Extended Telemetry" />
<MudCheckBox @bind-Checked="ShowHudOverlay" Label="Show HUD Overlay" />
<MudSelect @bind-Value="HudPosition" Label="HUD Position">
  <MudSelectItem Value="@("top")">Top</MudSelectItem>
  <MudSelectItem Value="@("bottom")">Bottom</MudSelectItem>
</MudSelect>
```

#### 3.2 Backend Configuration
**File**: `HudRenderConfig.cs`

```csharp
public class HudRenderConfig
{
    // ... existing properties ...
    public bool ShowExtendedTelemetry { get; set; } = false;
    public string HudPosition { get; set; } = "top"; // "top" or "bottom"
}
```

**File**: `HudRendererService.cs`

```csharp
// Pass to Node.js
sb.Append($"--extended-telemetry {config.ShowExtendedTelemetry.ToString().ToLower()} ");
sb.Append($"--hud-position {config.HudPosition} ");
```

**File**: `hud-renderer.js`

```javascript
// Parse new flags
case '--extended-telemetry':
  config.showExtendedTelemetry = args[++i].toLowerCase() === 'true';
  break;
case '--hud-position':
  config.hudPosition = args[++i];
  break;

// Pass to template via page.evaluate
await page.evaluate((extendedTelem, position) => {
  window.setExtendedTelemetry(extendedTelem);
  window.setHudPosition(position);
}, config.showExtendedTelemetry, config.hudPosition);
```

---

## File Change Summary

### Files to Modify:
1. ✅ `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/lib/hud-render-template.html`
   - Sync with latest sei-hud.js
   - Replace backdrop-filter with solid background

2. `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/wwwroot/js/dashcam/sei-hud.js` (if Phase 2)
   - Add extended telemetry row
   - Add position configuration

3. `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/Models/HudRenderConfig.cs` (if Phase 3)
   - Add new configuration properties

4. `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/Services/HudRendererService.cs` (if Phase 3)
   - Pass new flags to Node.js

5. `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server/lib/hud-renderer.js` (if Phase 3)
   - Parse and forward new configuration

6. `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client/Pages/ExportSettingsDialog.razor` (if Phase 3)
   - Add UI controls

### Testing Strategy:
1. **Visual Comparison**: Export 10-second clip, compare HUD frame-by-frame with web UI screenshot
2. **Data Accuracy**: Verify all SEI fields render correctly (speed, gear, pedals, steering, autopilot)
3. **Edge Cases**: Test with missing data (no GPS, no autopilot, missing pedal data)
4. **Performance**: Ensure rendering speed remains 2-5 FPS with graphical HUD

---

## Critical Questions for User

Before implementing, please clarify:

1. **What does "plain text" mean?**
   - Is the current export showing NO HUD at all?
   - Is it showing a basic text overlay (different from code)?
   - Screenshot of current export would help

2. **What additional data is needed?**
   - Current HUD shows: speed, gear, pedals, steering, blinkers, autopilot
   - Web UI has same data - what's missing?
   - Do you want GPS, acceleration, heading visible?

3. **Layout preferences?**
   - Keep current pill design?
   - Add secondary telemetry row below?
   - Different position (currently top-center)?

4. **Styling requirements?**
   - Exactly match web UI (including blur)?
   - Or export-optimized (solid background, higher contrast)?

**Recommended Next Step**: User provides screenshot of current export HUD to confirm actual vs expected state.
