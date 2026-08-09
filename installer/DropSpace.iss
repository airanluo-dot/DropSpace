#ifndef AppVersion
  #error AppVersion must be supplied by scripts/Build-Installer.ps1
#endif
#ifndef VersionInfoVersion
  #error VersionInfoVersion must be supplied by scripts/Build-Installer.ps1
#endif
#ifndef VersionCode
  #error VersionCode must be supplied by scripts/Build-Installer.ps1
#endif
#ifndef SourceExe
  #error SourceExe must be supplied by scripts/Build-Installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by scripts/Build-Installer.ps1
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "DropSpaceSetup"
#endif

[Setup]
AppId={{E11EC281-BCE7-4F98-8EEF-2387E202CF0F}
AppName=DropSpace
AppVersion={#AppVersion}
AppVerName=DropSpace {#AppVersion}
AppPublisher=DropSpace
AppPublisherURL=https://github.com/airanluo-dot/DropSpace
AppSupportURL=https://github.com/airanluo-dot/DropSpace/issues
AppUpdatesURL=https://github.com/airanluo-dot/DropSpace/releases
DefaultDirName={localappdata}\Programs\DropSpace
DefaultGroupName=DropSpace
DisableProgramGroupPage=auto
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\src\DropSpace.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\DropSpace.exe
UninstallDisplayName=DropSpace
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallFilesDir={app}\uninstall
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UninstallLogMode=append
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
CloseApplications=no
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
VersionInfoVersion={#VersionInfoVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoCompany=DropSpace
VersionInfoDescription=DropSpace Setup
VersionInfoProductName=DropSpace
VersionInfoProductVersion={#VersionInfoVersion}
VersionInfoProductTextVersion={#AppVersion}
SignedUninstaller=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "DropSpace.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\DropSpace"; Filename: "{app}\DropSpace.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\DropSpace"; Filename: "{app}\DropSpace.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU64; Subkey: "Software\DropSpace\Install"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU64; Subkey: "Software\DropSpace\Install"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey
Root: HKCU64; Subkey: "Software\DropSpace\Install"; ValueType: dword; ValueName: "VersionCode"; ValueData: "{#VersionCode}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\DropSpace.exe"; Description: "{cm:LaunchProgram,DropSpace}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\install.version"
Type: filesandordirs; Name: "{localappdata}\DropSpace"; Check: ShouldPurgeData

[Code]
const
  EventModifyState = $0002;
  SynchronizeAccess = $00100000;
  WaitObject0 = 0;
  MaintenanceWaitMilliseconds = 15000;

var
  DeleteDataCheckBox: TNewCheckBox;

function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): HANDLE;
external 'OpenEventW@kernel32.dll stdcall';

function SignalEvent(EventHandle: HANDLE): BOOL;
external 'SetEvent@kernel32.dll stdcall';

function WaitForSingleObject(EventHandle: HANDLE; Milliseconds: DWORD): DWORD;
external 'WaitForSingleObject@kernel32.dll stdcall';

function CloseKernelHandle(Handle: HANDLE): BOOL;
external 'CloseHandle@kernel32.dll stdcall';

function HasParameter(const Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  InstalledVersionCode: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKCU64, 'Software\DropSpace\Install', 'VersionCode', InstalledVersionCode) and
     (InstalledVersionCode > {#VersionCode}) and
     (not HasParameter('/ALLOWDOWNGRADE=1')) then
  begin
    MsgBox(
      'A newer DropSpace version is already installed. Setup blocked this downgrade to protect the installation. ' +
      'Use a newer installer, or explicitly pass /ALLOWDOWNGRADE=1 if you intentionally need to test a downgrade.',
      mbError,
      MB_OK);
    Result := False;
  end;
end;

function RequestMaintenanceShutdown(): Boolean;
var
  RequestEvent: HANDLE;
  StoppedEvent: HANDLE;
  WaitResult: DWORD;
  Attempt: Integer;
begin
  Result := True;
  if not CheckForMutexes('Local\DropSpace.Running.v1') then
    Exit;

  Log('DropSpace is running; requesting graceful maintenance shutdown through named events.');
  RequestEvent := OpenEvent(EventModifyState, False, 'Local\DropSpace.MaintenanceShutdown.v1');
  StoppedEvent := OpenEvent(SynchronizeAccess, False, 'Local\DropSpace.MaintenanceStopped.v1');
  if (RequestEvent = 0) or (StoppedEvent = 0) then
  begin
    if RequestEvent <> 0 then
      CloseKernelHandle(RequestEvent);
    if StoppedEvent <> 0 then
      CloseKernelHandle(StoppedEvent);
    Log(Format('Maintenance events were unavailable (Win32 error %d).', [DLLGetLastError]));
    Result := False;
    Exit;
  end;

  if not SignalEvent(RequestEvent) then
  begin
    Log(Format('Could not signal the maintenance request (Win32 error %d).', [DLLGetLastError]));
    CloseKernelHandle(RequestEvent);
    CloseKernelHandle(StoppedEvent);
    Result := False;
    Exit;
  end;

  WaitResult := WaitForSingleObject(StoppedEvent, MaintenanceWaitMilliseconds);
  CloseKernelHandle(RequestEvent);
  CloseKernelHandle(StoppedEvent);
  if WaitResult <> WaitObject0 then
  begin
    Log(Format('Timed out waiting for graceful maintenance shutdown (wait result %d).', [WaitResult]));
    Result := False;
    Exit;
  end;

  { The stopped event is signaled after managed disposal completes and immediately before
    process exit. Give the kernel a bounded moment to release the process-owned mutex. }
  for Attempt := 1 to 50 do
  begin
    if not CheckForMutexes('Local\DropSpace.Running.v1') then
      Break;
    Sleep(100);
  end;
  Result := not CheckForMutexes('Local\DropSpace.Running.v1');
  if Result then
    Log('DropSpace completed graceful maintenance shutdown.')
  else
    Log('DropSpace acknowledged shutdown but its process mutex remained present.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not RequestMaintenanceShutdown() then
    Result := 'DropSpace is still running and could not close gracefully. Choose Exit DropSpace, then run Setup again.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\install.version'), '{#AppVersion}', False);
end;

function InitializeUninstall: Boolean;
begin
  Result := RequestMaintenanceShutdown();
  if not Result and not UninstallSilent then
    MsgBox(
      'DropSpace is still running and could not close gracefully. Choose Exit DropSpace, then start uninstall again.',
      mbError,
      MB_OK);
end;

procedure InitializeUninstallProgressForm;
begin
  DeleteDataCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  DeleteDataCheckBox.Parent := UninstallProgressForm;
  DeleteDataCheckBox.Left := UninstallProgressForm.StatusLabel.Left;
  DeleteDataCheckBox.Top := UninstallProgressForm.StatusLabel.Top + UninstallProgressForm.StatusLabel.Height + ScaleY(16);
  DeleteDataCheckBox.Width := UninstallProgressForm.StatusLabel.Width;
  DeleteDataCheckBox.Height := ScaleY(24);
  DeleteDataCheckBox.Caption := 'Also delete all DropSpace local data and settings (original files are kept).';
  DeleteDataCheckBox.Checked := HasParameter('/PURGEDATA=1');
end;

function ShouldPurgeData(): Boolean;
begin
  Result := HasParameter('/PURGEDATA=1') or
            ((DeleteDataCheckBox <> nil) and DeleteDataCheckBox.Checked);
end;
