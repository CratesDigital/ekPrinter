; Inno Setup script for ekPrinter.
;
; Build the payload first, from the repository root:
;     dotnet publish -c Release
; then compile this script:
;     iscc installer\ekprinter.iss
;
; Produces one ekPrinter-Setup.exe: double-click, Install, Finish.
;
; Two choices are deliberate and worth not undoing:
;
; PER-USER, NOT A SERVICE. Printing silently to a user's printers, and showing
; a tray icon, both belong to the interactive session; a Windows service runs
; in session 0, which has neither. Installing under the user's own profile
; also means no UAC prompt — the people running this are shop staff, not
; administrators.
;
; NO FIREWALL RULE. The agent binds 127.0.0.1 only. Windows Firewall does not
; filter loopback and does not prompt for it. Opening a port would let anyone
; on the shop's network print to the till and open its cash drawer.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName    "ekPrinter"
#define Publisher  "Eickter Software & Supplies"
#define ExeName    "ekprinter.exe"
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"
#define AgentPort  "8421"

[Setup]
; Never change AppId — it is how Windows recognises an upgrade rather than a
; second, parallel installation.
AppId={{CB760E65-6876-4A88-88FF-AC479132AC57}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://eickter.com
AppSupportURL=https://github.com/CratesDigital/ekPrinter
AppCopyright=© 2026 Eickter Software & Supplies
VersionInfoCompany={#Publisher}
VersionInfoProductName={#AppName}
VersionInfoDescription=ekPrinter Setup
VersionInfoCopyright=© 2026 Eickter Software & Supplies
VersionInfoVersion={#AppVersion}
SetupIconFile=..\ekprinter.ico
DefaultDirName={localappdata}\Programs\ekPrinter
DefaultGroupName={#AppName}
OutputDir=..\dist
OutputBaseFilename=ekPrinter-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#ExeName}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"

[CustomMessages]
en.NoWebView2=The Microsoft Edge WebView2 Runtime was not found on this computer.%n%nekPrinter needs it to print documents. Windows 11 and updated Windows 10 include it; otherwise install it from Microsoft (search "WebView2 Runtime"), then restart ekPrinter.%n%nSetup will continue.
ar.NoWebView2=لم يتم العثور على Microsoft Edge WebView2 Runtime على هذا الجهاز.%n%nيحتاجه ekPrinter لطباعة المستندات. يتضمنه Windows 11 وWindows 10 المحدّث؛ وإلا فقم بتثبيته من Microsoft (ابحث عن "WebView2 Runtime") ثم أعد تشغيل ekPrinter.%n%nسيستمر التثبيت.
en.StartAgent=Start ekPrinter
ar.StartAgent=تشغيل ekPrinter
en.AgentPage=ekPrinter page
ar.AgentPage=صفحة ekPrinter

[Files]
; The whole publish folder: self-contained without single-file is the runtime
; as a few hundred loose DLLs, and a missing one fails at launch with nothing
; to read. The WebView2 loader lives in runtimes\win-x64\native, hence
; recursesubdirs.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,web.config,aspnetcorev2_inprocess.dll"

[Icons]
Name: "{group}\{#AppName}";            Filename: "{app}\{#ExeName}"
Name: "{group}\{cm:AgentPage}";        Filename: "http://127.0.0.1:{#AgentPort}/"
; No {userstartup} shortcut. The agent manages autostart itself through the
; HKCU Run key so the operator can turn it off from the tray menu.

[Registry]
; The agent writes this itself; declared only so uninstalling removes it, or
; Windows would try to start a deleted executable at every login.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "ekPrinter"; ValueType: none; Flags: uninsdeletevalue

[Run]
; First run opens the agent's page by itself, which is where the pairing code is.
Filename: "{app}\{#ExeName}"; Description: "{cm:StartAgent}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#ExeName}"; Flags: runhidden; RunOnceId: "StopAgent"

[Code]
const
  WebView2Client = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

// Microsoft's documented detection: a per-machine install registers under
// HKLM (WOW6432Node on 64-bit Windows), a per-user one under HKCU. A missing
// or "0.0.0.0" version means not installed.
function HasWebView2Key(Root: Integer; Key: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function WebView2Installed: Boolean;
begin
  Result := HasWebView2Key(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2Client)
         or HasWebView2Key(HKLM, 'SOFTWARE\' + WebView2Client)
         or HasWebView2Key(HKCU, 'Software\' + WebView2Client);
end;

// Warn, do not block: raw printing works without it, and the agent page says
// the same thing in words for whoever looks later.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardSilent and not WebView2Installed then
    MsgBox(CustomMessage('NoWebView2'), mbInformation, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // An upgrade almost always runs while the old agent is up — it started at
  // login. Its exe is locked, and Inno's "close applications" prompt cannot
  // find it, because a windowless process has no window to close.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#ExeName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

// agent-config.json lives in %APPDATA%\ekPrinter and is left alone on
// uninstall, so reinstalling keeps the pairing.
