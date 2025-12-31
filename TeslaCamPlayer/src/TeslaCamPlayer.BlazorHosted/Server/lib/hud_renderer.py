#!/usr/bin/env python3
"""
Tesla Cam Player - HUD Renderer
Generates graphical HUD overlays from SEI telemetry data using PIL/Pillow
"""

import json
import sys
import argparse
import math
from PIL import Image, ImageDraw, ImageFont

# HUD Constants
HUD_WIDTH = 520
HUD_HEIGHT = 60
HUD_MARGIN_TOP = 5
HUD_BG_COLOR = (8, 10, 14, 220)
HUD_BORDER_COLOR = (255, 255, 255, 42)
HUD_GLOSS_COLOR = (255, 255, 255, 18)
HUD_TEXT_COLOR = (245, 245, 245, 255)

CHIP_SIZE = 42
CHIP_RADIUS = 12
CHIP_INNER_RADIUS = 10
CHIP_GAP = 8
CHIP_GAP_WIDE = 14
SPEED_BLOCK_HALF_W = 60
SPEED_BLOCK_HALF_H = 28

# Icon supersampling for higher quality (2x = better quality, 4x = best quality but slower)
ICON_SUPERSAMPLE = 2

BLINKER_COLOR = (120, 255, 140, 220)  # Green
BRAKE_COLOR = (255, 90, 90, 230)  # Red
THROTTLE_COLOR = (120, 255, 120, 220)  # Green
AUTOPILOT_COLOR = (100, 170, 255, 230)  # Blue
CRUISE_COLOR = (90, 160, 255, 200)
MAX_STEER_DEG = 540

# Location overlay constants (matching FFmpeg style)
LOCATION_FONT_SIZE = 24
LOCATION_TEXT_COLOR = (255, 255, 255, 255)  # White
LOCATION_BG_COLOR = (0, 0, 0, 102)  # Black @ 40% opacity (0.4 * 255)
LOCATION_PADDING = 8
LOCATION_MARGIN_X = 10
LOCATION_MARGIN_Y = 10

def parse_args():
    parser = argparse.ArgumentParser(description='Render HUD overlay from SEI telemetry')
    parser.add_argument('--sei-json', required=True, help='Path to SEI messages JSON file')
    parser.add_argument('--width', type=int, default=1920, help='Output width')
    parser.add_argument('--height', type=int, default=1080, help='Output height')
    parser.add_argument('--framerate', type=float, default=30.0, help='Frame rate')
    parser.add_argument('--use-mph', action='store_true', help='Use MPH instead of km/h')
    parser.add_argument('--output-dir', help='Output directory for PNG frames (if not using pipe)')
    parser.add_argument('--pipe', action='store_true', help='Output raw RGBA to stdout')
    parser.add_argument('--location-text', help='Street and city text (e.g., "Main St, San Francisco")')
    parser.add_argument('--fallback-lat', type=float, help='Fallback GPS latitude from event.json')
    parser.add_argument('--fallback-lon', type=float, help='Fallback GPS longitude from event.json')
    parser.add_argument('--enable-location-overlay', action='store_true', help='Render location overlay (city/GPS)')
    return parser.parse_args()

def load_sei_messages(json_path):
    """Load SEI messages from JSON file"""
    with open(json_path, 'r') as f:
        messages = json.load(f)
    return messages

def load_font(size):
    """Try to load a TrueType font, fall back to default if not available"""
    font_paths = [
        '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf',
        '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',
        '/System/Library/Fonts/Helvetica.ttc',
        '/usr/share/fonts/TTF/DejaVuSans-Bold.ttf'
    ]

    for font_path in font_paths:
        try:
            return ImageFont.truetype(font_path, size)
        except:
            continue

    # Fall back to default font
    return ImageFont.load_default()

def clamp(value, min_value, max_value):
    return max(min_value, min(max_value, value))

def lerp(a, b, t):
    return a + (b - a) * t

def lerp_color(c1, c2, t):
    t = clamp(t, 0.0, 1.0)
    return tuple(int(round(lerp(c1[i], c2[i], t))) for i in range(4))

