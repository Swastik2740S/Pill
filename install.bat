@echo off
setlocal
cd /d "%~dp0"
set "TARGET=%LOCALAPPDATA%\Programs\DynamicIsland"
set "STAGE=%TEMP%\DynamicIslandBuild"

where dotnet >nul 2>nul || (
  echo .NET SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)

echo [1/4] Building an optimized release...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
dotnet publish src\DynamicIsland -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o "%STAGE%" --nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed, nothing was changed. Scroll up for the errors.
  pause
  exit /b 1
)

echo [2/4] Replacing the installed copy in %TARGET% ...
taskkill /im DynamicIsland.exe /f >nul 2>nul
timeout /t 1 /nobreak >nul
robocopy "%STAGE%" "%TARGET%" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo Copy failed.
  pause
  exit /b 1
)

echo [3/4] Registering start with Windows and a Start menu entry...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DynamicIsland /t REG_SZ /d "\"%TARGET%\DynamicIsland.exe\"" /f >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs') + '\Dynamic Island.lnk'); $s.TargetPath = '%TARGET%\DynamicIsland.exe'; $s.WorkingDirectory = '%TARGET%'; $s.Description = 'Dynamic Island for Windows'; $s.Save()"

echo [4/4] Starting Dynamic Island...
start "" "%TARGET%\DynamicIsland.exe"

echo.
echo Done. Dynamic Island now starts automatically every time you sign in.
timeout /t 4 >nul
