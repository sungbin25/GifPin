; GifPin 설치 프로그램 (Inno Setup 6.1 이상)
; CI에서: ISCC /DAppVersion=0.2.0 /DSourceDir=<publish 폴더> installer\GifPin.iss
;
; 설치 프로그램에는 .NET 런타임을 넣지 않은 작은 GifPin.exe가 들어간다.
; 설치할 때 PC에 .NET 8 데스크톱 런타임(x64)이 있는지 확인하고, 없으면 Microsoft에서 내려받아 자동으로 설치한다.
; (포터블 exe는 런타임을 포함하므로 이 과정이 필요 없음)

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

; Microsoft가 관리하는 "최신 .NET 8 데스크톱 런타임" 고정 주소 (보안 업데이트가 반영된 8.0.x로 연결됨)
#define RuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
#define RuntimeFile "windowsdesktop-runtime-8-win-x64.exe"

[Setup]
AppId={{8C4F1E52-6A1D-4E7B-9C0E-3B2A9F5D7E61}
AppName=MotiNook
AppVersion={#AppVersion}
AppVerName=MotiNook {#AppVersion}
AppPublisher=MotiNook
DefaultDirName={localappdata}\Programs\MotiNook
DefaultGroupName=MotiNook
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputBaseFilename=MotiNook-Setup-{#AppVersion}
SetupIconFile=..\src\GifPin\Assets\GifPin.ico
UninstallDisplayIcon={app}\MotiNook.exe
UninstallDisplayName=MotiNook
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
AppMutex=GifPin.SingleInstance.v1
RestartApplications=no
; 설치가 끝나면 탐색기에 "아이콘이 바뀌었음"을 알림 (예전 아이콘이 캐시에 남아 바로가기에 보이는 문제)
ChangesAssociations=yes
MinVersion=10.0.17763
; 언어 선택 창은 항상 영어로 띄움 (목록 첫 번째 = English가 기본 선택). 한국어를 고르면 이후 화면과 앱이 한국어
ShowLanguageDialog=yes
LanguageDetectionMethod=none

[Languages]
#define HasKorean FileExists(AddBackslash(CompilerPath) + "Languages\Korean.isl")
Name: "english"; MessagesFile: "compiler:Default.isl"
#if HasKorean
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif

[CustomMessages]
english.DesktopIcon=Create a desktop shortcut
english.Autostart=Start with Windows
english.AdditionalTasks=Additional tasks:
english.LaunchApp=Launch MotiNook
english.PackType=MotiNook pack
english.RuntimeTitle=Installing .NET 8 Desktop Runtime
english.RuntimeDownloading=MotiNook needs the Microsoft .NET 8 Desktop Runtime. Downloading it from Microsoft…
english.RuntimeInstalling=Installing the .NET 8 Desktop Runtime… (Windows may ask for permission)
english.RuntimeDownloadFailed=Couldn't download the .NET 8 Desktop Runtime:%n%1%n%nCheck your internet connection and run setup again, or install it yourself from https://dotnet.microsoft.com/download/dotnet/8.0 (".NET Desktop Runtime 8", x64).
english.RuntimeCancelled=The .NET 8 Desktop Runtime was not installed because permission was declined. MotiNook can't run without it. Run setup again and allow the permission request.
english.RuntimeFailed=Installing the .NET 8 Desktop Runtime failed (code %1).%n%nInstall it yourself from https://dotnet.microsoft.com/download/dotnet/8.0 (".NET Desktop Runtime 8", x64) and run setup again.
#if HasKorean
korean.DesktopIcon=바탕화면에 바로가기 만들기
korean.Autostart=Windows 시작 시 자동 실행
korean.AdditionalTasks=추가 작업:
korean.LaunchApp=MotiNook 실행
korean.PackType=MotiNook 팩
korean.RuntimeTitle=.NET 8 데스크톱 런타임 설치
korean.RuntimeDownloading=MotiNook을 실행하려면 Microsoft .NET 8 데스크톱 런타임이 필요합니다. Microsoft에서 내려받는 중…
korean.RuntimeInstalling=.NET 8 데스크톱 런타임을 설치하는 중… (Windows에서 권한을 물어볼 수 있습니다)
korean.RuntimeDownloadFailed=.NET 8 데스크톱 런타임을 내려받지 못했습니다:%n%1%n%n인터넷 연결을 확인하고 설치 프로그램을 다시 실행하거나, https://dotnet.microsoft.com/download/dotnet/8.0 에서 ".NET Desktop Runtime 8"(x64)을 직접 설치하세요.
korean.RuntimeCancelled=권한 요청을 거절해서 .NET 8 데스크톱 런타임을 설치하지 못했습니다. 이 런타임이 없으면 MotiNook을 실행할 수 없습니다. 설치 프로그램을 다시 실행하고 권한 요청을 허용하세요.
korean.RuntimeFailed=.NET 8 데스크톱 런타임 설치에 실패했습니다 (코드 %1).%n%nhttps://dotnet.microsoft.com/download/dotnet/8.0 에서 ".NET Desktop Runtime 8"(x64)을 직접 설치한 뒤 설치 프로그램을 다시 실행하세요.
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalTasks}"
Name: "autostart"; Description: "{cm:Autostart}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

[InstallDelete]
; Remove the legacy launcher and shortcuts when upgrading the same AppId. User data stays intact.
Type: files; Name: "{app}\GifPin.exe"
Type: files; Name: "{autoprograms}\GifPin.lnk"
Type: files; Name: "{autodesktop}\GifPin.lnk"
; 예전 버전의 바로가기 아이콘 파일
Type: files; Name: "{app}\GifPin-*.ico"
Type: files; Name: "{app}\GifPinPack-*.ico"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 바로가기 아이콘: 버전마다 파일 이름이 달라서 Windows 아이콘 캐시에 남은 예전 아이콘을 쓰지 않음
Source: "..\src\GifPin\Assets\GifPin.ico"; DestDir: "{app}"; DestName: "GifPin-{#AppVersion}.ico"; Flags: ignoreversion
; .gifpin 팩 파일 아이콘
Source: "..\src\GifPin\Assets\GifPinPack.ico"; DestDir: "{app}"; DestName: "GifPinPack-{#AppVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MotiNook"; Filename: "{app}\MotiNook.exe"; IconFilename: "{app}\GifPin-{#AppVersion}.ico"
Name: "{autodesktop}\MotiNook"; Filename: "{app}\MotiNook.exe"; IconFilename: "{app}\GifPin-{#AppVersion}.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GifPin"; Flags: deletevalue uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MotiNook"; ValueData: """{app}\MotiNook.exe"" --tray"; Check: EnableAutostart
; 설치할 때 고른 언어 → GifPin이 처음 실행될 때(설치할 때마다 한 번) 그 언어로 시작
Root: HKCU; Subkey: "Software\GifPin"; ValueType: string; ValueName: "InstallLanguage"; ValueData: "{language}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\GifPin"; ValueType: string; ValueName: "InstallStamp"; ValueData: "{code:InstallStamp}"
; .gifpin = GifPin 팩: 팩 전용 아이콘, 두 번 누르면 GifPin으로 가져오기 (현재 사용자만)
Root: HKCU; Subkey: "Software\Classes\.gifpin"; ValueType: string; ValueName: ""; ValueData: "GifPin.Pack"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.gifpin\OpenWithProgids"; ValueType: string; ValueName: "GifPin.Pack"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\GifPin.Pack"; ValueType: string; ValueName: ""; ValueData: "{cm:PackType}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\GifPin.Pack\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\GifPinPack-{#AppVersion}.ico"
Root: HKCU; Subkey: "Software\Classes\GifPin.Pack\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\MotiNook.exe"" ""%1"""
; 앱 설정에서 자동 실행을 켰더라도 제거할 때 함께 지움
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MotiNook"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\MotiNook.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM MotiNook.exe"; Flags: runhidden; RunOnceId: "KillGifPin"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  LegacyAutostart: Boolean;

function EnableAutostart(): Boolean;
begin
  Result := LegacyAutostart or WizardIsTaskSelected('autostart');
end;

{ 이번 설치를 구별하는 값 (앱이 같은 설치의 언어를 두 번 적용하지 않게) }
function InstallStamp(Param: String): String;
begin
  Result := GetDateTimeString('yyyymmddhhnnss', #0, #0);
end;

{ 폴더 이름이 "8."로 시작하는 Microsoft.WindowsDesktop.App 버전 폴더가 있는가 }
function HasRuntimeFolder(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\8.*', FindRec) then
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

{ .NET 8 데스크톱 런타임(x64)이 설치되어 있는가: 설치 기록(레지스트리) 또는 기본 설치 폴더로 확인 }
function IsDesktopRuntime8Installed(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  InstallLocation: String;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '8.' then
      begin
        Log('Found .NET Desktop Runtime ' + Names[I] + ' (registry)');
        Result := True;
        Exit;
      end;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', InstallLocation)
     and HasRuntimeFolder(InstallLocation) then
  begin
    Log('Found .NET Desktop Runtime 8 in ' + InstallLocation);
    Result := True;
    Exit;
  end;
  if HasRuntimeFolder(ExpandConstant('{commonpf64}\dotnet')) then
  begin
    Log('Found .NET Desktop Runtime 8 in Program Files');
    Result := True;
  end;
end;

procedure InitializeWizard();
var
  Command: String;
  Approved: TArrayOfByte;
begin
  LegacyAutostart := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GifPin', Command);
  if LegacyAutostart and RegQueryBinaryValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'GifPin', Approved) then
    if GetArrayLength(Approved) > 0 then
      LegacyAutostart := (Approved[0] and 1) = 0;
  DownloadPage := CreateDownloadPage(CustomMessage('RuntimeTitle'), CustomMessage('RuntimeDownloading'), nil);
end;

{ 파일을 복사하기 직전: 런타임이 없으면 내려받아 설치. 실패하면 이유를 보여 주고 설치를 멈춤 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Installer: String;
  ResultCode: Integer;
begin
  Result := '';
  if IsDesktopRuntime8Installed() then
  begin
    Log('.NET 8 Desktop Runtime is already installed; nothing to download');
    Exit;
  end;

  Log('.NET 8 Desktop Runtime not found; downloading ' + '{#RuntimeUrl}');
  try
    if WizardSilent() then
      DownloadTemporaryFile('{#RuntimeUrl}', '{#RuntimeFile}', '', nil)
    else
    begin
      DownloadPage.Clear;
      DownloadPage.Add('{#RuntimeUrl}', '{#RuntimeFile}', '');
      DownloadPage.Show;
      try
        DownloadPage.Download;
      finally
        DownloadPage.Hide;
      end;
    end;
  except
    Result := FmtMessage(CustomMessage('RuntimeDownloadFailed'), [GetExceptionMessage()]);
    Log('Runtime download failed: ' + GetExceptionMessage());
    Exit;
  end;

  Installer := ExpandConstant('{tmp}\{#RuntimeFile}');
  WizardForm.PreparingLabel.Caption := CustomMessage('RuntimeInstalling');
  WizardForm.PreparingLabel.Visible := True;
  { 런타임은 PC 전체에 설치되므로 관리자 권한이 필요 → Windows가 권한을 물어봄 }
  if not ShellExec('runas', Installer, '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Runtime installer could not start, code ' + IntToStr(ResultCode));
    if ResultCode = 1223 then
      Result := CustomMessage('RuntimeCancelled')
    else
      Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ResultCode)]);
    Exit;
  end;
  Log('Runtime installer finished with code ' + IntToStr(ResultCode));
  { 0 성공, 3010/1641 성공(다시 시작 권장 — GifPin 실행에는 필요 없음), 1638 이미 더 새 버전 설치됨 }
  if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1641) and (ResultCode <> 1638) then
  begin
    if ResultCode = 1602 then
      Result := CustomMessage('RuntimeCancelled')
    else
      Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ResultCode)]);
    Exit;
  end;
  if not IsDesktopRuntime8Installed() then
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ResultCode)]);
end;

{ 설치가 끝나면 Windows 아이콘 캐시를 새로 고침 → 바로가기·작업 표시줄에 새 아이콘이 바로 보임 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if Exec(ExpandConstant('{sys}\ie4uinit.exe'), '-show', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Log('Icon cache refreshed (ie4uinit -show, code ' + IntToStr(Code) + ')')
    else
      Log('Icon cache refresh skipped');
  end;
end;