class HudRenderState:
    def __init__(self, frame_rate):
        safe_rate = frame_rate if frame_rate and frame_rate > 0 else 30.0
        self.frame_rate = safe_rate
        self.frame_dt_ms = 1000.0 / safe_rate
        self.elapsed_ms = 0.0
        self.steer_initialized = False
        self.steer_target_deg = 0.0
        self.steer_display_deg = 0.0
        self.last_target_change_ms = 0.0

    def advance(self):
        self.elapsed_ms += self.frame_dt_ms

    def blink_pulse(self):
        phase = ((self.elapsed_ms / 1000.0) + 0.5) % 1.0  # start bright to mirror CSS keyframes
        return 0.5 * (1 - math.cos(2 * math.pi * phase))

    def smooth_steer(self, target_deg):
        target = clamp(target_deg, -MAX_STEER_DEG, MAX_STEER_DEG)
        if not self.steer_initialized:
            self.steer_initialized = True
            self.steer_target_deg = target
            self.steer_display_deg = target
            self.last_target_change_ms = self.elapsed_ms
        else:
            if target != self.steer_target_deg:
                self.steer_target_deg = target
                self.last_target_change_ms = self.elapsed_ms

            smoothing = 1 - math.exp(-self.frame_dt_ms / 110.0) if self.frame_dt_ms > 0 else 1.0
            self.steer_display_deg += (self.steer_target_deg - self.steer_display_deg) * smoothing

            if self.elapsed_ms - self.last_target_change_ms > 500.0:
                self.steer_display_deg = self.steer_target_deg

        return self.steer_display_deg

def pick_number(data, keys, default=None):
    for key in keys:
        if key not in data:
            continue
        try:
            val = float(data[key])
            if not math.isnan(val):
                return val
        except (TypeError, ValueError):
            continue
    return default

def pick_bool(data, keys, default=False):
    for key in keys:
        val = data.get(key)
        if isinstance(val, bool):
            return val
        if isinstance(val, (int, float)):
            return val != 0
        if isinstance(val, str):
            lowered = val.strip().lower()
            if lowered in ['true', '1', 'yes', 'on']:
                return True
            if lowered in ['false', '0', 'no', 'off']:
                return False
    return default

def normalize_gear(raw):
    if raw is None:
        return 'P'

    if isinstance(raw, (int, float)):
        idx = int(raw)
        mapping = ['P', 'D', 'R', 'N']
        if 0 <= idx < len(mapping):
            return mapping[idx]

    if isinstance(raw, str):
        gear_str = raw.strip().upper()
        lookup = {
            'PARK': 'P',
            'GEAR_PARK': 'P',
            'P': 'P',
            'DRIVE': 'D',
            'GEAR_DRIVE': 'D',
            'D': 'D',
            'REVERSE': 'R',
            'GEAR_REVERSE': 'R',
            'R': 'R',
            'NEUTRAL': 'N',
            'GEAR_NEUTRAL': 'N',
            'N': 'N'
        }
        return lookup.get(gear_str, gear_str[:1] if gear_str else '?')

    return '?'

def normalize_autopilot(raw):
    if raw is None:
        return 'NONE'

    if isinstance(raw, (int, float)):
        idx = int(raw)
        states = ['NONE', 'SELF_DRIVING', 'AUTOSTEER', 'TACC']
        return states[idx] if 0 <= idx < len(states) else 'NONE'

    state = str(raw).strip().upper()
    aliases = {
        'AUTOPILOT': 'AUTOSTEER',
        'FSD': 'SELF_DRIVING',
        'SELFDRIVING': 'SELF_DRIVING',
        'SELF-DRIVING': 'SELF_DRIVING',
        'AUTO-STEER': 'AUTOSTEER',
        'AUTO_STEER': 'AUTOSTEER',
        'CRUISE': 'TACC'
    }
    return aliases.get(state, state or 'NONE')

def draw_rounded_rectangle(draw, xy, radius, fill=None, outline=None, width=1):
    """Draw a rounded rectangle without outline self-intersections."""
    x1, y1, x2, y2 = xy
    w = x2 - x1
    h = y2 - y1
    r = max(0, min(radius, w / 2, h / 2))

    rect = [int(round(x1)), int(round(y1)), int(round(x2)), int(round(y2))]
    r = int(round(r))

    if fill is not None:
        draw.rounded_rectangle(rect, radius=r, fill=fill)

    if outline is not None and width and width > 0:
        draw.rounded_rectangle(rect, radius=r, outline=outline, width=int(width))

def draw_chip_background(draw, box, radius=CHIP_RADIUS, fill=None, outline=None, outline_width=1):
    draw_rounded_rectangle(draw, box, radius, fill=fill, outline=outline, width=outline_width)

