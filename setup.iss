[Setup]
AppName=Radmin Stream
AppVersion=1.0
DefaultDirName={autopf}\Radmin Stream
DefaultGroupName=Radmin Stream
OutputDir=D:\Projetos em andameto\app-live
OutputBaseFilename=RadminStream_Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de Trabalho"; GroupDescription: "Atalhos adicionais:"

[Files]
Source: "D:\Projetos em andameto\app-live\publish_zip\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Radmin Stream"; Filename: "{app}\RadminStreamApp.exe"
Name: "{autodesktop}\Radmin Stream"; Filename: "{app}\RadminStreamApp.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RadminStreamApp.exe"; Description: "Iniciar o Radmin Stream agora"; Flags: nowait postinstall skipifsilent
