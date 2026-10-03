# WildCraft launcher: Outer Wilds (OWML) + hidden Minecraft (SkyCraft Fabric), one click.
# Usage: JOGAR.bat   (or: powershell -ExecutionPolicy Bypass -File JOGAR.ps1 [-NoBuild] [-NoMinecraft] [-NoOuterWilds])
param(
	[switch]$NoBuild,
	[switch]$NoMinecraft,
	[switch]$NoOuterWilds
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..\..')).Path           # SkyCraft-0.1.0
$owml = Join-Path $root 'wildcraft\owml\_runtime\OWML'
$modDir = Join-Path $owml 'Mods\Tecno.WildCraft'
$fabric = Join-Path $root 'fabric'

function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Fail($t) { Say "ERRO: $t" Red; Read-Host 'Enter para sair' | Out-Null; exit 1 }

Say '============================================' Cyan
Say ' WildCraft - Minecraft dentro do Outer Wilds' Cyan
Say '============================================' Cyan

# --- Outer Wilds --------------------------------------------------------------------------------------------
$owGame = $null
foreach ($p in @("${env:ProgramFiles(x86)}\Steam\steamapps\common\Outer Wilds", "$env:ProgramFiles\Steam\steamapps\common\Outer Wilds", 'D:\SteamLibrary\steamapps\common\Outer Wilds', 'E:\SteamLibrary\steamapps\common\Outer Wilds')) {
	if (Test-Path (Join-Path $p 'OuterWilds.exe')) { $owGame = $p; break }
}
if (-not $owGame -and -not $NoOuterWilds) { Fail 'Outer Wilds nao encontrado (Steam). Edite JOGAR.ps1 com o caminho do jogo.' }
if (-not (Test-Path (Join-Path $owml 'OWML.Launcher.exe'))) { Fail "OWML nao encontrado em $owml" }

# --- Java 25 --------------------------------------------------------------------------------------------------
$java = $null
foreach ($p in @('C:\Program Files\Eclipse Adoptium\jdk-25.0.3.9-hotspot') + (Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -ErrorAction SilentlyContinue | ? { $_.Name -like 'jdk-25*' } | % FullName)) {
	if ($p -and (Test-Path (Join-Path $p 'bin\java.exe'))) { $java = $p; break }
}
if (-not $java -and -not $NoMinecraft) { Fail 'Java 25 nao encontrado (Eclipse Adoptium JDK 25).' }

# --- Mod build (only when the sources are newer than the installed DLL) -------------------------------------------
$dll = Join-Path $modDir 'WildCraft.dll'
if (-not $NoBuild) {
	$src = Get-ChildItem (Join-Path $root 'wildcraft\owml') -Recurse -Include *.cs, *.csproj, default-config.json, manifest.json |
		? { $_.FullName -notmatch '\\(obj|bin|_runtime)\\' } | sort LastWriteTime -Descending | select -First 1
	if (-not (Test-Path $dll) -or ($src -and $src.LastWriteTime -gt (Get-Item $dll).LastWriteTime)) {
		Say 'A compilar o mod WildCraft...' Yellow
		if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet SDK nao encontrado (precisa para compilar o mod).' }
		& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'install_owml_mod.ps1')
		if ($LASTEXITCODE -ne 0) { Fail 'A compilacao do mod falhou.' }
	}
}

# --- Stale instances of this project ------------------------------------------------------------------------------
# A Minecraft left over from an earlier run keeps the SkyCraft world locked and blocks the new one.
if (-not $NoMinecraft) {
	$stale = Get-CimInstance Win32_Process -Filter "Name='java.exe' OR Name='javaw.exe'" -ErrorAction SilentlyContinue |
		? { $_.CommandLine -and ($_.CommandLine -like "*$fabric*" -or $_.CommandLine -like '*skycraft.hostName*') }
	if ($stale) {
		Say 'A fechar Minecraft/Gradle antigos deste projeto...' Yellow
		$stale | % { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
		Start-Sleep 2
	}
}
if (-not $NoOuterWilds) {
	$running = Get-Process OuterWilds -ErrorAction SilentlyContinue
	if ($running) { Say 'O Outer Wilds ja esta aberto: feche-o para carregar a versao nova do mod.' Yellow; Read-Host 'Feche o jogo e carregue Enter' | Out-Null }
}

# --- Config: keep OWML pointing at this install --------------------------------------------------------------------
try {
	$cfgPath = Join-Path $owml 'OWML.Config.json'
	if ((Test-Path $cfgPath) -and $owGame) {
		$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
		$cfg.gamePath = $owGame.Replace('\', '/')
		$cfg.owmlPath = $owml + '\'
		$cfg | ConvertTo-Json | Set-Content $cfgPath -Encoding UTF8
	}
} catch { }

# --- Minecraft (hidden) ---------------------------------------------------------------------------------------------
if (-not $NoMinecraft) {
	Say 'A iniciar o Minecraft (escondido; a primeira vez pode demorar)...' Green
	$env:JAVA_HOME = $java
	$env:PATH = "$java\bin;$env:PATH"
	$log = Join-Path $root 'wildcraft\minecraft_launch.log'
	Start-Process -FilePath (Join-Path $fabric 'gradlew.bat') -ArgumentList 'runClient', '--no-daemon' -WorkingDirectory $fabric `
		-RedirectStandardOutput $log -RedirectStandardError ($log + '.err') -WindowStyle Hidden
}

# --- Outer Wilds ------------------------------------------------------------------------------------------------------
if (-not $NoOuterWilds) {
	Say 'A iniciar o Outer Wilds com o OWML...' Green
	Start-Process -FilePath (Join-Path $owml 'OWML.Launcher.exe') -WorkingDirectory $owml
}

Say ''
Say 'Pronto. Como jogar:' Cyan
Say '  1) No Outer Wilds, entre no save (Retomar Expedicao) e espere acordar na fogueira.'
Say '  2) Quando o Minecraft estiver pronto, aparece a barra de itens do Minecraft: voce JA e um jogador de Minecraft.'
Say '  WASD / rato / Espaco / Shift = Minecraft  |  E = inventario  |  1-9 = hotbar  |  F5 = camera'
Say '  O = menu do Minecraft  |  Esc = menu do Outer Wilds  |  G = interagir do Outer Wilds (fogueira, nave, NPCs)'
Say '  Dentro da nave voce joga Outer Wilds normal; ao sair a pe de novo, volta o Minecraft.'
Say ''
Say "Logs: wildcraft\owml\_runtime\OWML\Logs\latest.txt  |  fabric\run\logs\latest.log"
Start-Sleep 6