def draw_blinker_chip(draw, x, y, active, direction='left', pulse=1.0):
    box = [x, y, x + CHIP_SIZE, y + CHIP_SIZE]
    pulse = clamp(pulse, 0.0, 1.0)

    base_fill = (22, 24, 26, 175)
    base_outline = HUD_BORDER_COLOR
    if active:
        base_fill = lerp_color(base_fill, (24, 36, 26, 210), 0.65 + 0.25 * pulse)
        base_outline = lerp_color(base_outline, (140, 255, 170, 180), 0.4 + 0.4 * pulse)

    draw_chip_background(draw, box, fill=base_fill, outline=base_outline, outline_width=2)

    # Render arrow at higher resolution for smoother edges
    ss_size = CHIP_SIZE * ICON_SUPERSAMPLE
    arrow_img = Image.new('RGBA', (ss_size, ss_size), (0, 0, 0, 0))
    arrow_draw = ImageDraw.Draw(arrow_img)

    cx = ss_size / 2
    cy = ss_size / 2
    arrow = 14 * ICON_SUPERSAMPLE

    if direction == 'left':
        points = [
            (cx + arrow * 0.65, cy - arrow),
            (cx - arrow * 0.85, cy),
            (cx + arrow * 0.65, cy + arrow)
        ]
    else:
        points = [
            (cx - arrow * 0.65, cy - arrow),
            (cx + arrow * 0.85, cy),
            (cx - arrow * 0.65, cy + arrow)
        ]

    arrow_base = (210, 210, 210, 170)
    arrow_color = lerp_color(arrow_base, BLINKER_COLOR, 0.65 + 0.35 * pulse) if active else arrow_base
    arrow_draw.polygon(points, fill=arrow_color)

    # Scale down to target size with high-quality resampling
    arrow_img = arrow_img.resize((CHIP_SIZE, CHIP_SIZE), Image.LANCZOS)
    draw._image.paste(arrow_img, (x, y), arrow_img)

def draw_brake_icon(draw, center, size, color):
    """Draw brake pedal icon with supersampling for better quality"""
    # Render at higher resolution
    ss_factor = ICON_SUPERSAMPLE
    ss_size = int(size * ss_factor)
    icon_img = Image.new('RGBA', (ss_size, ss_size), (0, 0, 0, 0))
    icon_draw = ImageDraw.Draw(icon_img)

    cx = cy = ss_size / 2
    scale = ss_size / 24.0
    outline_points = [(6, 7), (18, 7), (20, 16), (12, 19), (4, 16)]
    scaled_outline = [(cx + (px - 12) * scale, cy + (py - 12) * scale) for px, py in outline_points]

    icon_draw.line(scaled_outline + [scaled_outline[0]], fill=color, width=max(2, int(2 * scale)))

    for x in [8, 10, 12, 14, 16]:
        x_pos = cx + (x - 12) * scale
        icon_draw.line(
            [(x_pos, cy + (9 - 12) * scale), (x_pos, cy + (14 - 12) * scale)],
            fill=color,
            width=max(1, int(1.4 * scale))
        )

    # Scale down and composite
    icon_img = icon_img.resize((int(size), int(size)), Image.LANCZOS)
    offset_x = int(center[0] - size / 2)
    offset_y = int(center[1] - size / 2)
    draw._image.paste(icon_img, (offset_x, offset_y), icon_img)

def draw_throttle_icon(draw, center, size, color):
    """Draw throttle pedal icon with supersampling for better quality"""
    # Render at higher resolution
    ss_factor = ICON_SUPERSAMPLE
    ss_size = int(size * ss_factor)
    icon_img = Image.new('RGBA', (ss_size, ss_size), (0, 0, 0, 0))
    icon_draw = ImageDraw.Draw(icon_img)

    cx = cy = ss_size / 2
    scale = ss_size / 24.0
    outline_points = [(9, 4), (15, 4), (16, 18), (12, 20), (8, 18)]
    scaled_outline = [(cx + (px - 12) * scale, cy + (py - 12) * scale) for px, py in outline_points]

    icon_draw.line(scaled_outline + [scaled_outline[0]], fill=color, width=max(2, int(2 * scale)))

    rect_top = cy + (2 - 12) * scale
    icon_draw.rectangle(
        [cx - 3 * scale, rect_top, cx + 3 * scale, rect_top + 2 * scale],
        outline=color,
        width=max(2, int(2 * scale))
    )

    # Scale down and composite
    icon_img = icon_img.resize((int(size), int(size)), Image.LANCZOS)
    offset_x = int(center[0] - size / 2)
    offset_y = int(center[1] - size / 2)
    draw._image.paste(icon_img, (offset_x, offset_y), icon_img)

