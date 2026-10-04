; Flux 安装脚本（Inno Setup 6+）
; 编译：ISCC.exe Flux.iss
;
; 注意：AssemblyName 一旦改名（SmartUnzip -> Flux），本文件里所有文件名
;       都必须同步（MyAppExeName / .dll / .msix / .cer），否则安装完
;       右键菜单会因为找不到 Flux.exe 而失效。

#define MyAppName "Flux"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Flux"
#define MyAppExeName "Flux.exe"
; 换了新的 AppId：旧版 SmartUnzip 用的是 8F2A9C31-...，
; 不复用是为了让新旧两版在"应用和功能"里各自独立、可分别卸载。
#define MyAppId "{{C4E7B1A9-6D35-4F82-B0E7-2A9C5D3F8142}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Flux
DefaultGroupName=Flux
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Flux_Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 需要管理员：把自签证书导入「受信任的人」(TrustedPeople)、
; 清理可能劫持一级菜单的 HKLM\SOFTWARE\Classes\CLSID\... 注册项。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\dist\Flux\LICENSE.txt
SetupIconFile=..\dist\Flux\Flux.ico

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.AdditionalIcons=附加图标：
chinesesimplified.ContextMenuTask=添加「使用 Flux 解压」右键菜单（推荐）
chinesesimplified.LaunchApp=运行 Flux
chinesesimplified.Missing7Zip=提示：未检测到 7-Zip。%n%nFlux 需要 7-Zip 作为解压引擎，请从 https://www.7-zip.org/ 下载安装。%n%n是否稍后自行安装？
english.CreateDesktopIcon=Create a desktop shortcut
english.AdditionalIcons=Additional icons:
english.ContextMenuTask=Add "Extract with Flux" to the context menu (recommended)
english.LaunchApp=Launch Flux
english.Missing7Zip=Note: 7-Zip was not detected.%n%nFlux requires 7-Zip as its extraction engine. Please install it from https://www.7-zip.org/.%n%nContinue anyway?

[Tasks]
; 桌面图标任务保留（供静默安装时显式控制），但 [Icons] 里
; 桌面快捷方式是**无条件创建**的，见下方说明。
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "contextmenu"; Description: "{cm:ContextMenuTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\dist\Flux\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\Flux\Flux.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\Flux\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\Flux\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\Flux\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
; --- Windows 11 一级右键菜单所需（缺一不可）---------------------------------
; ShellExt.dll : IExplorerCommand 的 COM 服务器，由 dllhost.exe 以 SurrogateServer 加载
; .msix        : 稀疏包（只含清单 + 图标），安装时配合 -ExternalLocation 指向 {app}
; -Sparse.cer  : 自签名证书公钥，需导入「受信任的人」后才可安装稀疏包
Source: "..\dist\Flux\Flux.ShellExt.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\Flux\Flux.ShellIntegration.msix"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\Flux\Flux-Sparse.cer"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; 开始菜单
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Flux.ico"
; 桌面快捷方式：**无条件创建**（不加 Tasks 限定）。
; 需求就是"装完桌面上要有它"，所以不给它被任务勾选开关掉的机会。
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Flux.ico"

[Registry]
; ============================================================================
; 右键菜单 —— 传统注册表方式（HKLM，仅用于 Windows 10）
;
;  ⚠ 两种菜单机制：
;    * Windows 11 **一级菜单**：由稀疏包（Sparse Package）实现，
;      在 [Run] 段执行 `Flux.exe --register-shell` 完成。
;    * 下面这些 HKLM\SOFTWARE\Classes\*\shell 项是**传统方式**，
;      在 Windows 11 上只会出现在「显示更多选项」（二级菜单）里。
;
;  因此这里加了 `Check: IsWin10OrOlder`：
;    - Windows 10 及更早：写传统项（Win10 用不了稀疏包的一级菜单）
;    - Windows 11：跳过，避免出现"一级 + 二级"两个重复入口
;
;  用 HKLM 而不是 HKCU：本安装包以管理员模式运行，写 HKCU 会落到
;  管理员账户名下而非当前用户；写 HKLM 则对所有用户生效。
; ============================================================================
Root: HKLM; Subkey: "SOFTWARE\Classes\*\shell\Flux"; ValueType: string; ValueName: ""; ValueData: "使用 Flux 解压"; Flags: uninsdeletekey; Tasks: contextmenu; Check: IsWin10OrOlder
Root: HKLM; Subkey: "SOFTWARE\Classes\*\shell\Flux"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu; Check: IsWin10OrOlder
Root: HKLM; Subkey: "SOFTWARE\Classes\*\shell\Flux\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --extract ""%1"""; Tasks: contextmenu; Check: IsWin10OrOlder
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\Flux"; ValueType: string; ValueName: ""; ValueData: "使用 Flux 解压"; Flags: uninsdeletekey; Tasks: contextmenu; Check: IsWin10OrOlder
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\Flux"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu; Check: IsWin10OrOlder
Root: HKLM; Subkey: "SOFTWARE\Classes\Directory\shell\Flux\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --extract ""%1"""; Tasks: contextmenu; Check: IsWin10OrOlder

[Run]
; 安装 Windows 11 一级右键菜单（稀疏包）。需要管理员权限写 HKLM / 导入证书。
; 该步骤幂等，重复执行安全。
;
; 必须带 --silent：否则 Flux.exe 会弹一个结果对话框、
; 等用户点"确定"才退出，而这里用了 waituntilterminated，安装会一直卡住。
;
; ★ 这里**故意不加 Tasks: contextmenu**：
;   contextmenu 任务带 checkedonce，静默重装时 Inno 会沿用上次勾选状态，
;   一旦某次没勾上，后续静默安装就再也不会注册一级菜单（菜单凭空消失）。
;   一级菜单是这个扩展存在的唯一理由，所以无条件执行。
;   contextmenu 任务只保留给 [Registry] 段的 Win10 传统菜单项使用。
Filename: "{app}\{#MyAppExeName}"; Parameters: "--register-shell --silent"; StatusMsg: "正在注册 Windows 11 一级右键菜单..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前注销稀疏包，否则「使用 Flux 解压」会残留在右键菜单里。
; 同样必须带 --silent，理由见上。
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister-shell --silent"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterShellMenu"

[Code]
function Is7ZipInstalled(): Boolean;
var
  Paths: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Paths := [
    ExpandConstant('{pf}\7-Zip\7z.exe'),
    ExpandConstant('{pf32}\7-Zip\7z.exe'),
    ExpandConstant('{localappdata}\Programs\7-Zip\7z.exe'),
    'C:\7-Zip\7z.exe'
  ];
  for I := 0 to GetArrayLength(Paths) - 1 do
  begin
    if FileExists(Paths[I]) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not Is7ZipInstalled() then
  begin
    if MsgBox(CustomMessage('Missing7Zip'), mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

{ Windows 10 或更早？

  GetWindowsVersion 的布局：高 8 位 = major，次 8 位 = minor，低 16 位 = build。
  Windows 11 的 major 仍然是 10（内核版本没变），所以不能只看 major，
  必须用 build 号区分：Windows 11 = build 22000 起。

  用途：传统 HKLM\SOFTWARE\Classes\*\shell 菜单项只在 Win10 及更早安装，
        避免 Win11 上出现"一级 + 二级"两个重复的右键入口。 }
function IsWin10OrOlder(): Boolean;
var
  Build: Cardinal;
begin
  Build := GetWindowsVersion and $FFFF;
  Result := Build < 22000;
end;
