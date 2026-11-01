@echo off
setlocal
cd /d "%~dp0"
rem Builds what a release contains: a self-contained portable zip, and Setup.exe if Inno Setup is installed.
rem Usage: package.bat [version]     (GitHub builds releases automatically from tags; this is for local checks.)
set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=2.0.0"
set "RID=win-x64"
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"

where dotnet >nul 2>nul || (
  echo .NET SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)

echo [1/3] Building a self-contained release %VERSION% (the target PC needs no .NET)...
if exist "publish\%RID%" rmdir /s /q "publish\%RID%"
dotnet publish src\DynamicIsland -c Release -r %RID% --self-contained true -p:PublishReadyToRun=true -p:Version=%VERSION% -o "publish\%RID%" --nologo -v q
if errorlevel 1 (
  echo Build failed. Scroll up for the errors.
  pause
  exit /b 1
)

echo [2/3] Making the portable zip...
if not exist dist mkdir dist
powershell -NoProfile -Command "Compress-Archive -Force -Path 'publish\%RID%\*' -DestinationPath 'dist\DynamicIsland-%VERSION%-x64-portable.zip'"

echo [3/3] Making the installer...
if exist "%ISCC%" (
  "%ISCC%" /Qp /DAppVersion=%VERSION% /DArch=x64 /DSourceDir=..\publish\%RID% installer\DynamicIsland.iss
  copy /y installer\Output\*.exe dist\ >nul
) else (
  echo Inno Setup is not installed here, so Setup.exe was skipped. GitHub builds it for every release.
)

echo.
echo Done. Look in the dist folder.
pause
