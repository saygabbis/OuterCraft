# WildCraft MVP: start FakeHost (Python). Start Minecraft+SkyCraft Fabric yourself first or in another terminal.
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent  # .../SkyCraft-0.1.0
if (-not (Test-Path "$repo\wildcraft\host\fake_outerwilds.py")) {
    $repo = Split-Path $PSScriptRoot -Parent  # allow running if tools/ moves
}
Set-Location $repo
Write-Host "Starting WildCraft FakeHost from $repo"
Write-Host "In another terminal, launch Minecraft 26.3 + SkyCraft Fabric."
python "$repo\wildcraft\host\fake_outerwilds.py" @args
