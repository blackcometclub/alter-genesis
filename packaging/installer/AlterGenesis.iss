#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef SourceDir
  #error SourceDir must point to the published application directory.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define MyAppName "ALTER GENESIS"
#define MyAppPublisher "TINMOON"
#define MyAppExeName "AlterGenesis.exe"

[Setup]
AppId={{42EE82D0-17F8-472B-B9E0-C7FC191E4327}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\TINMOON\ALTER GENESIS
DefaultGroupName=ALTER GENESIS
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=ALTER-GENESIS-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
english.RemoveUserData=Also remove ALTER GENESIS settings and Google connection data?%n%nChoose No to keep them for a later reinstall.
japanese.RemoveUserData=ALTER GENESIS の設定と Google 接続情報も削除しますか？%n%nあとで入れ直す場合に残すなら「いいえ」を選んでください。

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ALTER GENESIS"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,ALTER GENESIS}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDirectory: String;
begin
  if CurUninstallStep = usUninstall then
    DeleteFile(ExpandConstant('{userstartup}\ALTER GENESIS.lnk'));

  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    DataDirectory := ExpandConstant('{localappdata}\TINMOON\AlterGenesis.NotificationProbe');
    if SuppressibleMsgBox(ExpandConstant('{cm:RemoveUserData}'), mbConfirmation, MB_YESNO, IDNO) = IDYES then
      DelTree(DataDirectory, True, True, True);
  end;
end;
