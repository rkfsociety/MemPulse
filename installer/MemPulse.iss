#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "MemPulse"
#define AppPublisher "Разработчики MemPulse"
#define AppExeName "MemPulse.exe"

[Setup]
AppId={{CA4E1937-68E0-4468-A822-209EE88C4C3F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/rkfsociety/MemPulse
AppSupportURL=https://github.com/rkfsociety/MemPulse/issues
AppUpdatesURL=https://github.com/rkfsociety/MemPulse/releases/latest
DefaultDirName={localappdata}\Programs\MemPulse
DefaultGroupName=MemPulse
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\MemPulse\Assets\MemPulse.ico
OutputDir=..\dist
OutputBaseFilename=MemPulse-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\publish\MemPulse.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\MemPulse.Helper.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MemPulse"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\MemPulse"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Запустить MemPulse"; Flags: nowait postinstall skipifsilent
