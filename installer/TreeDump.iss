; TreeDump Pro installer. Build with scripts\Build-Release.ps1
; Defines can be overridden from the command line: /DAppVersion=1.0.0 /DSourceDir=... /DOutputDir=...

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\sc"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{8F3C2A5E-4B71-4D96-9E0A-6C1D7B52F3A8}
AppName=TreeDump Pro
AppVersion={#AppVersion}
AppVerName=TreeDump Pro {#AppVersion}
AppPublisher=DeZ4p
AppPublisherURL=https://github.com/DeZ4p
AppSupportURL=https://github.com/DeZ4p/TreeDump/issues
AppUpdatesURL=https://github.com/DeZ4p/TreeDump/releases
DefaultDirName={autopf}\TreeDump Pro
DefaultGroupName=TreeDump Pro
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=TreeDump-Setup-{#AppVersion}
SetupIconFile=..\Assets\TreeDump.ico
UninstallDisplayIcon={app}\TreeDump.exe
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TreeDump Pro"; Filename: "{app}\TreeDump.exe"
Name: "{group}\Uninstall TreeDump Pro"; Filename: "{uninstallexe}"
Name: "{autodesktop}\TreeDump Pro"; Filename: "{app}\TreeDump.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TreeDump.exe"; Description: "{cm:LaunchProgram,TreeDump Pro}"; Flags: nowait postinstall skipifsilent