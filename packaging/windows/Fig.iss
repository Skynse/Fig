; The Windows installer: puts Fig (fig.exe and the FFmpeg DLLs beside it) in the user's programs folder (no admin
; needed; an admin can choose all users), adds it to the Start menu (and the desktop, if ticked), and registers an
; uninstaller. The user's projects and settings in %USERPROFILE%\.fig are left alone.
;
;   iscc /DVersion=2.0.0 /DSource=path\to\staged\folder /DOutput=dist packaging\windows\Fig.iss
;
; Built by .github/workflows/release.yml.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "stage"
#endif
#ifndef Output
  #define Output "."
#endif

[Setup]
; never change the AppId: it is how an update finds the install it replaces (the 1.x installs too)
AppId={{6F0D6B2E-4C3A-4F57-9E3B-7A1C2E8D5F14}
AppName=Fig
AppVersion={#Version}
AppVerName=Fig {#Version}
AppPublisher=Fig
AppPublisherURL=https://github.com/Skynse/Fig
DefaultDirName={autopf}\Fig
DefaultGroupName=Fig
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\..\LICENSE
SetupIconFile=fig.ico
UninstallDisplayIcon={app}\fig.ico
UninstallDisplayName=Fig
OutputDir={#Output}
OutputBaseFilename=Fig-{#Version}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Fig must not be running while it is replaced
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "fig.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Fig"; Filename: "{app}\fig.exe"; IconFilename: "{app}\fig.ico"
Name: "{autodesktop}\Fig"; Filename: "{app}\fig.exe"; IconFilename: "{app}\fig.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\fig.exe"; Description: "{cm:LaunchProgram,Fig}"; Flags: nowait postinstall skipifsilent
