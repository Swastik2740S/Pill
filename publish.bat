@echo off
cd /d "%~dp0"
dotnet publish src\DynamicIsland -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o publish
if errorlevel 1 (
  pause
  exit /b 1
)
echo.
echo Done. Run publish\DynamicIsland.exe
pause
