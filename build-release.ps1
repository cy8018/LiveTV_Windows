# Build Release Script for Live TV
# This script builds the release version of the application
# Note: ARM64 Windows devices can run the x64 version through emulation

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Live TV - Release Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Clean previous builds
Write-Host "[1/3] Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean IPTV-Player.csproj -c Release --nologo -v q
if (Test-Path "./publish") {
    Remove-Item -Path "./publish" -Recurse -Force
}
Write-Host "  Done." -ForegroundColor Green

# Restore packages
Write-Host "[2/3] Restoring packages..." -ForegroundColor Yellow
dotnet restore IPTV-Player.csproj -r win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Restore failed!" -ForegroundColor Red
    exit 1
}
Write-Host "  Done." -ForegroundColor Green

# Publish
Write-Host "[3/3] Publishing release build..." -ForegroundColor Yellow
dotnet publish IPTV-Player.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "./publish" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    exit 1
}
Write-Host "  Done." -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output location: ./publish/" -ForegroundColor White

$exePath = "./publish/LiveTV.exe"
if (Test-Path $exePath) {
    $size = (Get-Item $exePath).Length / 1MB
    Write-Host "  LiveTV.exe ($([math]::Round($size, 2)) MB)" -ForegroundColor Yellow
}

# Calculate total folder size
$totalSize = (Get-ChildItem -Path "./publish" -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host "Total size: $([math]::Round($totalSize, 2)) MB" -ForegroundColor Gray
Write-Host ""
Write-Host "Note: This x64 build works on both x64 and ARM64 Windows devices." -ForegroundColor DarkGray
Write-Host "      ARM64 devices run it through built-in x64 emulation." -ForegroundColor DarkGray
