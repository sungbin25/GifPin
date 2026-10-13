; GifPin 설치 프로그램 (Inno Setup 6)
; CI에서: ISCC /DAppVersion=0.2.0 /DSourceDir=<publish 폴더> installer\GifPin.iss

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{8C4F1E52-6A1D-4E7B-9C0E-3B2A9F5D7E61}
AppName=GifPin
AppVersion={#AppVersion}
AppVerName=GifPin {#AppVersion}
AppPublisher=GifPin
DefaultDirName={localappdata}\Programs\GifPin
DefaultGroupName=GifPin
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputBaseFilename=GifPin-Setup-{#AppVersion}
SetupIconFile=..\src\GifPin\Assets\GifPin.ico
UninstallDisplayIcon={app}\GifPin.exe
UninstallDisplayName=GifPin
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no

[Languages]
#if FileExists(AddBackslash(CompilerPath) + "Languages\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; GroupDescription: "추가 작업:"
Name: "autostart"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GifPin"; Filename: "{app}\GifPin.exe"
Name: "{autodesktop}\GifPin"; Filename: "{app}\GifPin.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GifPin"; ValueData: """{app}\GifPin.exe"" --tray"; Tasks: autostart
; 앱 설정에서 자동 실행을 켰더라도 제거할 때 함께 지움
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GifPin"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\GifPin.exe"; Description: "GifPin 실행"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM GifPin.exe"; Flags: runhidden; RunOnceId: "KillGifPin"
