@echo off
setlocal EnableExtensions
cd /d "%~dp0"

REM Resolve absolute path (the old ..\ path made start fail silently)
for %%I in ("%~dp0..\owml\_runtime\OWML") do set "OWML=%%~fI"

echo ============================================
echo  WildCraft - Outer Wilds via OWML
echo ============================================
echo.
echo Pasta OWML:
echo   %OWML%
echo.

if not exist "%OWML%\OWML.Launcher.exe" (
  echo ERRO: nao achei OWML.Launcher.exe
  echo Corre de novo o setup ou diz ao assistente.
  echo.
  pause
  exit /b 1
)

if not exist "C:\Program Files (x86)\Steam\steamapps\common\Outer Wilds\OuterWilds.exe" (
  echo ERRO: Outer Wilds nao encontrado em:
  echo   C:\Program Files ^(x86^)\Steam\steamapps\common\Outer Wilds
  echo.
  pause
  exit /b 1
)

echo A iniciar OWML (vai abrir o Outer Wilds pela Steam^)...
echo Depois abre o Minecraft com launch_minecraft_wildcraft.bat
echo.

pushd "%OWML%"
start "OWML" /D "%OWML%" "OWML.Launcher.exe"
set ERR=%ERRORLEVEL%
popd

if errorlevel 1 (
  echo Falhou a iniciar. Codigo %ERR%
  pause
  exit /b %ERR%
)

echo OK - OWML foi chamado. Se o jogo nao abrir, olha a janela da Steam.
echo Esta janela fecha em 5 segundos...
timeout /t 5 /nobreak >nul
exit /b 0
