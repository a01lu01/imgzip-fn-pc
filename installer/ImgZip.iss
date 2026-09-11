#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{3A602924-B953-452E-ACD7-2E396A74ED88}
AppName=ImgZip
AppVersion={#AppVersion}
AppPublisher=ImgZip contributors
DefaultDirName={localappdata}\Programs\ImgZip
DefaultGroupName=ImgZip
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=ImgZip-{#AppVersion}-win-x64-setup
#ifexist "..\icon\compress-icon.ico"
SetupIconFile=..\icon\compress-icon.ico
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ImgZip.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "contextmenu"; Description: "添加资源管理器右键菜单（显示更多选项）"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\ImgZip"; Filename: "{app}\ImgZip.exe"

[Registry]
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ImgZipWinUI"; ValueType: string; ValueName: ""; ValueData: "使用 ImgZip 压缩图片"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ImgZipWinUI"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\ImgZip.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ImgZipWinUI\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ImgZip.exe"" --path ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\ImgZipWinUI"; ValueType: string; ValueName: ""; ValueData: "使用 ImgZip 压缩图片"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\ImgZipWinUI"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\ImgZip.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\ImgZipWinUI\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ImgZip.exe"" --path ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ImgZipWinUI"; ValueType: string; ValueName: ""; ValueData: "使用 ImgZip 压缩图片"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ImgZipWinUI"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\ImgZip.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\ImgZipWinUI\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ImgZip.exe"" --path ""%V"""; Tasks: contextmenu

; There is intentionally no deletion of LocalAppData\ImgZip or the legacy ImgZipCompress keys.
