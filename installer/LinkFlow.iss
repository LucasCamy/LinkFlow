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
#define AppGuid "6F1A3C2E-8B4D-4E7A-9C51-2D7F0B9E4A13"

[Setup]
AppId={{{#AppGuid}}
AppName=LinkFlow
AppVersion={#AppVersion}
AppVerName=LinkFlow {#AppVersion}
AppPublisher=LucasCamy
AppPublisherURL=https://github.com/LucasCamy/LinkFlow
AppSupportURL=https://github.com/LucasCamy/LinkFlow/issues
AppUpdatesURL=https://github.com/LucasCamy/LinkFlow/releases
UsePreviousTasks=yes
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
CloseApplications=yes
RestartApplications=no
SetupMutex=Global\LinkFlow.Setup
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
ptbr.UpdateTargetError=Abra a instalação atual do LinkFlow e verifique novamente se há uma versão mais recente.
en.UpdateTargetError=Open the installed copy of LinkFlow and check again for a newer version.
ptbr.CloseAppError=Não foi possível fechar o LinkFlow. Encerre o aplicativo pela bandeja e tente novamente.
en.CloseAppError=Could not close LinkFlow. Exit the app from the system tray and try again.
ptbr.StopServiceError=Não foi possível parar o serviço do LinkFlow. Tente novamente.
en.StopServiceError=Could not stop the LinkFlow service. Try again.
ptbr.StartServiceError=Não foi possível reiniciar o serviço do LinkFlow. Você pode tentar de novo nas configurações do aplicativo.
en.StartServiceError=Could not restart the LinkFlow service. You can try again in the app settings.
ptbr.ReopenError=Abra o LinkFlow pelo menu Iniciar.
en.ReopenError=Open LinkFlow from the Start menu.

[Tasks]
Name: "service"; Description: "{cm:TaskService}"
Name: "autostart"; Description: "{cm:TaskAutostart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LinkFlow"; Filename: "{app}\LinkFlow.App.exe"
Name: "{autodesktop}\LinkFlow"; Filename: "{app}\LinkFlow.App.exe"; Tasks: desktopicon; Check: IsManualSetup

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LinkFlow"; \
    ValueData: """{app}\LinkFlow.App.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue; Check: IsManualSetup

[Run]
Filename: "{app}\LinkFlow.App.exe"; Parameters: "--install-service"; StatusMsg: "{cm:StatusService}"; \
    Flags: runhidden waituntilterminated; Tasks: service; Check: ShouldInstallService
Filename: "{app}\LinkFlow.App.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\LinkFlow.App.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitApp"
Filename: "{app}\LinkFlow.App.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\LinkFlowService';

var
  RestartService: Boolean;
  ReopenApp: Boolean;
  InstallServiceTask: Boolean;

function IsAppUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:LINKFLOWUPDATE|0}') = '1');
end;

function IsManualSetup: Boolean;
begin
  Result := not IsAppUpdate;
end;

function ShouldInstallService: Boolean;
begin
  Result := InstallServiceTask;
end;

function IsNewerUpdate(const Directory, Exe: String): Boolean;
var
  Registered: String;
  ExistingVersion, SetupVersion: Int64;
begin
  Result := RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', Registered) and
    (CompareText(RemoveBackslashUnlessRoot(Registered), RemoveBackslashUnlessRoot(Directory)) = 0) and
    GetPackedVersion(Exe, ExistingVersion) and StrToVersion('{#AppVersion}', SetupVersion) and
    (ComparePackedVersion(SetupVersion, ExistingVersion) > 0);
end;

function QueryServiceState: String;
var
  Code, I: Integer;
  Output: TExecOutput;
begin
  Result := '';
  if not ExecAndCaptureOutput(ExpandConstant('{sys}\sc.exe'), 'query LinkFlowService', '', SW_HIDE,
    ewWaitUntilTerminated, Code, Output) then Exit;
  if (Code <> 0) or Output.Error then Exit;
  { O nome do campo é localizado, mas o valor do estado após os dois pontos é fixo. }
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    if Pos(' RUNNING', Output.StdOut[I]) > 0 then Result := 'RUNNING';
    if Pos(' START_PENDING', Output.StdOut[I]) > 0 then Result := 'START_PENDING';
    if Pos(' STOP_PENDING', Output.StdOut[I]) > 0 then Result := 'STOP_PENDING';
    if Pos(' STOPPED', Output.StdOut[I]) > 0 then Result := 'STOPPED';
  end;
end;

function WaitForServiceState(const TargetState: String): Boolean;
var
  I: Integer;
  State: String;
begin
  for I := 1 to 40 do
  begin
    State := QueryServiceState;
    Result := State = TargetState;
    if Result or (State = '') then Exit;
    Sleep(500);
  end;
end;

function StopInstalledService: Boolean;
var
  ImagePath, Expected, State: String;
  Code: Integer;
begin
  Result := True;
  if not RegQueryStringValue(HKLM64, ServiceKey, 'ImagePath', ImagePath) then Exit;
  Expected := ExpandConstant('{app}\LinkFlow.Service.exe');
  if (CompareText(ImagePath, '"' + Expected + '"') <> 0) and (CompareText(ImagePath, Expected) <> 0) then Exit;

  State := QueryServiceState;
  if State = 'STOPPED' then Exit;
  Result := False;
  if State = '' then Exit;
  if State <> 'STOP_PENDING' then
  begin
    RestartService := True;
    if not ExecAndLogOutput(ExpandConstant('{sys}\sc.exe'), 'stop LinkFlowService', '', SW_HIDE,
      ewWaitUntilTerminated, Code, nil) or ((Code <> 0) and (Code <> 1062)) then Exit;
  end;
  Result := WaitForServiceState('STOPPED');
end;

function StartInstalledService: Boolean;
var
  Code: Integer;
begin
  Result := ExecAndLogOutput(ExpandConstant('{sys}\sc.exe'), 'start LinkFlowService', '', SW_HIDE,
    ewWaitUntilTerminated, Code, nil) and ((Code = 0) or (Code = 1056)) and WaitForServiceState('RUNNING');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Directory, Exe, ServiceImage: String;
  Code: Integer;
begin
  Result := '';
  Directory := ExpandConstant('{app}');
  Exe := AddBackslash(Directory) + 'LinkFlow.App.exe';
  InstallServiceTask := not IsAppUpdate and WizardIsTaskSelected('service');
  if IsAppUpdate and not IsNewerUpdate(Directory, Exe) then
  begin
    Result := CustomMessage('UpdateTargetError');
    Exit;
  end;
  if FileExists(Exe) and RegQueryStringValue(HKLM64, ServiceKey, 'ImagePath', ServiceImage) then
    InstallServiceTask := False;
  if FileExists(Exe) then
  begin
    ReopenApp := IsAppUpdate;
    if not ExecAsOriginalUser(Exe, '--exit', Directory, SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      Result := CustomMessage('CloseAppError');
      Exit;
    end;
    if not StopInstalledService then Result := CustomMessage('StopServiceError');
  end;
end;

procedure DeinitializeSetup;
var
  Exe, Directory: String;
  Code: Integer;
begin
  if not RestartService and not ReopenApp then Exit;
  Directory := ExpandConstant('{app}');
  Exe := AddBackslash(Directory) + 'LinkFlow.App.exe';
  if RestartService and not StartInstalledService then
    MsgBox(CustomMessage('StartServiceError'), mbError, MB_OK);
  if ReopenApp and not ExecAsOriginalUser(Exe, '', Directory, SW_SHOWNORMAL, ewNoWait, Code) then
    MsgBox(CustomMessage('ReopenError'), mbError, MB_OK);
end;
