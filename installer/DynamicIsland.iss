; Dynamic Island for Windows: per-user installer, no administrator rights needed.
;
; Built automatically by .github/workflows/release.yml, or locally by package.bat:
;   ISCC /DAppVersion=2.0.1 /DArch=x64 /DSourceDir=..\publish\win-x64 installer\DynamicIsland.iss
; Needs Inno Setup 6.3 or newer (https://jrsoftware.org/isinfo.php).

#ifndef AppVersion
  #define AppVersion "2.0.1"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif

[Setup]
AppId={{6F2B8C4E-3A1D-4E9B-9C57-2D8E5B1A7F43}
AppName=Dynamic Island
AppVersion={#AppVersion}
AppVerName=Dynamic Island {#AppVersion}
AppPublisher=Swastik Verma
#ifdef RepoUrl
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
#endif
; Same folder install.bat uses, so a source install is upgraded in place.
DefaultDirName={localappdata}\Programs\DynamicIsland
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir=Output
OutputBaseFilename=DynamicIsland-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\DynamicIsland\Assets\DynamicIsland.ico
UninstallDisplayIcon={app}\DynamicIsland.exe
UninstallDisplayName=Dynamic Island
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "startup"; Description: "Start Dynamic Island when I sign in"; GroupDescription: "Options:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Dynamic Island"; Filename: "{app}\DynamicIsland.exe"; Comment: "Dynamic Island for Windows"

[Registry]
; The same value the app's own "Start with Windows" switch uses, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DynamicIsland"; ValueData: """{app}\DynamicIsland.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\DynamicIsland.exe"; Description: "Start Dynamic Island now"; Flags: nowait postinstall

[Code]
// The island has no taskbar window to close politely, so stop it before replacing or removing files.
procedure StopIsland();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im DynamicIsland.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopIsland();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopIsland();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Also covers "Start with Windows" turned on later from inside the app.
  // Settings, history and the log in %LOCALAPPDATA%\DynamicIsland are kept.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DynamicIsland');
end;
