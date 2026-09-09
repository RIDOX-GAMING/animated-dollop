$ErrorActionPreference = 'Stop'
Write-Host '=== Road96 Farsi Auto 2.0 build ===' -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw 'dotnet SDK 6.x is required.'
}

dotnet restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

dotnet build -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

$src = Join-Path $PSScriptRoot 'bin\Release\net6.0\Road96FarsiAuto.dll'
$dest = Join-Path $PSScriptRoot 'release\BepInEx\plugins\Road96FarsiAuto.dll'
New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
Copy-Item $src $dest -Force
Write-Host "Built: $dest" -ForegroundColor Green
