; ClaudeWidgetSetup.iss — Inno Setup script that produces a single
; ClaudeWidgetSetup.exe installer for the ClaudeWidget MSIX.
;
; Why a wrapped .exe instead of a real .msi: a genuine .msi's custom actions
; run via the Windows Installer service as SYSTEM in session 0, which cannot
; reliably call Add-AppxPackage (that needs the interactive user's session).
; Inno Setup compiles to a normal elevated .exe, so it can run
; Add-AppxPackage/Import-Certificate correctly, while still behaving like a
; real installer: Start Menu / Add-Remove-Programs entry, wizard UI, and a
; proper Uninstall entry that removes both the app and the trusted cert.
;
; Expects to be compiled from a staging folder (see package-release.ps1)
; containing ClaudeWidget.msix, ClaudeUsageWidgetLocal.cer,
; PostInstall.ps1, PreUninstall.ps1.
;
; NOTE: the app is built self-contained (WindowsAppSDKSelfContained=true in
; ClaudeWidget.csproj), so the Windows App Runtime is baked into the MSIX
; itself -- no separate runtime installer needs to be bundled/run here.

#define MyAppName "Claude Limits"
#define MyAppVersion "1.0.0.0"
#define MyAppPublisher "AppPublisher"
#define MyAppExeName "ClaudeWidget.msix"

; StagingDir is passed in from package-release.ps1 via /DStagingDir=...
#ifndef StagingDir
  #define StagingDir "."
#endif

[Setup]
AppId={{5F2B6C2B-9C7B-4B2A-9A0E-1F9E7E9C7D01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ClaudeWidget
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
OutputDir={#StagingDir}\..\output
OutputBaseFilename=ClaudeWidgetSetup
Compression=lzma
SolidCompression=yes
SetupIconFile={#StagingDir}\AppIcon.ico
UninstallDisplayIcon={app}\AppIcon.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#StagingDir}\ClaudeWidget.msix"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\ClaudeUsageWidgetLocal.cer"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\PostInstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\PreUninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StagingDir}\AppIcon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\PostInstall.ps1"" -MsixPath ""{app}\{#MyAppExeName}"" -CerPath ""{app}\ClaudeUsageWidgetLocal.cer"""; \
    StatusMsg: "Installing Claude Limits widget..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\PreUninstall.ps1"""; \
    StatusMsg: "Removing Claude Limits widget..."; Flags: runhidden waituntilterminated; RunOnceId: "RemoveClaudeWidget"