def draw_pedal_chip(draw, x, y, value, color, icon_kind, font_small):
    box = [x, y, x + CHIP_SIZE, y + CHIP_SIZE]
    active = value > 0

    outline_color = HUD_BORDER_COLOR
    base_fill = (24, 24, 26, 200)
    icon_color = (235, 235, 235, 230)
    if icon_kind == 'brake' and active:
        outline_color = (255, 120, 120, 220)
        icon_color = (255, 190, 190, 240)
        base_fill = (40, 16, 16, 210)

    draw_chip_background(draw, box, fill=base_fill, outline=outline_color, outline_width=2)

    inner = [x + 7, y + 7, x + CHIP_SIZE - 7, y + CHIP_SIZE - 7]
    draw_rounded_rectangle(draw, inner, CHIP_INNER_RADIUS, fill=(8, 8, 10, 190), outline=(255, 255, 255, 24), width=1)

    clamped = clamp(value, 0.0, 1.0)
    fill_height = int((inner[3] - inner[1]) * clamped)
    if fill_height > 0:
        fill_box = [inner[0] + 1, inner[3] - fill_height, inner[2] - 1, inner[3]]
        draw_rounded_rectangle(draw, fill_box, CHIP_INNER_RADIUS, fill=color)

    icon_center = (inner[0] + (inner[2] - inner[0]) / 2, inner[1] + (inner[3] - inner[1]) / 2)
    icon_size = 24
    if icon_kind == 'brake':
        draw_brake_icon(draw, icon_center, icon_size, icon_color)
    else:
        draw_throttle_icon(draw, icon_center, icon_size, icon_color)

