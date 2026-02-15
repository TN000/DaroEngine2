#define MyAppName "DaroEngine2"
#define MyAppVersion "0.1.1"
#define MyAppPublisher "DaroEngine2 Contributors"
#define MyAppURL "https://github.com/TN000/DaroEngine2"
#define MyAppExeName "DaroDesigner.exe"
#define BuildDir "..\release-tmp\DaroEngine2-v0.1.1"

[Setup]
AppId={{E8F3A2B1-5C7D-4E9F-B6A8-3D2C1F0E9B7A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=..\release-tmp
OutputBaseFilename=DaroEngine2-v{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BuildDir}\DaroDesigner.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\DaroDesigner.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\DaroDesigner.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\DaroDesigner.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\DaroEngine.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\Dapper.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\Microsoft.Data.Sqlite.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\SQLitePCLRaw.batteries_v2.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\SQLitePCLRaw.core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\SQLitePCLRaw.provider.e_sqlite3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\avcodec-61.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\avdevice-61.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\avfilter-10.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\avformat-61.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\avutil-59.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\swresample-5.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\swscale-8.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\runtimes\win-x64\native\e_sqlite3.dll"; DestDir: "{app}\runtimes\win-x64\native"; Flags: ignoreversion
; GraphicsMiddleware (self-contained)
Source: "{#BuildDir}\Middleware\GraphicsMiddleware.exe"; DestDir: "{app}\Middleware"; Flags: ignoreversion
Source: "{#BuildDir}\Middleware\appsettings.json"; DestDir: "{app}\Middleware"; Flags: ignoreversion
Source: "{#BuildDir}\Middleware\appsettings.Production.json"; DestDir: "{app}\Middleware"; Flags: ignoreversion
Source: "{#BuildDir}\Middleware\wwwroot\*"; DestDir: "{app}\Middleware\wwwroot"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNet10Installed(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('dotnet', '--list-runtimes', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if Result then
  begin
    // Check if .NET 10 runtime is available by looking for dotnet command
    Result := Exec('cmd', '/c dotnet --list-runtimes | findstr "Microsoft.WindowsDesktop.App 10."', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet10Installed() then
  begin
    if MsgBox('.NET 10 Desktop Runtime is required but not installed.' + #13#10 + #13#10 +
             'Would you like to download it now?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
      MsgBox('Please install the .NET 10 Desktop Runtime and then run this installer again.', mbInformation, MB_OK);
      Result := False;
    end
    else
    begin
      Result := MsgBox('DaroEngine2 may not work without .NET 10 Desktop Runtime.' + #13#10 + #13#10 +
                       'Continue installation anyway?', mbConfirmation, MB_YESNO) = IDYES;
    end;
  end;
end;
