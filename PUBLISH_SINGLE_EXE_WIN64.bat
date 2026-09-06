@echo off
setlocal
cd /d "%~dp0"
echo ===== BLE Serial Terminal - WIN64 SELF-CONTAINED PUBLISH =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-icon.ps1"
if errorlevel 1 goto :fail
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK was not found.
  pause
  exit /b 1
)

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 goto :fail

echo.
echo PUBLISH PASS
echo EXE folder: bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
pause
exit /b 0
:fail
echo.
echo PUBLISH FAILED
pause
exit /b 1
