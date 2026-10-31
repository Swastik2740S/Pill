@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul || (
  echo .NET SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)
dotnet run --project src\DynamicIsland -c Release
if errorlevel 1 pause
