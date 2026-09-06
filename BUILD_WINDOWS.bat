@echo off
setlocal
cd /d "%~dp0"
echo ===== BLE Serial Terminal - BUILD =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-icon.ps1"
if errorlevel 1 goto :fail
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK was not found.
  echo Install .NET 8 SDK from Microsoft, then run this file again.
  pause
  exit /b 1
)
dotnet restore
if errorlevel 1 goto :fail
dotnet build -c Release
if errorlevel 1 goto :fail
echo.
echo BUILD PASS
echo Output: bin\Release\net8.0-windows10.0.19041.0\BLESerialTerminal.exe
pause
exit /b 0
:fail
echo.
echo BUILD FAILED
pause
exit /b 1
