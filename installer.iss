; SVNFileBox Inno Setup Script
; 生成 Windows 安装包 (EXE)

#define MyAppName "SVNFileBox"
#define MyAppVersion "2.3.1"
#define MyAppPublisher "vxling"
#define MyAppExeName "SVNFileBox.exe"

[Setup]
AppId={{8E4F1C2A-3B7D-4E9F-A1B2-5C6D7E8F9A0B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=
OutputDir=.
OutputBaseFilename=SVNFileBox-Setup
SetupIconFile=src\Assets\Icons\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "installer\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Uninstall]
; 先强制杀掉正在运行的程序（防止程序驻留后台导致卸载失败）
Exec('cmd', '/c taskkill /F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
; 删除安装目录（程序文件）
DirExists: "{app}"; Flags: removedirectories
; 删除 %APPDATA%\SVNFileBox（配置信息）
Delete: "{userappdata}\{#MyAppName}\*"
RMDir: "{userappdata}\{#MyAppName}"
