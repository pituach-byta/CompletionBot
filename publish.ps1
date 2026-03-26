# ==============================
# publish.ps1 - CompletionBot Deploy Script
# Usage: .\publish.ps1
# ==============================

$ErrorActionPreference = "Stop"
$rootDir   = $PSScriptRoot
$clientDir = Join-Path $rootDir "Client"
$serverDir = Join-Path $rootDir "Server"
$wwwrootDir = Join-Path $serverDir "wwwroot"
$publishDir = Join-Path $serverDir "publish"

Write-Host ""
Write-Host "=============================="
Write-Host " CompletionBot - Build & Publish"
Write-Host "=============================="
Write-Host ""

# Step 1: Build React
Write-Host "[1/3] Building React client..." -ForegroundColor Cyan
Set-Location $clientDir
npm run build
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: React build failed!" -ForegroundColor Red; exit 1 }
Write-Host "     React build complete." -ForegroundColor Green

# Step 2: Copy build to wwwroot
Write-Host ""
Write-Host "[2/3] Copying build to wwwroot..." -ForegroundColor Cyan
$distDir = Join-Path $clientDir "dist"
if (-not (Test-Path $distDir)) { Write-Host "ERROR: dist folder not found!" -ForegroundColor Red; exit 1 }

# Clear old files from wwwroot (keep BotUploads untouched)
Get-ChildItem $wwwrootDir -Exclude "empty.txt" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# Copy dist contents to wwwroot
Copy-Item -Path "$distDir\*" -Destination $wwwrootDir -Recurse -Force
Write-Host "     Copy complete." -ForegroundColor Green

# Step 3: Publish .NET server
Write-Host ""
Write-Host "[3/3] Publishing .NET server..." -ForegroundColor Cyan
Set-Location $serverDir
dotnet publish CompletionBot.Server.csproj -c Release -o "$publishDir" --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: .NET publish failed!" -ForegroundColor Red; exit 1 }
Write-Host "     Server publish complete." -ForegroundColor Green

# Return to root
Set-Location $rootDir

Write-Host ""
Write-Host "=============================="
Write-Host " Done! Output folder: Server\publish\" -ForegroundColor Green
Write-Host ""
Write-Host " Next steps:"
Write-Host " 1. Connect to remote server (RDP / WinSCP / network share)"
Write-Host " 2. Copy contents of Server\publish\ to the app folder on the server"
Write-Host " 3. Do NOT overwrite:"
Write-Host "    - appsettings.json (if server has different settings)"
Write-Host "    - BotUploads\ folder"
Write-Host "    - Data\ folder"
Write-Host "=============================="
