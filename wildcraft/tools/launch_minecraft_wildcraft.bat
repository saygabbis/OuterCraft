@echo off
setlocal
REM Abre Minecraft 26.3 + SkyCraft Fabric direto (nao precisa aparecer no CurseForge).
set JAVA_HOME=C:\Program Files\Eclipse Adoptium\jdk-25.0.3.9-hotspot
set PATH=%JAVA_HOME%\bin;%PATH%
cd /d "%~dp0..\..\fabric"
echo.
echo A iniciar Minecraft WildCraft / SkyCraft...
echo (primeira vez pode demorar a descarregar libs)
echo.
call gradlew.bat runClient --no-daemon
pause
