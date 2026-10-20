; This is a separate installer. It does not include Dokany.
#ifndef PublishDirectory
  #error PublishDirectory is required
#endif
#ifndef NativeVersion
  #define NativeVersion "0.1.0.1"
#endif

[Setup]
AppId={{A97A113C-BA92-4E12-92E9-4E02B157C841}
AppName=UGREEN Remote Drive Native SMB
AppVersion={#NativeVersion}
AppPublisher=LukaStuurman
AppPublisherURL=https://github.com/LukaStuurman/UGREEN-remote-windows-file-service
DefaultDirName={autopf}\UGREEN Remote Drive Native SMB
DefaultGroupName=UGREEN Remote Drive Native SMB
OutputDir=.
OutputBaseFilename=UGREENRemoteDrive-NativeSMB-Setup-win-x64
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\UGREENRemoteDrive.Native.exe
SetupIconFile=..\client\UGREENRemoteDrive\Assets\Techbase.ico

[Languages]
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\UGREEN Remote Drive Native SMB"; Filename: "{app}\UGREENRemoteDrive.Native.exe"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\InstallTailnet.ps1"" -AppRoot ""{app}"""; StatusMsg: "Privé-Tailscale-adapter configureren..."; Flags: runhidden waituntilterminated
Filename: "{app}\UGREENRemoteDrive.Native.exe"; Description: "Native SMB starten"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\InstallTailnet.ps1"" -AppRoot ""{app}"" -Remove"; Flags: runhidden waituntilterminated
