; Build with Build-Installer.ps1 after publishing the self-contained x64 app.
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif

[Setup]
AppId={{9C462B1A-1133-4EC8-B740-FCC87B992921}
AppName=UGREEN Remote Drive
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=LukaStuurman
AppPublisherURL=https://github.com/LukaStuurman/UGREEN-remote-windows-file-service
DefaultDirName={autopf}\UGREEN Remote Drive
DefaultGroupName=UGREEN Remote Drive
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Drivers must match the OS architecture; do not offer this build on ARM64.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=UGREENRemoteDrive-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\UGREENRemoteDrive.exe
InfoBeforeFile=NOTICE.txt

[Languages]
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Een snelkoppeling op het bureaublad maken"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "NOTICE.txt"; DestDir: "{app}"; DestName: "Dokany-NOTICE.txt"; Flags: ignoreversion
; Embedded in this EXE and only extracted when the prerequisite is missing.
Source: "{#DependencyDirectory}\DokanSetup.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\UGREEN Remote Drive"; Filename: "{app}\UGREENRemoteDrive.exe"
Name: "{autodesktop}\UGREEN Remote Drive"; Filename: "{app}\UGREENRemoteDrive.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\UGREENRemoteDrive.exe"; Description: "UGREEN Remote Drive starten"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanLaunchApp

[Code]
var
  DokanyRestartRequired: Boolean;

function CompatibleDokanyFile(const FileName: String): Boolean;
var
  VersionMS, VersionLS: Cardinal;
begin
  Result := GetVersionNumbers(FileName, VersionMS, VersionLS);
  if Result then
    { DokanNet 2.3 requires Dokany 2.3 or later in the same major version. }
    Result := (VersionMS >= $00020003) and (VersionMS < $00030000);
end;

function DokanyRemovalPending: Boolean;
var
  DeleteFlag: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64,
    'SYSTEM\CurrentControlSet\Services\Dokan2', 'DeleteFlag', DeleteFlag);
  if Result then Result := DeleteFlag <> 0;
end;

function DokanyReady: Boolean;
begin
  Result := CompatibleDokanyFile(ExpandConstant('{sys}\dokan2.dll')) and
    CompatibleDokanyFile(ExpandConstant('{sys}\drivers\dokan2.sys')) and
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\Dokan2') and
    not DokanyRemovalPending;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  LogDirectory, LogPath: String;
begin
  Result := '';
  if DokanyRemovalPending then
  begin
    NeedsRestart := True;
    Result := 'Start Windows opnieuw op om de eerdere Dokany-verwijdering af te ronden. Voer daarna deze installer opnieuw uit.';
    Exit;
  end;
  if DokanyRestartRequired then Exit;
  if DokanyReady then
  begin
    Log('Compatible Dokany runtime and driver already installed; skipping bundled setup.');
    Exit;
  end;
  { Upstream explicitly refuses an in-place driver upgrade. Never remove a
    shared filesystem driver that another application may still be using. }
  if RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\Dokan2') or
     FileExists(ExpandConstant('{sys}\dokan2.dll')) then
  begin
    Result := 'Er is een oudere of onvolledige Dokany-installatie aanwezig. Verwijder Dokany via Windows Instellingen > Apps, start Windows opnieuw op en voer deze installer opnieuw uit. Andere apps kunnen Dokany ook gebruiken.';
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Dokany installeren voor de Windows-schijf...';
  ExtractTemporaryFile('DokanSetup.exe');
  LogDirectory := ExpandConstant('{commonappdata}\UGREEN Remote Drive\InstallerLogs');
  if not ForceDirectories(LogDirectory) then
  begin
    Result := 'De logmap voor de Dokany-installatie kon niet worden gemaakt.';
    Exit;
  end;
  LogPath := LogDirectory + '\Dokany-setup.log';
  Log('Installing bundled official Dokany 2.3.1.1000.');
  if not Exec(ExpandConstant('{tmp}\DokanSetup.exe'),
    '/install /quiet /norestart /log "' + LogPath + '"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Dokany kon niet starten: ' + SysErrorMessage(ResultCode);
    Exit;
  end;
  Log(Format('Bundled Dokany setup exit code: %d', [ResultCode]));
  if (ResultCode = 3010) or (ResultCode = 1641) then
    DokanyRestartRequired := True
  else if ResultCode <> 0 then
  begin
    Result := Format('Dokany-installatie mislukt (code %d). Zie %s', [ResultCode, LogPath]);
    Exit;
  end;
  if not DokanyRestartRequired and not DokanyReady then
    Result := 'Dokany is nog niet beschikbaar. Start Windows opnieuw op en voer deze installer opnieuw uit. Zie ' + LogPath;
end;

function NeedRestart: Boolean;
begin
  Result := DokanyRestartRequired;
end;

function CanLaunchApp: Boolean;
begin
  Result := not DokanyRestartRequired and DokanyReady;
end;
