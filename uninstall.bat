@echo off
setlocal
set "TARGET=%LOCALAPPDATA%\Programs\DynamicIsland"

echo Removing Dynamic Island...
taskkill /im DynamicIsland.exe /f >nul 2>nul
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DynamicIsland /f >nul 2>nul
powershell -NoProfile -Command "Remove-Item -LiteralPath ([Environment]::GetFolderPath('Programs') + '\Dynamic Island.lnk') -ErrorAction SilentlyContinue"
timeout /t 1 /nobreak >nul
if exist "%TARGET%" rmdir /s /q "%TARGET%"

echo Done. Your settings and log remain in %LOCALAPPDATA%\DynamicIsland.
pause
