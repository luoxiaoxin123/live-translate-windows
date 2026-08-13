#ifndef MyAppVersion
#define MyAppVersion "0.1.0"
#endif

#define MyAppName "实时翻译"
#define MyAppNameEn "Live Translate"
#define MyAppPublisher "luoxiaoxin123"
#define MyAppURL "https://github.com/luoxiaoxin123/live-translate-windows"
#define MyAppExeName "LiveTranslate.exe"

[Setup]
AppId={{8F3C2A91-6B47-4E1D-9C5A-2D8E4F0B7A16}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\LiveTranslate
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..
OutputBaseFilename=LiveTranslate-Setup-x64
SetupIconFile=..\src\LiveTranslate.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no
DisableWelcomePage=no
UsedUserAreasWarning=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppNameEn}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动实时翻译"; Flags: nowait postinstall skipifsilent; Languages: chinesesimplified
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Live Translate now"; Flags: nowait postinstall skipifsilent; Languages: english
