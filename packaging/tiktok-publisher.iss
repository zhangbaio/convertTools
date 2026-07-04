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
Name: "resetdata"; Description: "重置本地数据（删除当前用户的 .tiktok_publisher）"; GroupDescription: "本地数据:"; Flags: unchecked

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
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 Runtime..."; Flags: waituntilterminated; Check: NeedsWebView2
#endif
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
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
