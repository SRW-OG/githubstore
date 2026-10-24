#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir is required
#endif
#ifndef IconPath
  #define IconPath "..\src\GitHubStore.App\Assets\githubstore.ico"
#endif

; AppVersion 必须与 build-installer.ps1 的 -Version 完全相同：它同时决定
; OutputBaseFilename、程序集版本（-p:AppVersion=）与客户端自更新期待的资产名
; GitHubStore-Setup-<AppVersion>-win-x64.exe。三者不一致时自更新会找不到官方安装包
; 并如实拒绝，而不是去猜一个相近的文件名。
;
; CloseApplications=yes 是自更新链的一环：客户端只启动安装包，由安装器负责关闭并替换
; 正在运行的应用；客户端从不覆盖自己正在运行的文件。

[Setup]
AppId={{739F23F9-0C57-4938-A740-54A455E166DA}
AppName=GitHubStore
AppVersion={#AppVersion}
AppVerName=GitHubStore {#AppVersion}
AppPublisher=GitHubStore
DefaultDirName={localappdata}\Programs\GitHubStore
DefaultGroupName=GitHubStore
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#InstallerOutputDir}
OutputBaseFilename=GitHubStore-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#IconPath}
UninstallDisplayIcon={app}\githubstore-ui.exe
UninstallDisplayName=GitHubStore
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=GitHubStore
VersionInfoDescription=GitHubStore Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\GitHubStore"; Filename: "{app}\githubstore-ui.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\GitHubStore"; Filename: "{app}\githubstore-ui.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\githubstore-ui.exe"; Description: "Launch GitHubStore"; Flags: nowait postinstall skipifsilent

; Deliberately no [UninstallDelete]: user settings, downloads, installed applications,
; and data under LocalAppData\GitHubStore must survive removal of the client.
