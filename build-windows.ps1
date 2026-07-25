#!/usr/bin/env pwsh
# Build script for Windows portable distribution

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$ClientPath = Join-Path $ProjectRoot "TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Client"
$ServerPath = Join-Path $ProjectRoot "TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/Server"
$OutputPath = Join-Path $ProjectRoot "dist/windows-x64"

Write-Host "Building TeslaCam Player for Windows..." -ForegroundColor Cyan

# Step 1: Build frontend assets
Write-Host "`n[1/4] Building frontend assets..." -ForegroundColor Yellow
Set-Location $ClientPath
npm install
npx gulp default

# Step 2: Publish .NET application
Write-Host "`n[2/4] Publishing .NET application..." -ForegroundColor Yellow
Set-Location $ServerPath
dotnet publish -c Release -r win-x64 --self-contained -o $OutputPath

# Step 3: Copy additional files
Write-Host "`n[3/4] Copying additional files..." -ForegroundColor Yellow
Copy-Item (Join-Path $ProjectRoot "README.md") -Destination $OutputPath -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $ProjectRoot "LICENSE") -Destination $OutputPath -Force -ErrorAction SilentlyContinue

# Copy DISTRIBUTION-README.md as README.txt (for Windows users)
if (Test-Path (Join-Path $ProjectRoot "DISTRIBUTION-README.md")) {
    Copy-Item (Join-Path $ProjectRoot "DISTRIBUTION-README.md") -Destination (Join-Path $OutputPath "README.txt") -Force
}

# Create sample configuration
$sampleConfig = @"
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ClipsRootPath": "C:\\TeslaCam",
  "IndexingBatchSize": 1000,
  "IndexingMinBatchSize": 250,
  "IndexingMaxMemoryUtilization": 0.85,
  "IndexingMemoryRecoveryDelaySeconds": 5
}
"@
$sampleConfig | Out-File -FilePath (Join-Path $OutputPath "appsettings.json") -Encoding UTF8 -Force

# Create Windows setup script
$setupScript = @"
@echo off
echo TeslaCam Player - Windows Setup
echo ================================
echo.
echo Before running this application, ensure you have:
echo   1. FFmpeg 6.1+ installed and in your PATH (https://ffmpeg.org/download.html)
echo   2. Python 3.8+ installed (https://www.python.org/downloads/)
echo   3. Python Pillow library: pip install Pillow
echo.
echo Configuration:
echo   Edit appsettings.json to set your TeslaCam folder path (ClipsRootPath)
echo.
pause
"@
$setupScript | Out-File -FilePath (Join-Path $OutputPath "SETUP.bat") -Encoding ASCII -Force

# Create run script
$runScript = @"
@echo off
echo Starting TeslaCam Player...
echo Access the application at: http://localhost:5000
echo Press Ctrl+C to stop
echo.
TeslaCamPlayer.BlazorHosted.Server.exe
"@
$runScript | Out-File -FilePath (Join-Path $OutputPath "run.bat") -Encoding ASCII -Force

# Step 4: Create ZIP archive
Write-Host "`n[4/4] Creating ZIP archive..." -ForegroundColor Yellow
$distDir = Join-Path $ProjectRoot "dist"
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}
$ZipPath = Join-Path $ProjectRoot "dist/TeslaCamPlayer-Windows-x64.zip"
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$OutputPath/*" -DestinationPath $ZipPath -CompressionLevel Optimal

Write-Host "`nBuild complete!" -ForegroundColor Green
Write-Host "Output: $ZipPath" -ForegroundColor Green
Write-Host "Size: $([math]::Round((Get-Item $ZipPath).Length / 1MB, 2)) MB" -ForegroundColor Green
