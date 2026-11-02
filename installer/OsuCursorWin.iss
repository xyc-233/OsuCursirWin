#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef PayloadDir
  #error PayloadDir must point to the self-contained UIAccess build.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{C83B9967-D621-4CD9-8F8E-514A78592445}
AppName=osu! Cursor
AppVersion={#AppVersion}
AppPublisher=xyc-233
AppPublisherURL=https://github.com/xyc-233/OsuCursirWin
AppSupportURL=https://github.com/xyc-233/OsuCursirWin/issues
DefaultDirName={code:GetInstallDir}
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputDir={#OutputDir}
OutputBaseFilename=OsuCursorWin-Setup-{#AppVersion}-x64
SetupIconFile=..\OsuCursorWin\1.ico
UninstallDisplayIcon={app}\OsuCursorWin.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=no
RestartApplications=no
SetupLogging=yes
Uninstallable=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=osu! Cursor Installer

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.DesktopShortcut=创建桌面快捷方式
chinesesimp.LaunchProgram=打开 osu! Cursor 设置
chinesesimp.PreparingSecurity=正在配置开始菜单覆盖并验证程序签名……
chinesesimp.PrepareFailed=无法完成开始菜单覆盖配置。当前安装尚未替换。详情：%1
chinesesimp.CloseFailed=程序未能正常退出。请从托盘退出 osu! Cursor 后重试。
chinesesimp.RollbackFailed=安装准备的清理未完成，详情见安装日志。
english.DesktopShortcut=Create a desktop shortcut
english.LaunchProgram=Open osu! Cursor settings
english.PreparingSecurity=Configuring Start menu coverage and verifying the application signature...
english.PrepareFailed=Start menu coverage could not be configured. The current installation has not been replaced. Details: %1
english.CloseFailed=The application did not exit cleanly. Exit osu! Cursor from its tray menu and try again.
english.RollbackFailed=Installation preparation cleanup did not complete. See the setup log for details.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\OsuCursorWin.exe"; Flags: dontcopy
Source: "configure-uiaccess.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "..\scripts\read-manifest.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "..\scripts\stop-running.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "{tmp}\OsuCursorWin.exe"; DestDir: "{app}"; Flags: external ignoreversion
Source: "{tmp}\uiaccess.json"; DestDir: "{app}"; Flags: external ignoreversion

[Icons]
Name: "{commonprograms}\osu! Cursor"; Filename: "{app}\OsuCursorWin.exe"; Parameters: "--settings"
Name: "{commondesktop}\osu! Cursor"; Filename: "{app}\OsuCursorWin.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OsuCursorWin.exe"; Parameters: "--settings"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent runasoriginaluser unchecked

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\setup\configure-uiaccess.ps1"" -Mode Uninstall -StatePath ""{app}\uiaccess.json"" -InstallDir ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveUIAccessConfiguration"

[Code]
var
  Prepared: Boolean;
  Installed: Boolean;

function GetInstallDir(Param: String): String;
var
  Directory: String;
begin
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion',
    'ProgramW6432Dir', Directory) or (Directory = '') then
    Directory := ExpandConstant('{autopf}');
  Result := AddBackslash(Directory) + 'OsuCursorWin';
end;

function RunPowerShell(const Parameters: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass ' + Parameters,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  Log(Format('PowerShell action completed: success=%s, exit=%d', [IntToStr(Ord(Result)), ResultCode]));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  LogPath: String;
begin
  Result := '';
  if not Prepared then
  begin
    ExtractTemporaryFile('OsuCursorWin.exe');
    ExtractTemporaryFile('configure-uiaccess.ps1');
    ExtractTemporaryFile('read-manifest.ps1');
    ExtractTemporaryFile('stop-running.ps1');
    WizardForm.PreparingLabel.Caption := CustomMessage('PreparingSecurity');
    LogPath := ExpandConstant('{tmp}\uiaccess-install.log');
    if not RunPowerShell('-File "' + ExpandConstant('{tmp}\configure-uiaccess.ps1') +
      '" -Mode Prepare -ExePath "' + ExpandConstant('{tmp}\OsuCursorWin.exe') +
      '" -StatePath "' + ExpandConstant('{tmp}\uiaccess.json') +
      '" -InstallDir "' + ExpandConstant('{app}') + '" -LogPath "' + LogPath + '"') then
    begin
      Result := FmtMessage(CustomMessage('PrepareFailed'), [LogPath]);
      Exit;
    end;
    Prepared := True;
  end;
  if not RunPowerShell('-File "' + ExpandConstant('{tmp}\stop-running.ps1') + '"') then
    Result := CustomMessage('CloseFailed');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
    Installed := True;
end;

procedure DeinitializeSetup;
begin
  if Prepared and not Installed then
  begin
    if not RunPowerShell('-File "' + ExpandConstant('{tmp}\configure-uiaccess.ps1') +
      '" -Mode Rollback -StatePath "' + ExpandConstant('{tmp}\uiaccess.json') +
      '" -InstallDir "' + ExpandConstant('{app}') + '"') then
      Log(CustomMessage('RollbackFailed'));
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := RunPowerShell('-File "' + ExpandConstant('{app}\setup\stop-running.ps1') + '"');
  if not Result then
    MsgBox(CustomMessage('CloseFailed'), mbError, MB_OK);
end;
