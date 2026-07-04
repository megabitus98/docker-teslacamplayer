# TeslaCam Player - Portable Distribution

A web-based viewer for Tesla dashcam videos with support for viewing multiple camera angles, timeline navigation, and video export with HUD overlays.

## Quick Start

### Windows

1. **Install Prerequisites:**
   - Download and install FFmpeg: https://ffmpeg.org/download.html
     - Add to PATH or place `ffmpeg.exe` in the application folder
   - Download and install Python 3.8+: https://www.python.org/downloads/
   - Install Pillow: Open Command Prompt and run `pip install Pillow`

2. **Configure:**
   - Edit `appsettings.json`
   - Set `ClipsRootPath` to your TeslaCam folder (e.g., `"C:\\TeslaCam"`)

3. **Run:**
   - Double-click `run.bat` or run `TeslaCamPlayer.BlazorHosted.Server.exe`
   - Open browser to http://localhost:5000

### Linux

1. **Install Prerequisites:**
   ```bash
   sudo apt update
   sudo apt install ffmpeg python3 python3-pip
   pip3 install Pillow
   ```

2. **Configure:**
   - Edit `appsettings.json`
   - Set `ClipsRootPath` to your TeslaCam folder (e.g., `"/mnt/teslacam"`)

3. **Run:**
   ```bash
   chmod +x run.sh
   ./run.sh
   ```
   - Open browser to http://localhost:5000

## Configuration Options

Configure these in the WebUI settings dialog, through environment variables, or in `appsettings.json`. Saved WebUI values take precedence over environment variables until reset.

| Setting | Description | Default |
|---------|-------------|---------|
| `ClipsRootPath` | Path to TeslaCam folder (REQUIRED) | - |
| `CACHE_DATABASE_PATH` | SQLite database location | `./clips.db` |
| `EXPORT_ROOT_PATH` | Where to save exports | `./wwwroot/exports` |
| `EXPORT_RETENTION_HOURS` | Auto-delete exports after N hours | 24 |
| `ENABLE_DELETE` | Allow deleting clips via UI | false |
| `SPEED_UNIT` | Display speed as "mph" or "kmh" | "kmh" |

## Features

- View synchronized multi-camera footage (front, back, left, right)
- Timeline navigation with event filtering
- Export clips with optional HUD overlay showing:
  - Vehicle speed, steering angle, gear
  - Brake/throttle indicators
  - Autopilot state, blinkers
  - GPS location overlay
- Dark mode UI
- Real-time indexing of new clips

## Troubleshooting

### "Unable to resolve service for type IFfProbeService"
- Make sure FFmpeg is installed and `ffprobe` is accessible
- Windows: Ensure `lib\ffprobe.exe` exists in application folder

### "HUD renderer failed"
- Verify Python 3 is installed and in PATH
- Install Pillow: `pip install Pillow` (Windows) or `pip3 install Pillow` (Linux)
- Check that `lib\hud_renderer.py` exists

### "Configured clips root path doesn't exist"
- Edit `appsettings.json` and set `ClipsRootPath` to valid TeslaCam folder
- Ensure the path exists and has read permissions

### Port 5000 already in use
- Set environment variable: `ASPNETCORE_URLS=http://localhost:5001`
- Or edit `appsettings.json` and add: `"Urls": "http://localhost:5001"`

## License

See LICENSE file for details.

## Support

For issues and feature requests, visit the project repository on GitHub.
