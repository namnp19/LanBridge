#define MyAppName "LanBridge"
#define MyAppVersion "1.0.0"
#define MyAppExeName "LanBridge.Desktop.exe"

[Setup]
AppId={{9FA1E87C-CE83-41E6-9451-1E457470C29A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=LanBridge
DefaultDirName={autopf}\LanBridge
DefaultGroupName=LanBridge
OutputDir=..\artifacts\setup
OutputBaseFilename=LanBridge-Setup-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LanBridge"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\LanBridge"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng trên Desktop"; GroupDescription: "Biểu tượng bổ sung"; Flags: unchecked

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\firewall.ps1"" -Action Install -ExePath ""{app}\{#MyAppExeName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Thiết lập firewall cho mạng riêng..."
Filename: "{app}\{#MyAppExeName}"; Description: "Mở LanBridge"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\firewall.ps1"" -Action Remove"; Flags: runhidden waituntilterminated

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
end;
