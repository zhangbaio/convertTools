#define AppName "TikTok 短剧助手"

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

#ifndef PublishDir
#error PublishDir is required. Use package-tiktok-installer.ps1.
#endif

#ifndef OutputDir
#define OutputDir "."
#endif

[Setup]
AppId={{8F617FD8-AD88-4A82-8231-6D180956EB7F}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\TikTokShortDramaUploader
DefaultGroupName={#AppName}
DisableDirPage=no
DisableProgramGroupPage=auto
OutputDir={#OutputDir}
OutputBaseFilename=TikTokShortDramaUploader-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
CloseApplications=yes
SetupLogging=yes
UninstallDisplayIcon={app}\TikTokPublisher.Desktop.exe
#ifdef AppIconFile
SetupIconFile={#AppIconFile}
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"; Flags: unchecked
Name: "resetdata"; Description: "重置本地数据（保留上传记录）"; GroupDescription: "本地数据:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef WebView2Installer
Source: "{#WebView2Installer}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\TikTokPublisher.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\TikTokPublisher.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
#ifdef WebView2Installer
; 仅在本机缺少运行时时安装；用 shellexec 触发安装器自带的提权清单（setup 为 lowest，
; 直接 CreateProcess 无法完成 per-machine 安装，会静默失败导致内置浏览器黑屏）。
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 Runtime（需管理员授权）..."; Flags: shellexec waituntilterminated; Check: NeedsWebView2
#endif
Filename: "{app}\TikTokPublisher.Desktop.exe"; Parameters: "--reset-installer-data-secrets"; StatusMsg: "正在重置敏感配置..."; Flags: waituntilterminated runhidden; Check: ShouldResetData
Filename: "{app}\TikTokPublisher.Desktop.exe"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function HasWebView2Runtime(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM64, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

function NeedsWebView2(): Boolean;
begin
  Result := not HasWebView2Runtime();
end;

function ShouldResetData(): Boolean;
begin
  Result := WizardIsTaskSelected('resetdata');
end;

function ShouldKeepLocalDataEntry(Name: String): Boolean;
begin
  Result :=
    (CompareText(Name, 'app.db') = 0) or
    (CompareText(Name, 'app.db-wal') = 0) or
    (CompareText(Name, 'app.db-shm') = 0) or
    (CompareText(Name, 'reports') = 0);
end;

procedure ResetLocalDataKeepUploadRecords(DataDir: String);
var
  FindRec: TFindRec;
  EntryName: String;
  EntryPath: String;
begin
  if not DirExists(DataDir) then
    Exit;

  if FindFirst(AddBackslash(DataDir) + '*', FindRec) then
  begin
    try
      repeat
        EntryName := FindRec.Name;
        if (EntryName <> '.') and (EntryName <> '..') and not ShouldKeepLocalDataEntry(EntryName) then
        begin
          EntryPath := AddBackslash(DataDir) + EntryName;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            DelTree(EntryPath, True, True, True)
          else
            DeleteFile(EntryPath);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: String;
begin
  if (CurStep = ssInstall) and WizardIsTaskSelected('resetdata') then
  begin
    DataDir := GetEnv('USERPROFILE');
    if DataDir <> '' then
    begin
      DataDir := AddBackslash(DataDir) + '.tiktok_publisher';
      if DirExists(DataDir) then
      begin
        ResetLocalDataKeepUploadRecords(DataDir);
      end;
    end;
  end;
end;
