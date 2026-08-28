; version.iss é gerado pelo build a partir de <Version> no .csproj — não editar à mão.
#include "version.iss"

; O ISCC resolve os caminhos do script contra o diretório de onde ele foi chamado, e não
; contra a pasta do script. Sem ancorar, compilar da raiz ("ISCC build\setup.iss" — que é o
; comando documentado) falhava com "não foi possível encontrar o caminho".
; SourcePath é a pasta deste arquivo; subir um nível dá a raiz do repositório, e é a ela que
; todos os caminhos abaixo se referem, venha a chamada de onde vier.
#define RepoRoot AddBackslash(SourcePath) + ".."

[Setup]
SourceDir={#RepoRoot}
AppName=Radmin Stream Live
AppVersion={#AppVersion}
DefaultDirName={pf}\Radmin Stream Live
DefaultGroupName=Radmin Stream Live
UninstallDisplayIcon={app}\RadminStreamApp.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.
OutputBaseFilename=RadminStream_Setup
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=src\RadminStreamApp\Assets\app_icon.ico

[Files]
Source: "publish_zip\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Radmin Stream Live"; Filename: "{app}\RadminStreamApp.exe"; WorkingDir: "{app}"
Name: "{commondesktop}\Radmin Stream Live"; Filename: "{app}\RadminStreamApp.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\RadminStreamApp.exe"; Description: "Launch Radmin Stream Live"; Flags: nowait postinstall skipifsilent