def draw_speed_block(draw, x, y, speed, unit, font_large, font_small):
    speed_box = [x - SPEED_BLOCK_HALF_W, y - SPEED_BLOCK_HALF_H, x + SPEED_BLOCK_HALF_W, y + SPEED_BLOCK_HALF_H]
    draw_chip_background(draw, speed_box, radius=22, fill=(18, 20, 24, 185), outline=HUD_BORDER_COLOR, outline_width=2)

    speed_text = f"{int(speed)}"
    bbox = draw.textbbox((0, 0), speed_text, font=font_large)
    text_w = bbox[2] - bbox[0]
    text_h = bbox[3] - bbox[1]
    speed_y = speed_box[1] + 4
    draw.text((x - text_w // 2, speed_y), speed_text, fill=HUD_TEXT_COLOR, font=font_large)

    unit_bbox = draw.textbbox((0, 0), unit, font=font_small)
    unit_w = unit_bbox[2] - unit_bbox[0]
    unit_h = unit_bbox[3] - unit_bbox[1]
    unit_y = speed_box[3] - unit_h - 4
    unit_y = max(unit_y, speed_y + text_h + 4)  # ensure no overlap
    draw.text((x - unit_w // 2, unit_y), unit, fill=(210, 210, 210, 210), font=font_small)
    return speed_box

def draw_gear_chip(draw, x, y, gear, font):
    box = [x, y, x + CHIP_SIZE, y + CHIP_SIZE]
    gear_text = normalize_gear(gear)
    gear_colors = {
        'P': (240, 240, 240, 255),
        'R': (255, 160, 160, 255),
        'N': (180, 210, 255, 255),
        'D': HUD_TEXT_COLOR
    }
    text_color = gear_colors.get(gear_text, HUD_TEXT_COLOR)

    draw_chip_background(draw, box, fill=(20, 20, 22, 200), outline=HUD_BORDER_COLOR, outline_width=2)

    bbox = draw.textbbox((0, 0), gear_text, font=font)
    text_w = bbox[2] - bbox[0]
    text_h = bbox[3] - bbox[1]
    center_x = x + CHIP_SIZE / 2
    center_y = y + CHIP_SIZE / 2
    text_x = int(round(center_x - text_w / 2))
    text_y = int(round(center_y - text_h / 2 - 3))  # nudge up to optically center
    draw.text((text_x, text_y), gear_text, fill=text_color, font=font)

def draw_wheel_chip(draw, x, y, angle, autopilot_state):
    box = [x, y, x + CHIP_SIZE, y + CHIP_SIZE]
    autopilot_state = normalize_autopilot(autopilot_state)
    is_autopilot = autopilot_state in ['AUTOSTEER', 'SELF_DRIVING']
    is_cruise = autopilot_state in ['TACC']

    base_fill = (22, 24, 26, 200)
    outline = HUD_BORDER_COLOR
    icon_color = (225, 225, 225, 240)

    if is_autopilot:
        icon_color = AUTOPILOT_COLOR
    elif is_cruise:
        base_fill = (18, 28, 42, 200)
        outline = (90, 160, 255, 170)

    draw_chip_background(draw, box, fill=base_fill, outline=outline, outline_width=2)

    # Render wheel icon at higher resolution for better quality
    ss_size = CHIP_SIZE * ICON_SUPERSAMPLE
    wheel_img = Image.new('RGBA', (ss_size, ss_size), (0, 0, 0, 0))
    wheel_draw = ImageDraw.Draw(wheel_img)
    center = ss_size / 2

    # Match web UI design: outer circle, horizontal bar, vertical bar, center hub
    # Scale from 24x24 viewBox to supersampled size
    scale = ss_size / 24.0

    # Outer circle (r=8 in viewBox, scaled)
    outer_radius = 8 * scale
    wheel_draw.ellipse(
        [center - outer_radius, center - outer_radius, center + outer_radius, center + outer_radius],
        outline=icon_color,
        width=max(2, int(1.4 * scale))
    )

    # Horizontal bar (from x=6.8 to x=17.2 at y=9.8 in viewBox)
    bar_y = center + (9.8 - 12) * scale  # 9.8 is above center (12)
    bar_x1 = center + (6.8 - 12) * scale
    bar_x2 = center + (17.2 - 12) * scale
    wheel_draw.line(
        [(bar_x1, bar_y), (bar_x2, bar_y)],
        fill=icon_color,
        width=max(2, int(2 * scale))
    )

    # Vertical bar (from y=9.8 to y=16.8 at x=12 in viewBox)
    bar_x = center  # x=12 is center
    bar_y1 = center + (9.8 - 12) * scale
    bar_y2 = center + (16.8 - 12) * scale
    wheel_draw.line(
        [(bar_x, bar_y1), (bar_x, bar_y2)],
        fill=icon_color,
        width=max(2, int(2 * scale))
    )

    # Center hub circle (r=1.8 in viewBox)
    hub_radius = 1.8 * scale
    wheel_draw.ellipse(
        [center - hub_radius, center - hub_radius, center + hub_radius, center + hub_radius],
        outline=icon_color,
        width=max(1, int(1.4 * scale))
    )

    # Rotate at high resolution for better quality
    rotation_angle = clamp(angle, -MAX_STEER_DEG, MAX_STEER_DEG)
    wheel_img = wheel_img.rotate(-rotation_angle, resample=Image.BICUBIC)

    # Scale down to target size with high-quality Lanczos resampling
    wheel_img = wheel_img.resize((CHIP_SIZE, CHIP_SIZE), Image.LANCZOS)
    draw._image.paste(wheel_img, (x, y), wheel_img)

def draw_location_overlay(draw, width, height, location_text, latitude, longitude, fallback_lat, fallback_lon, font):
    """Draw location overlay at bottom-left corner

    Args:
        location_text: Street/city text from event.json (e.g., "Main St, San Francisco")
        latitude: SEI GPS latitude (may be None or 0.0)
        longitude: SEI GPS longitude (may be None or 0.0)
        fallback_lat: Fallback latitude from event.json
        fallback_lon: Fallback longitude from event.json
    """
    sys.stderr.write(f"[LOCATION DEBUG] draw_location_overlay called: location_text={location_text}, latitude={latitude}, longitude={longitude}, fallback_lat={fallback_lat}, fallback_lon={fallback_lon}\n")
    sys.stderr.flush()

    # Determine which GPS to use (SEI or fallback)
    gps_lat = latitude
    gps_lon = longitude

    # Use fallback if SEI GPS is missing or invalid
    if gps_lat is None or gps_lon is None or (gps_lat == 0.0 and gps_lon == 0.0):
        sys.stderr.write(f"[LOCATION DEBUG] SEI GPS invalid/missing, using fallback: fallback_lat={fallback_lat}, fallback_lon={fallback_lon}\n")
        sys.stderr.flush()
        gps_lat = fallback_lat
        gps_lon = fallback_lon
    else:
        sys.stderr.write(f"[LOCATION DEBUG] Using SEI GPS: {gps_lat}, {gps_lon}\n")
        sys.stderr.flush()

    # Build location string
    parts = []
    if location_text:
        parts.append(location_text)

    # Add GPS coordinates if available
    if gps_lat is not None and gps_lon is not None:
        # Skip if still (0, 0) after fallback
        if not (gps_lat == 0.0 and gps_lon == 0.0):
            gps_text = f"{gps_lat:.5f}, {gps_lon:.5f}"  # 5 decimal places like event.json
            if parts:
                parts.append(f"({gps_text})")
            else:
                parts.append(gps_text)

    # If no location data at all, skip rendering
    if not parts:
        sys.stderr.write("[LOCATION DEBUG] No location data to display - skipping render\n")
        sys.stderr.flush()
        return

    full_text = " ".join(parts)
    sys.stderr.write(f"[LOCATION DEBUG] Rendering location text: {full_text}\n")
    sys.stderr.flush()

    # Measure text
    bbox = draw.textbbox((0, 0), full_text, font=font)
    text_w = bbox[2] - bbox[0]
    text_h = bbox[3] - bbox[1]

    # Background box dimensions
    box_w = text_w + LOCATION_PADDING * 2
    box_h = text_h + LOCATION_PADDING * 2

    # Position at bottom-left (matching FFmpeg location)
    box_x = LOCATION_MARGIN_X
    box_y = height - box_h - LOCATION_MARGIN_Y

    # Draw semi-transparent black background
    draw_rounded_rectangle(
        draw,
        [box_x, box_y, box_x + box_w, box_y + box_h],
        radius=6,
        fill=LOCATION_BG_COLOR
    )

    # Draw text
    text_x = box_x + LOCATION_PADDING
    text_y = box_y + LOCATION_PADDING
    draw.text((text_x, text_y), full_text, fill=LOCATION_TEXT_COLOR, font=font)

def create_hud_frame(width, height, telemetry, use_mph, state=None, enable_location_overlay=False, location_text=None, fallback_lat=None, fallback_lon=None):
    """Create a single HUD frame"""
    # Create transparent image
    img = Image.new('RGBA', (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    if state:
        state.advance()

    # Load fonts
    font_large = load_font(34)
    font_location = load_font(24)  # Match timestamp font size
    font_medium = load_font(20)
    font_small = load_font(11)

    # Calculate HUD position (centered horizontally, near bottom where blank space exists)
    hud_x = (width - HUD_WIDTH) // 2
    hud_y = HUD_MARGIN_TOP

    # No pill background; render chips directly over video

    # Extract GPS coordinates from SEI telemetry (if available)
    latitude = pick_number(telemetry, ['latitude', 'latitudeDeg', 'latitude_deg'], None) if telemetry else None
    longitude = pick_number(telemetry, ['longitude', 'longitudeDeg', 'longitude_deg'], None) if telemetry else None

    # Draw location overlay at bottom-left (even if telemetry is None, we can use fallback GPS)
    if enable_location_overlay:
        draw_location_overlay(draw, width, height, location_text, latitude, longitude, fallback_lat, fallback_lon, font_location)

    # Parse telemetry data for HUD rendering
    if telemetry is None:
        return img

    speed_mps = pick_number(telemetry, ['vehicleSpeedMps', 'vehicle_speed_mps', 'speed_mps'], 0) or 0
    speed_mph = speed_mps * 2.23694
    speed = speed_mph if use_mph else speed_mph * 1.60934
    unit = 'mph' if use_mph else 'km/h'

    gear = telemetry.get('gearState') or telemetry.get('gear_state') or telemetry.get('gear')
    brake = pick_bool(telemetry, ['brakeApplied', 'brake_applied'], False)
    throttle_raw = pick_number(telemetry, ['throttlePct', 'acceleratorPedalPosition', 'accelerator_pedal_position'], 0) or 0
    if throttle_raw <= 1.5:
        throttle_raw *= 100
    throttle = clamp(throttle_raw / 100.0, 0.0, 1.0)
    steering_angle = pick_number(telemetry, ['steeringWheelAngle', 'steering_wheel_angle'], 0) or 0
    left_blinker = pick_bool(telemetry, ['leftBlinkerOn', 'blinker_on_left'], False)
    right_blinker = pick_bool(telemetry, ['rightBlinkerOn', 'blinker_on_right'], False)
    autopilot_raw = telemetry.get('autopilotState') or telemetry.get('autopilot_state') or 'NONE'
    autopilot = normalize_autopilot(autopilot_raw)
    steer_display_angle = state.smooth_steer(steering_angle) if state else clamp(steering_angle, -MAX_STEER_DEG, MAX_STEER_DEG)
    blinker_pulse = state.blink_pulse() if state else 1.0

    chip_y = hud_y + (HUD_HEIGHT - CHIP_SIZE) // 2

    speed_x = hud_x + HUD_WIDTH // 2
    speed_y = hud_y + HUD_HEIGHT // 2
    speed_box = draw_speed_block(draw, speed_x, speed_y - 2, speed, unit, font_large, font_small)

    # symmetric spacing around the speed block
    LEFT_CHIPS = 3
    RIGHT_CHIPS = 3
    left_group_w = LEFT_CHIPS * CHIP_SIZE + (LEFT_CHIPS - 1) * CHIP_GAP
    right_group_w = RIGHT_CHIPS * CHIP_SIZE + (RIGHT_CHIPS - 1) * CHIP_GAP
    gap = CHIP_GAP_WIDE
    pad = 2  # keep outlines/AA from touching

    left_end = speed_box[0] - gap - pad
    left_start = left_end - left_group_w
    right_start = speed_box[2] + gap + pad

    # left group: blinker, gear, brake
    current_x = left_start
    draw_blinker_chip(draw, current_x, chip_y, left_blinker, 'left', pulse=blinker_pulse)
    current_x += CHIP_SIZE + CHIP_GAP

    draw_gear_chip(draw, current_x, chip_y, gear, font_medium)
    current_x += CHIP_SIZE + CHIP_GAP

    brake_value = 1.0 if brake else 0.0
    draw_pedal_chip(draw, current_x, chip_y, brake_value, BRAKE_COLOR, 'brake', font_small)

    # right group: wheel, throttle, blinker
    current_x = right_start
    draw_wheel_chip(draw, current_x, chip_y, steer_display_angle, autopilot)
    current_x += CHIP_SIZE + CHIP_GAP

    draw_pedal_chip(draw, current_x, chip_y, throttle, THROTTLE_COLOR, 'throttle', font_small)
    current_x += CHIP_SIZE + CHIP_GAP

    draw_blinker_chip(draw, current_x, chip_y, right_blinker, 'right', pulse=blinker_pulse)

    return img

def main():
    args = parse_args()

    sys.stderr.write(f"[LOCATION DEBUG] hud_renderer.py started with args: enable_location_overlay={args.enable_location_overlay}, location_text={args.location_text}, fallback_lat={args.fallback_lat}, fallback_lon={args.fallback_lon}\n")
    sys.stderr.flush()

    # Load SEI messages
    messages = load_sei_messages(args.sei_json)
    total = len(messages)
    state = HudRenderState(args.framerate)

    sys.stderr.write(f"Rendering {total} HUD frames to {'pipe' if args.pipe else args.output_dir}...\n")
    sys.stderr.flush()

    for i, telemetry in enumerate(messages):
        # Create HUD frame with location overlay
        img = create_hud_frame(
            args.width,
            args.height,
            telemetry,
            args.use_mph,
            state,
            args.enable_location_overlay,
            args.location_text,
            args.fallback_lat,
            args.fallback_lon
        )

        if args.pipe:
            # Output raw RGBA to stdout
            sys.stdout.buffer.write(img.tobytes())
            sys.stdout.buffer.flush()
        else:
            # Save as PNG file
            frame_path = f"{args.output_dir}/frame_{i:06d}.png"
            img.save(frame_path, 'PNG')

        # Progress reporting
        if (i + 1) % 10 == 0 or (i + 1) == total:
            sys.stderr.write(f"\rRendered {i+1}/{total} frames ({(i+1)/total*100:.1f}%)")
            sys.stderr.flush()

    sys.stderr.write("\nHUD rendering complete\n")
    sys.stderr.flush()

if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        sys.stderr.write("\nHUD rendering cancelled\n")
        sys.exit(1)
    except Exception as e:
        sys.stderr.write(f"\nError: {e}\n")
        import traceback
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
