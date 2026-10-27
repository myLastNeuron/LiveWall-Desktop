# Builds dist\LiveWallSetup.exe: the LiveWall app zipped into a small self-contained setup exe.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$payload = Join-Path $root 'installer\payload'
$zip = Join-Path $root 'installer\payload.zip'
$out = Join-Path $root 'installer\bin\publish'
$dist = Join-Path $root 'dist'

# 1. Publish the app (framework-dependent: setup downloads the .NET runtime if it is missing).
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
dotnet publish (Join-Path $root 'LiveWall\LiveWall.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $payload
if ($LASTEXITCODE) { throw 'LiveWall publish failed' }

# The XML/PDB files are docs, not needed at runtime.
Get-ChildItem $payload -Include *.xml, *.pdb -File -Recurse | Remove-Item -Force

# 2. Zip it so the setup exe can embed it.
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal

# 3. Build the setup exe (self-contained, so it runs on a PC with no .NET at all).
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'installer\LiveWallSetup.csproj') -c Release -o $out
if ($LASTEXITCODE) { throw 'Setup publish failed' }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $out 'LiveWallSetup.exe') (Join-Path $dist 'LiveWallSetup.exe') -Force
Remove-Item $zip -Force

$mb = [math]::Round((Get-Item (Join-Path $dist 'LiveWallSetup.exe')).Length / 1MB, 1)
Write-Host "Built dist\LiveWallSetup.exe ($mb MB)"
