#define AppName "云帆多平台发布助手"
#define AppExe "YunfanPlatformPublisher.exe"

[Setup]
AppId={{A8D033F1-8487-4BD4-A129-B51651E9F2BF}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\YunfanPlatformPublisher
DefaultGroupName={#AppName}
DisableProgramGroupPage=auto
OutputDir={#OutputDir}
OutputBaseFilename=YunfanPlatformPublisher-Setup-{#AppVersion}
SetupIconFile={#AppIconFile}
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent
