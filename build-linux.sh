#!/bin/bash
set -e

PROJECT_ROOT="$(cd "$(dirname "$0")" && pwd)"
CLIENT_PATH="$PROJECT_ROOT/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client"
SERVER_PATH="$PROJECT_ROOT/TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server"
OUTPUT_PATH="$PROJECT_ROOT/dist/linux-x64"

echo "Building TeslaCam Player for Linux..."

# Step 1: Build frontend assets
echo ""
echo "[1/4] Building frontend assets..."
cd "$CLIENT_PATH"
npm install
npx gulp default

# Step 2: Publish .NET application
echo ""
echo "[2/4] Publishing .NET application..."
cd "$SERVER_PATH"
dotnet publish -c Release -r linux-x64 --self-contained -o "$OUTPUT_PATH"

# Step 3: Copy additional files
echo ""
echo "[3/4] Copying additional files..."
cp "$PROJECT_ROOT/README.md" "$OUTPUT_PATH/" 2>/dev/null || true
cp "$PROJECT_ROOT/LICENSE" "$OUTPUT_PATH/" 2>/dev/null || true

# Copy DISTRIBUTION-README.md as README.md
if [ -f "$PROJECT_ROOT/DISTRIBUTION-README.md" ]; then
    cp "$PROJECT_ROOT/DISTRIBUTION-README.md" "$OUTPUT_PATH/README.md"
fi

# Create sample configuration
cat > "$OUTPUT_PATH/appsettings.json" <<'EOF'
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ClipsRootPath": "/mnt/teslacam",
  "IndexingBatchSize": 1000,
  "IndexingMinBatchSize": 250,
  "IndexingMaxMemoryUtilization": 0.85,
  "IndexingMemoryRecoveryDelaySeconds": 5
}
EOF

# Create Linux setup script
cat > "$OUTPUT_PATH/setup.sh" <<'EOF'
#!/bin/bash
echo "TeslaCam Player - Linux Setup"
echo "=============================="
echo ""
echo "Before running this application, ensure you have:"
echo "  1. FFmpeg 6.1+ installed: sudo apt install ffmpeg (or equivalent)"
echo "  2. Python 3.8+ installed: sudo apt install python3"
echo "  3. Python Pillow library: pip3 install Pillow"
echo ""
echo "Configuration:"
echo "  Edit appsettings.json to set your TeslaCam folder path (ClipsRootPath)"
echo ""
read -p "Press Enter to continue..."
EOF
chmod +x "$OUTPUT_PATH/setup.sh"

# Create run script
cat > "$OUTPUT_PATH/run.sh" <<'EOF'
#!/bin/bash
echo "Starting TeslaCam Player..."
echo "Access the application at: http://localhost:5000"
echo "Press Ctrl+C to stop"
echo ""
./TeslaCamPlayer.BlazorHosted.Server
EOF
chmod +x "$OUTPUT_PATH/run.sh"
chmod +x "$OUTPUT_PATH/TeslaCamPlayer.BlazorHosted.Server"

# Step 4: Create tar.gz archive
echo ""
echo "[4/4] Creating tar.gz archive..."
mkdir -p "$PROJECT_ROOT/dist"
TAR_PATH="$PROJECT_ROOT/dist/TeslaCamPlayer-Linux-x64.tar.gz"
cd "$PROJECT_ROOT/dist"
tar -czf "TeslaCamPlayer-Linux-x64.tar.gz" -C linux-x64 .

echo ""
echo "Build complete!"
echo "Output: $TAR_PATH"
echo "Size: $(du -h "$TAR_PATH" | cut -f1)"
