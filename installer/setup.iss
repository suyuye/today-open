; 今日营业中 v1.5.5 安装包脚本
; 使用 Inno Setup 6 编译

#define MyAppName "今日营业中"
#define MyAppVersion "1.5.5"
#define MyAppPublisher "苏羽野"
#define MyAppExeName "今日营业中.exe"
#define MyAppDataDir "今日营业中"

[Setup]
; 唯一标识（一旦发布不要修改，否则升级时会变成两个独立软件）
AppId={{B7A1F3E2-4D5C-4A8E-9F1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/yourusername/today-open
AppSupportURL=https://github.com/yourusername/today-open/issues
AppUpdatesURL=https://github.com/yourusername/today-open/releases

; 安装路径
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; 输出
OutputDir=D:\PJ\Note\release
OutputBaseFilename=今日营业中_v1.5.5_安装包
SetupIconFile=D:\PJ\Note\src\Note.App\Assets\app.ico

; 压缩
Compression=lzma2
SolidCompression=yes

; 安装向导风格
WizardStyle=modern

; 64 位安装
ArchitecturesInstallIn64BitMode=x64

; 权限：默认不需要管理员（安装到用户目录），安装到 Program Files 时自动提权
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 卸载信息
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; 文件版本信息
VersionInfoVersion=1.5.5.0
VersionInfoCompany=苏羽野
VersionInfoDescription=今日营业中 - 桌面便签软件
VersionInfoCopyright=Copyright (C) 2026 苏羽野

; 安装包只允许运行一次
AllowNoIcons=yes

[Languages]
; 中文语言文件（如果 Inno Setup 安装目录下没有 Languages\ChineseSimplified.isl，请注释掉下面这行，使用默认英文界面）
; Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
; 主程序
Source: "D:\PJ\Note\publish\Note.App.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion
; 配置文件
Source: "D:\PJ\Note\publish\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; 开始菜单
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
; 桌面快捷方式（可选）
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后可选立即运行
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前检查软件是否运行，如果运行则提示关闭
Filename: "{cmd}"; Parameters: "/c taskkill /F /IM ""{#MyAppExeName}"" 2>nul"; Flags: runhidden

[Registry]
; 卸载时删除开机自启注册表项（如果用户开启了开机自启）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TodayOpen"; Flags: deletevalue uninsdeletevalue

[UninstallDelete]
; 卸载时删除软件注册表项（如果有）
Type: filesandordirs; Name: "{userappdata}\{#MyAppDataDir}"
; 注意：上面这行默认被注释，卸载时不删除用户数据。
; 如果需要卸载时同时删除用户数据，请取消下面这行的注释：
; Type: filesandordirs; Name: "{userappdata}\{#MyAppDataDir}"

[Code]
{
  卸载前检查软件是否正在运行，如果运行则提示用户关闭
}
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  
  { 检查软件是否正在运行 }
  if Exec('cmd', '/c tasklist /FI "IMAGENAME eq ' + '{#MyAppExeName}' + '" 2>nul | find /i "' + '{#MyAppExeName}' + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      if MsgBox('检测到「今日营业中」正在运行。' + #13#10 + #13#10 + '请先关闭软件后再继续卸载。' + #13#10 + #13#10 + '是否现在关闭软件并继续卸载？', mbConfirmation, MB_YESNO) = IDYES then
      begin
        { 尝试关闭软件 }
        Exec('taskkill', '/F /IM "' + '{#MyAppExeName}' + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Sleep(1000);
      end
      else
      begin
        Result := False;
      end;
    end;
  end;
end;
