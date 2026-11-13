; NextCommander setup (Inno Setup 7): compiled by build/publish.ps1 -Rid win-x64 / win-arm64.
; The values below come from the command line (/D...): AppVersion, SourceDir (the published program),
; OutputDir, OutputName, Arch (x64 or arm64), IconFile.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
; the same id for every version: a new setup updates the installed program
AppId={{6F1C2E7A-4B3D-4F7E-9C51-2A8D0E6B3F14}
AppName=NextCommander
AppVersion={#AppVersion}
AppVerName=NextCommander {#AppVersion}
AppPublisher=NextCommander
VersionInfoVersion={#AppVersion}
VersionInfoProductName=NextCommander
DefaultDirName={autopf}\NextCommander
DefaultGroupName=NextCommander
DisableProgramGroupPage=yes
; for the current user without administrator rights; "all users" can be chosen (asks for them then)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\NextCommander.exe
UninstallDisplayName=NextCommander
Compression=lzma2/ultra64
SolidCompression=yes
; light or dark like Windows
WizardStyle=modern dynamic
MinVersion=10.0
; a 64-bit setup program (Inno Setup 7) for the 64-bit application
SetupArchitecture=x64
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; a running NextCommander is closed (it saves its settings) before its files are replaced
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
; the other languages of the application (Inno Setup 7 ships Simplified Chinese)
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.ExplorerMenu=Add "Open in NextCommander" to the folder menu of Windows Explorer
turkish.ExplorerMenu=Windows Gezgini'nin klasör menüsüne "NextCommander'da aç" ekle
german.ExplorerMenu="In NextCommander öffnen" zum Ordnermenü des Windows-Explorers hinzufügen
spanish.ExplorerMenu=Añadir "Abrir en NextCommander" al menú de carpetas del Explorador de Windows
french.ExplorerMenu=Ajouter « Ouvrir dans NextCommander » au menu des dossiers de l'Explorateur Windows
chinesesimplified.ExplorerMenu=在 Windows 资源管理器的文件夹菜单中添加“在 NextCommander 中打开”
english.OpenInNextCommander=Open in NextCommander
turkish.OpenInNextCommander=NextCommander'da aç
german.OpenInNextCommander=In NextCommander öffnen
spanish.OpenInNextCommander=Abrir en NextCommander
french.OpenInNextCommander=Ouvrir dans NextCommander
chinesesimplified.OpenInNextCommander=在 NextCommander 中打开
english.Integration=Windows integration:
turkish.Integration=Windows tümleştirmesi:
german.Integration=Windows-Integration:
spanish.Integration=Integración con Windows:
french.Integration=Intégration à Windows :
chinesesimplified.Integration=Windows 集成：

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "explorermenu"; Description: "{cm:ExplorerMenu}"; GroupDescription: "{cm:Integration}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NextCommander"; Filename: "{app}\NextCommander.exe"
Name: "{autodesktop}\NextCommander"; Filename: "{app}\NextCommander.exe"; Tasks: desktopicon

[Registry]
; "Open in NextCommander" on folders and on the empty space of a folder (HKA: the user's or the machine's classes,
; as the setup was run)
Root: HKA; Subkey: "Software\Classes\Directory\shell\NextCommander"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInNextCommander}"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\NextCommander"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\NextCommander.exe"",0"; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\NextCommander\command"; ValueType: string; ValueName: ""; ValueData: """{app}\NextCommander.exe"" ""%1"""; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\NextCommander"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInNextCommander}"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\NextCommander"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\NextCommander.exe"",0"; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\NextCommander\command"; ValueType: string; ValueName: ""; ValueData: """{app}\NextCommander.exe"" ""%V"""; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\NextCommander"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInNextCommander}"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\NextCommander"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\NextCommander.exe"",0"; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\NextCommander\command"; ValueType: string; ValueName: ""; ValueData: """{app}\NextCommander.exe"" ""%1"""; Tasks: explorermenu

[Run]
Filename: "{app}\NextCommander.exe"; Description: "{cm:LaunchProgram,NextCommander}"; Flags: nowait postinstall skipifsilent

; The settings (%APPDATA%\NextCommander) stay when the program is removed, so a new installation finds them.
