# Builds the WildCraft OWML mod and installs it into the bundled OWML (wildcraft\owml\_runtime\OWML\Mods).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$guess = "C:\Program Files (x86)\Steam\steamapps\common\Outer Wilds\OuterWilds_Data\Managed"
if (-not $env:OUTERWILDS_MANAGED -and (Test-Path $guess)) { $env:OUTERWILDS_MANAGED = $guess }

dotnet build "$root\wildcraft\owml\WildCraft\WildCraft.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$modDir = "$root\wildcraft\owml\_runtime\OWML\Mods\Tecno.WildCraft"
New-Item -ItemType Directory -Force -Path $modDir | Out-Null
$out = "$root\wildcraft\owml\WildCraft\bin\Release"
Copy-Item "$out\WildCraft.dll", "$out\WildCraft.Core.dll", "$out\manifest.json", "$out\default-config.json" $modDir -Force
# An old settings file from an earlier build (e.g. blocks switched off) would override the new defaults.
$cfg = "$modDir\config.json"
if ((Test-Path $cfg) -and ((Get-Content $cfg -Raw) -match 'DrawHudOverlay')) { Remove-Item $cfg -Force }
Write-Host "Installed to $modDir"
