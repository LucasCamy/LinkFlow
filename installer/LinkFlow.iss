; Instalador do LinkFlow (Inno Setup 6). Gerado por scripts\release.ps1, que define
; AppVersion e SourceDir (pasta publicada self-contained, não precisa de .NET instalado).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\release\LinkFlow"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
AppId={{6F1A3C2E-8B4D-4E7A-9C51-2D7F0B9E4A13}
AppName=LinkFlow
AppVersion={#AppVersion}
AppVerName=LinkFlow {#AppVersion}
AppPublisher=LucasCamy
AppPublisherURL=https://github.com/LucasCamy/LinkFlow
AppSupportURL=https://github.com/LucasCamy/LinkFlow/issues
AppUpdatesURL=https://github.com/LucasCamy/LinkFlow/releases
DefaultDirName={autopf}\LinkFlow
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=LinkFlow-Setup-{#AppVersion}
SetupIconFile=..\src\LinkFlow.App\Assets\linkflow.ico
UninstallDisplayIcon={app}\LinkFlow.App.exe
UninstallDisplayName=LinkFlow
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
UsedUserAreasWarning=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=LinkFlow

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ptbr.TaskService=Instalar o serviço do Windows (recomendado: o controle roda sem a janela aberta e inicia antes do logon)
en.TaskService=Install the Windows service (recommended: control runs without the window and starts before logon)
ptbr.TaskAutostart=Abrir o LinkFlow na bandeja ao entrar no Windows
en.TaskAutostart=Open LinkFlow in the tray when signing in to Windows
ptbr.StatusService=Instalando e iniciando o serviço LinkFlow...
en.StatusService=Installing and starting the LinkFlow service...
ptbr.LaunchApp=Abrir o LinkFlow
en.LaunchApp=Open LinkFlow

[Tasks]
Name: "service"; Description: "{cm:TaskService}"
Name: "autostart"; Description: "{cm:TaskAutostart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LinkFlow"; Filename: "{app}\LinkFlow.App.exe"
Name: "{autodesktop}\LinkFlow"; Filename: "{app}\LinkFlow.App.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LinkFlow"; \
    ValueData: """{app}\LinkFlow.App.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\LinkFlow.App.exe"; Parameters: "--install-service"; StatusMsg: "{cm:StatusService}"; \
    Flags: runhidden waituntilterminated; Tasks: service
Filename: "{app}\LinkFlow.App.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\LinkFlow.App.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitApp"
Filename: "{app}\LinkFlow.App.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure RunQuiet(const FileName, Params: String);
var
  Code: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Atualização: fecha a interface e para o serviço antes de substituir os arquivos.
  O serviço é reconfigurado e iniciado de novo pela tarefa "service". }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\LinkFlow.App.exe');
  if FileExists(Exe) then
  begin
    RunQuiet(Exe, '--exit');
    RunQuiet(ExpandConstant('{sys}\taskkill.exe'), '/F /IM LinkFlow.App.exe');
    RunQuiet(ExpandConstant('{sys}\sc.exe'), 'stop LinkFlowService');
    Sleep(3000);
  end;
end;
