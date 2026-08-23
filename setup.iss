#define MyAppVersion GetFileVersion("publish_zip\RadminStreamApp.exe")
[Setup]
AppName=Radmin Stream Live
AppVersion={#MyAppVersion}
DefaultDirName={pf}\Radmin Stream Live
DefaultGroupName=Radmin Stream Live
UninstallDisplayIcon={app}\RadminStreamApp.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.
OutputBaseFilename=RadminStream_Setup
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=app_icon.ico

[Files]
Source: "publish_zip\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Radmin Stream Live"; Filename: "{app}\RadminStreamApp.exe"
Name: "{commondesktop}\Radmin Stream Live"; Filename: "{app}\RadminStreamApp.exe"

[Run]
Filename: "{app}\RadminStreamApp.exe"; Description: "Launch Radmin Stream Live"; Flags: nowait postinstall skipifsilent
