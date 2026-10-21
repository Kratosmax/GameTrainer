#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

[Setup]
; AppId 决定"同一程序的不同版本视为同一应用"。一旦发布不可更改，
; 否则新版本会被当成另一个程序，出现双份安装。
AppId={{8F3C1D42-7A6E-4B91-9C2D-5E8B4A7F1D36}
AppName=GameTrainer
AppVersion={#AppVersion}
AppVerName=GameTrainer {#AppVersion}
AppPublisher=Kratosmax
AppPublisherURL=https://github.com/Kratosmax/GameTrainer
AppSupportURL=https://github.com/Kratosmax/GameTrainer/issues
AppUpdatesURL=https://github.com/Kratosmax/GameTrainer/releases

; 装到用户目录，不需要管理员权限
DefaultDirName={localappdata}\Programs\GameTrainer
DefaultGroupName=GameTrainer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
UninstallDisplayIcon={app}\GameTrainer.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
VersionInfoVersion={#AppVersion}.0
; 安装包不签名时给出提示而非静默；正式发布应配代码签名证书
AllowNoIcons=yes

[Languages]
Name: "chinese"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GameTrainer"; Filename: "{app}\GameTrainer.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 GameTrainer"; Filename: "{uninstallexe}"
Name: "{userdesktop}\GameTrainer"; Filename: "{app}\GameTrainer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Run]
Filename: "{app}\GameTrainer.exe"; Description: "启动 GameTrainer"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 运行期产物：审计日志与快照。卸载时一并清理，避免残留误导后续排查。
Type: files; Name: "{app}\audit.log"
Type: filesandordirs; Name: "{app}\snapshots"
; 更新事务残留（正常情况下不存在，异常退出时可能有）
Type: filesandordirs; Name: "{app}\.gt-update"

#ifdef RequireDesktopRuntime
[Code]
function HasDesktopRuntime8: Boolean;
var
  FindRec: TFindRec;
  RuntimeRoot: String;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*');
  if FindFirst(RuntimeRoot, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDesktopRuntime8;
  if not Result then
  begin
    if MsgBox('GameTrainer Lite 需要 .NET 8 Desktop Runtime (x64)。是否打开微软官方下载页面？',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime',
        '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
#endif
