; congee installer (Inno Setup 6). Build with installer\build.ps1.
;
; - An "Edition" page describes the .NET and Python editions; the user picks one.
; - A "Requirements" page then lists what that edition needs (.NET 10 Desktop Runtime or
;   Python 3.12), the congee sensor service and the PawnIO driver it uses, LibreHardwareMonitor
;   (only needed without the service), and winget, which installs the others. Installed ones
;   show ticked; tick a missing one to install it from its official source (winget, or its
;   download page).
; - The sensor service (congee-sensors, ..\sensors) reads temperatures, fans and GPU stats for
;   congee. It runs as a Windows service, so it is copied to Program Files and installed there
;   by "congee-sensors.exe --install", which Windows asks administrator permission for; that is
;   the only part of setup that needs admin. Uninstalling congee removes it the same way.
; - Installs per user (no admin needed for congee itself) into
;   %LOCALAPPDATA%\Programs\congee, creates %APPDATA%\congee\congee.conf if it
;   doesn't exist yet, and adds Start menu / optional sign-in shortcuts.

#ifdef TEST_INSTANCE
  ; test build only: installs beside the real congee, with its own uninstall entry and records
  #define AppName "congee-uninstall-test"
#else
  #define AppName "congee"
#endif
#define AppVersion "1.2.3"

[Setup]
#ifdef TEST_INSTANCE
AppId={{0D6A3C55-9E21-4C7B-8A44-6F1E2B3C4D5E}
#else
AppId={{6C7B1E64-3F0A-4E2B-9B7C-2C0E5A1D9F43}
#endif
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=congee-setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\congee.ico
SetupIconFile=..\congee.ico

; The Edition page (in [Code]) picks one of these; the standard components page is skipped.
; Silent installs can still pass /COMPONENTS="dotnet", "python" or "dotnet,python".
[Types]
Name: "dotnet"; Description: ".NET edition"
Name: "python"; Description: "Python edition"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "dotnet"; Description: ".NET edition"; Types: dotnet
Name: "python"; Description: "Python edition"; Types: python

; Switching editions: remove the one that wasn't picked, so only one starts and shows in Start.
[InstallDelete]
Type: filesandordirs; Name: "{app}\python"; Components: not python
Type: files; Name: "{group}\congee (Python edition).lnk"; Components: not python
Type: files; Name: "{app}\congee.exe"; Components: not dotnet

[Tasks]
Name: "startup"; Description: "Start congee when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; .NET edition
Source: "..\dotnet\publish\congee.exe"; DestDir: "{app}"; Components: dotnet; Flags: ignoreversion
Source: "..\congee.conf"; DestDir: "{app}"; Components: dotnet; Flags: ignoreversion
; Python edition (start.bat expects .venv next to it)
Source: "..\congee.py"; DestDir: "{app}\python"; Components: python; Flags: ignoreversion
Source: "..\congee.conf"; DestDir: "{app}\python"; Components: python; Flags: ignoreversion
Source: "..\requirements.txt"; DestDir: "{app}\python"; Components: python; Flags: ignoreversion
Source: "..\start.bat"; DestDir: "{app}\python"; Components: python; Flags: ignoreversion
; Shared
Source: "..\congee.ico"; DestDir: "{app}\python"; Components: python; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\congee.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "stop-congee.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "stop-congee.ps1"; Flags: dontcopy
Source: "upgrade-config.ps1"; DestDir: "{app}"; Flags: ignoreversion
; Sensor service (both editions). Setup copies it on to Program Files when installing it.
Source: "..\sensors\publish\*"; DestDir: "{app}\sensors"; Flags: ignoreversion recursesubdirs
; The user's own config: created once, never overwritten or removed
Source: "..\congee.conf"; DestDir: "{userappdata}\congee"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\congee"; Filename: "{app}\congee.exe"; Components: dotnet
Name: "{group}\congee (Python edition)"; Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; IconFilename: "{app}\python\congee.ico"; Components: python
Name: "{group}\Edit congee config"; Filename: "{userappdata}\congee\congee.conf"
Name: "{group}\congee README"; Filename: "{app}\README.md"
Name: "{group}\Uninstall congee"; Filename: "{uninstallexe}"
Name: "{userdesktop}\congee"; Filename: "{app}\congee.exe"; Tasks: desktopicon; Components: dotnet
Name: "{userdesktop}\congee"; Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; IconFilename: "{app}\python\congee.ico"; Tasks: desktopicon; Check: PythonIsMain
; Only one edition at sign-in (two would draw on top of each other): .NET if installed
Name: "{userstartup}\congee"; Filename: "{app}\congee.exe"; Tasks: startup; Components: dotnet
Name: "{userstartup}\congee"; Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; IconFilename: "{app}\python\congee.ico"; Tasks: startup; Check: PythonIsMain

[Run]
Filename: "{code:LhmExe}"; Description: "Start LibreHardwareMonitor now (asks for admin)"; Verb: "runas"; Flags: postinstall shellexec nowait skipifsilent; Check: LhmInstalled and not SensorServiceInstalled
Filename: "{app}\congee.exe"; Description: "Start congee now"; Flags: postinstall nowait skipifsilent; Components: dotnet; Check: HasDesktopRuntime10
Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; Description: "Start congee now"; Flags: postinstall nowait skipifsilent; Check: PythonIsMain and VenvReady

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\stop-congee.ps1"""; Flags: runhidden; RunOnceId: "StopCongee"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\python\.venv"
Type: filesandordirs; Name: "{app}\python\__pycache__"

[Messages]
FinishedLabel=Setup has installed [name] on your computer.%n%nYour config file is %APPDATA%\congee\congee.conf (Start menu → "Edit congee config"). Save it and the widget updates on its own.%n%nTemperatures, fans and GPU stats come from the congee sensor service, which starts with Windows. Without it, congee reads LibreHardwareMonitor instead.

[Code]
const
  DEP_LHM = 0;
  DEP_NET = 1;
  DEP_PY = 2;
  DEP_WINGET = 3;
  DEP_SVC = 4;     { congee's sensor service }
  DEP_PAWNIO = 5;  { the driver the service (and LibreHardwareMonitor) reads CPU/motherboard sensors with }
  DEP_NETRT = 6;   { .NET 10 Runtime, for the service with the Python edition }
  SVC_NAME = 'congee-sensors';
  WINGET_FLAGS = ' -e --accept-package-agreements --accept-source-agreements --disable-interactivity';
  { winget id = name of each app setup (or congee itself) installed; the uninstaller offers to remove them }
#ifdef TEST_INSTANCE
  DEPS_KEY = 'Software\congee-uninstall-test\InstalledDeps';
#else
  DEPS_KEY = 'Software\congee\InstalledDeps';
#endif
  URL_LHM = 'https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases';
  URL_NET = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  URL_PY = 'https://www.python.org/downloads/windows/';
  URL_WINGET = 'ms-windows-store://pdp/?productid=9NBLGGH4NNS1';  { App Installer in the Microsoft Store }
  URL_PAWNIO = 'https://pawnio.eu/';

var
  EditionPage: TWizardPage;
  NetRadio, PyRadio: TNewRadioButton;
  DepPage: TInputOptionWizardPage;
  DepPresent, DepChosen: array[0..6] of Boolean;
  DepRow: array[0..6] of Integer;  { which DEP_ each visible row is }
  DepRows: Integer;
  InstallPy, InstallNet, InstallLhm, InstallWinget, InstallSvc, InstallPawnIO, InstallNetRt: Boolean;

{ ---------------------------------------------------------------- detection }

function WingetExe(): String;
begin
#ifdef SIMULATE_NO_WINGET
  Result := ''; Exit;  { test build only: pretend winget isn't there }
#endif
  Result := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if not FileExists(Result) then
    Result := '';
end;

function PyFromRegistry(RootKey: Integer): String;
var
  S: String;
begin
  Result := '';
  if RegQueryStringValue(RootKey, 'Software\Python\PythonCore\3.12\InstallPath', 'ExecutablePath', S) and FileExists(S) then
    Result := S
  else if RegQueryStringValue(RootKey, 'Software\Python\PythonCore\3.12\InstallPath', '', S) and FileExists(AddBackslash(S) + 'python.exe') then
    Result := AddBackslash(S) + 'python.exe';
end;

function PyFromLauncher(Launcher: String): String;
var
  Tmp: String;
  Code: Integer;
  Lines: TArrayOfString;
begin
  Result := '';
  Tmp := ExpandConstant('{tmp}\pyexe.txt');
  DeleteFile(Tmp);
  if Exec(ExpandConstant('{cmd}'), '/C ""' + Launcher + '" -V:3.12 -c "import sys;print(sys.executable)" > "' + Tmp + '" 2>nul"',
          '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    if LoadStringsFromFile(Tmp, Lines) and (GetArrayLength(Lines) > 0) and FileExists(Trim(Lines[0])) then
      Result := Trim(Lines[0]);
end;

{ Path to a Python 3.12 python.exe, or '' if there isn't one. }
function FindPython312(): String;
begin
#ifdef SIMULATE_MISSING
  Result := ''; Exit;  { test build only: pretend nothing is installed }
#endif
  Result := PyFromRegistry(HKCU);
  if Result = '' then Result := PyFromRegistry(HKLM64);
  if Result = '' then Result := PyFromRegistry(HKLM32);
  { The py launcher also finds installs that skip the registry (e.g. the Python install manager) }
  if Result = '' then Result := PyFromLauncher(ExpandConstant('{localappdata}\Microsoft\WindowsApps\py.exe'));
  if Result = '' then Result := PyFromLauncher(ExpandConstant('{localappdata}\Programs\Python\Launcher\py.exe'));
  if Result = '' then Result := PyFromLauncher(ExpandConstant('{win}\py.exe'));
  if Result = '' then Result := PyFromLauncher('py');
end;

function HasDesktopRuntime10(): Boolean;
var
  FR: TFindRec;
begin
#ifdef SIMULATE_MISSING
  Result := False; Exit;
#endif
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FR);
  if Result then
    FindClose(FR);
end;

{ The plain .NET 10 Runtime (part of the Desktop Runtime too): what the sensor service needs. }
function HasNetRuntime10(): Boolean;
var
  FR: TFindRec;
begin
#ifdef SIMULATE_MISSING
  Result := False; Exit;
#endif
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\10.*'), FR);
  if Result then
    FindClose(FR);
end;

function SensorServiceInstalled(): Boolean;
begin
#ifdef SIMULATE_MISSING
  Result := False; Exit;
#endif
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\' + SVC_NAME);
end;

function PawnIOInstalled(): Boolean;
begin
#ifdef SIMULATE_MISSING
  Result := False; Exit;
#endif
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

{ Where --install puts the service (admin-only folder, since it runs as LocalSystem). }
function InstalledServiceDir(): String;
begin
  Result := ExpandConstant('{commonpf64}\congee\sensors');
end;

function FindInDir(Pattern, ExeName: String): String;
var
  FR: TFindRec;
begin
  Result := '';
  if FindFirst(Pattern, FR) then
  begin
    try
      repeat
        if FileExists(ExtractFilePath(Pattern) + FR.Name + '\' + ExeName) then
        begin
          Result := ExtractFilePath(Pattern) + FR.Name + '\' + ExeName;
          Exit;
        end;
      until not FindNext(FR);
    finally
      FindClose(FR);
    end;
  end;
end;

function LhmExe(Param: String): String;
begin
  Result := FindInDir(ExpandConstant('{localappdata}\Microsoft\WinGet\Packages\LibreHardwareMonitor.LibreHardwareMonitor_*'), 'LibreHardwareMonitor.exe');
  if Result = '' then
    Result := FindInDir(ExpandConstant('{commonpf64}\WinGet\Packages\LibreHardwareMonitor.LibreHardwareMonitor_*'), 'LibreHardwareMonitor.exe');
  if (Result = '') and FileExists(ExpandConstant('{commonpf64}\LibreHardwareMonitor\LibreHardwareMonitor.exe')) then
    Result := ExpandConstant('{commonpf64}\LibreHardwareMonitor\LibreHardwareMonitor.exe');
end;

function LhmInstalled(): Boolean;
begin
#ifdef SIMULATE_MISSING
  Result := False; Exit;
#endif
  Result := LhmExe('') <> '';
end;

{ Python edition is the one to start/autostart only when .NET isn't installed }
function PythonIsMain(): Boolean;
begin
  Result := WizardIsComponentSelected('python') and not WizardIsComponentSelected('dotnet');
end;

function VenvReady(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\python\.venv\Scripts\pythonw.exe'));
end;

{ ---------------------------------------------------------- dependency page }

function AddDescription(Parent: TWizardPage; Top: Integer; Text: String): TNewStaticText;
begin
  Result := TNewStaticText.Create(Parent);
  Result.Parent := Parent.Surface;
  Result.Left := ScaleX(18);
  Result.Top := Top;
  Result.Width := Parent.SurfaceWidth - ScaleX(18);
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.Height := ScaleY(48);
  Result.Caption := Text;
end;

function AddRadio(Parent: TWizardPage; Top: Integer; Caption: String): TNewRadioButton;
begin
  Result := TNewRadioButton.Create(Parent);
  Result.Parent := Parent.Surface;
  Result.Top := Top;
  Result.Width := Parent.SurfaceWidth;
  Result.Height := ScaleY(18);
  Result.Font.Style := [fsBold];
  Result.Caption := Caption;
end;

function DepIntro(Net, Winget: Boolean): String;
begin
  if Net then Result := 'What the .NET edition needs. ' else Result := 'What the Python edition needs. ';
  Result := Result + 'Ticked items are already installed. The sensor service and PawnIO need administrator ' +
            'permission once, which Windows asks for. ';
  if Winget then
    Result := Result + 'Tick anything missing that you want, and setup installs it with winget from its ' +
              'official source when you click Install, each under its own license.'
  else
    Result := Result + 'winget isn''t available, so for anything missing you tick, setup opens its official ' +
              'download page when you click Install.';
end;

procedure InitializeWizard();
var
  Note: TNewStaticText;
begin
  EditionPage := CreateCustomPage(wpSelectDir, 'Edition', 'Which edition of congee do you want?');
  NetRadio := AddRadio(EditionPage, 0, '.NET edition (recommended)');
  AddDescription(EditionPage, ScaleY(22),
    'The best-looking text: true per-pixel transparency keeps text crisp on any wallpaper, and a ' +
    'semi-transparent background fades only the background, like real conky. Uses about 80 MB of memory. ' +
    'Needs the .NET 10 Desktop Runtime from Microsoft.');
  PyRadio := AddRadio(EditionPage, ScaleY(84), 'Python edition');
  AddDescription(EditionPage, ScaleY(106),
    'The smallest footprint, about 35 MB of memory, and easy to tweak if you know Python. Text can get a ' +
    'slight dark edge on bright wallpapers. Needs Python 3.12 from python.org; setup adds the psutil ' +
    'package itself.');
  Note := AddDescription(EditionPage, ScaleY(176),
    'Both show the same things and use the same config file, so you can switch any time by running ' +
    'setup again. Next, setup checks what the edition you pick needs.');
  Note.Left := 0;
  Note.Width := EditionPage.SurfaceWidth;
  { Pre-select what's installed (or passed with /COMPONENTS): Python only if .NET isn't picked }
  PyRadio.Checked := WizardIsComponentSelected('python') and not WizardIsComponentSelected('dotnet');
  NetRadio.Checked := not PyRadio.Checked;
  { Recommended, so ticked when missing (the user can untick them) }
  DepChosen[DEP_SVC] := True;
  DepChosen[DEP_PAWNIO] := True;
  DepChosen[DEP_NETRT] := True;

  { The intro is set per edition when the page shows; the longest version is given here so the
    page leaves room for it above the list. }
  DepPage := CreateInputOptionPage(EditionPage.ID, 'Requirements', 'Software the edition you picked needs or can use',
    DepIntro(False, True), False, True);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := PageID = wpSelectComponents;
end;

{ One row: installed = ticked and greyed out; missing = the user's choice, with where it comes from. }
procedure AddDepSub(Dep: Integer; Caption: String; Present: Boolean; Missing: String);
var
  Sub: String;
begin
  DepPresent[Dep] := Present;
  if Present then Sub := 'Installed' else Sub := Missing;
  DepPage.CheckListBox.AddCheckBox(Caption, Sub, 0, Present or DepChosen[Dep], not Present, False, False, nil);
  DepRow[DepRows] := Dep;
  DepRows := DepRows + 1;
end;

procedure AddDep(Dep: Integer; Caption: String; Present: Boolean; Source: String);
begin
  AddDepSub(Dep, Caption, Present, 'Missing - from ' + Source);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = DepPage.ID then
  begin
    DepPage.SubCaptionLabel.Caption := DepIntro(NetRadio.Checked, WingetExe() <> '');
    DepPage.CheckListBox.Items.Clear;
    DepRows := 0;
    if NetRadio.Checked then
      AddDep(DEP_NET, '.NET 10 Desktop Runtime (required)', HasDesktopRuntime10(), 'Microsoft')
    else
      AddDep(DEP_PY, 'Python 3.12 (required)', FindPython312() <> '', 'python.org');
    AddDepSub(DEP_SVC, 'congee sensor service (recommended: temperatures, fans, GPU stats)', SensorServiceInstalled(),
              'Missing - comes with congee');
    if not NetRadio.Checked then
      AddDep(DEP_NETRT, '.NET 10 Runtime (for the sensor service)', HasNetRuntime10(), 'Microsoft');
    AddDep(DEP_PAWNIO, 'PawnIO driver (lets the sensor service read CPU and motherboard sensors)', PawnIOInstalled(), 'pawnio.eu');
    AddDep(DEP_LHM, 'LibreHardwareMonitor (optional: only needed without the sensor service)', LhmInstalled(), 'GitHub');
    AddDep(DEP_WINGET, 'winget (lets setup install the others)', WingetExe() <> '', 'Microsoft Store');
  end;
end;

function DepTicked(Dep: Integer): Boolean;
begin
  Result := DepChosen[Dep] and not DepPresent[Dep];
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I: Integer;
  Name: String;
begin
  Result := True;
  if CurPageID = EditionPage.ID then
  begin
    if NetRadio.Checked then WizardSelectComponents('dotnet,!python')
    else WizardSelectComponents('python,!dotnet');
  end
  else if CurPageID = DepPage.ID then
  begin
    for I := 0 to DepRows - 1 do
      if not DepPresent[DepRow[I]] then DepChosen[DepRow[I]] := DepPage.CheckListBox.Checked[I];
    { Only what's on the page for this edition gets installed }
    InstallLhm := DepTicked(DEP_LHM);
    InstallWinget := DepTicked(DEP_WINGET);
    InstallSvc := DepTicked(DEP_SVC);
    InstallPawnIO := DepTicked(DEP_PAWNIO);
    InstallNetRt := PyRadio.Checked and DepTicked(DEP_NETRT);
    InstallNet := NetRadio.Checked and DepTicked(DEP_NET);
    InstallPy := PyRadio.Checked and DepTicked(DEP_PY);
    Name := '';
    if NetRadio.Checked and not DepPresent[DEP_NET] and not InstallNet then Name := '.NET 10 Desktop Runtime';
    if PyRadio.Checked and not DepPresent[DEP_PY] and not InstallPy then Name := 'Python 3.12';
    if Name <> '' then
      Result := MsgBox(Name + ' is missing and not ticked, and congee won''t run without it.' + #13#10#13#10 +
                       'Continue anyway? You can install it later and congee will work then.',
                       mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

{ ------------------------------------------------------------ installation }

procedure SetStatus(S: String);
begin
  WizardForm.StatusLabel.Caption := S;
  WizardForm.StatusLabel.Update;
end;

procedure OpenPage(Url: String);
var
  Code: Integer;
begin
  ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

{ Installs a requirement with winget; without winget, or if winget fails, offers its download page. }
function InstallDep(Id, Extra, Title, Url: String): Boolean;
var
  Code: Integer;
begin
  Result := False;
  if WingetExe() = '' then
  begin
    MsgBox('winget isn''t available, so setup will open the ' + Title + ' download page. ' +
           'Install it from there; congee picks it up once it''s installed.', mbInformation, MB_OK);
    OpenPage(Url);
    Exit;
  end;
  SetStatus('Installing ' + Title + ' with winget (this can take a few minutes)...');
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    Result := Exec(WingetExe(), 'install --id ' + Id + WINGET_FLAGS + Extra, '', SW_SHOWMINNOACTIVE,
                   ewWaitUntilTerminated, Code) and (Code = 0);
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
  { Remember what we installed, so uninstalling congee can offer to remove it too. }
  if Result then RegWriteStringValue(HKCU, DEPS_KEY, Id, Title);
  if not Result then
    if MsgBox(Title + ' could not be installed automatically (winget exit code ' + IntToStr(Code) + ').' + #13#10#13#10 +
              'Open its download page to install it yourself?' + #13#10 + Url,
              mbError, MB_YESNO) = IDYES then
      OpenPage(Url);
end;

{ Turn on LHM's web server (what congee reads) the first time, if LHM has no settings yet. }
procedure ConfigureLhm();
var
  Exe, Cfg: String;
begin
  Exe := LhmExe('');
  if Exe = '' then Exit;
  Cfg := ExtractFilePath(Exe) + 'LibreHardwareMonitor.config';
  if FileExists(Cfg) then Exit;
  SaveStringToFile(Cfg,
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<configuration>' + #13#10 +
    '  <appSettings>' + #13#10 +
    '    <add key="runWebServerMenuItem" value="true" />' + #13#10 +
    '    <add key="listenerPort" value="8085" />' + #13#10 +
    '    <add key="startMinMenuItem" value="true" />' + #13#10 +
    '    <add key="minTrayMenuItem" value="true" />' + #13#10 +
    '    <add key="minCloseMenuItem" value="true" />' + #13#10 +
    '  </appSettings>' + #13#10 +
    '</configuration>' + #13#10, False);
end;

procedure SetupPythonVenv();
var
  Py, Venv, Msg: String;
  Code: Integer;
begin
  Py := FindPython312();
  Venv := ExpandConstant('{app}\python\.venv');
  if Py = '' then
  begin
    MsgBox('Python 3.12 was not found, so the Python edition is not set up yet.' + #13#10#13#10 +
           'Install Python 3.12 (winget install Python.Python.3.12), then run this setup again.', mbInformation, MB_OK);
    Exit;
  end;
  SetStatus('Setting up the Python edition (creating its environment)...');
  if not FileExists(Venv + '\Scripts\python.exe') then
    if not Exec(Py, '-m venv "' + Venv + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      MsgBox('Could not create the Python environment (exit code ' + IntToStr(Code) + ').', mbError, MB_OK);
      Exit;
    end;
  SetStatus('Setting up the Python edition (downloading psutil)...');
  if not Exec(Venv + '\Scripts\python.exe',
              '-m pip install --disable-pip-version-check -q -r "' + ExpandConstant('{app}\python\requirements.txt') + '"',
              '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
  begin
    Msg := 'Could not install the psutil package (exit code ' + IntToStr(Code) + '). Check your internet connection, then run:' + #13#10 +
           '  "' + Venv + '\Scripts\pip" install -r "' + ExpandConstant('{app}\python\requirements.txt') + '"';
    MsgBox(Msg, mbError, MB_OK);
  end;
end;

{ Stop running copies so their files can be replaced. }
procedure StopRunningCongee();
var
  Code: Integer;
begin
  ExtractTemporaryFile('stop-congee.ps1');
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       ExpandConstant('-NoProfile -ExecutionPolicy Bypass -File "{tmp}\stop-congee.ps1"'),
       '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCongee();
  Result := '';
end;

{ Adds features from newer versions to an existing config (only where it still has the old
  default lines), saving the old file as congee.conf.bak first. See upgrade-config.ps1. }
procedure UpgradeUserConfig();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       ExpandConstant('-NoProfile -ExecutionPolicy Bypass -File "{app}\upgrade-config.ps1" -Config "{userappdata}\congee\congee.conf"'),
       '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Runs congee-sensors.exe --install or --uninstall as administrator (Windows asks). Exit codes:
  0 done, 1 failed, 5 not admin; 1223 = the admin prompt was declined. }
function RunServiceSetup(Exe, Action: String; var Code: Integer): Boolean;
begin
  Code := -1;
#ifdef TEST_INSTANCE
  Result := True; Code := 0; Exit;  { test build only: never touch the real service }
#endif
  Result := ShellExec('runas', Exe, Action, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ The copy in Program Files differs from the one this setup brought (an older version). }
function SensorServiceOutdated(): Boolean;
var
  Mine, Theirs: String;
begin
  Mine := ExpandConstant('{app}\sensors\congee-sensors.dll');
  Theirs := InstalledServiceDir() + '\congee-sensors.dll';
  Result := not FileExists(Theirs) or (GetSHA256OfFile(Mine) <> GetSHA256OfFile(Theirs));
end;

procedure InstallSensorService();
var
  Code: Integer;
begin
  if not HasNetRuntime10() then
  begin
    MsgBox('The congee sensor service needs the .NET 10 Runtime, which isn''t installed, so the service ' +
           'wasn''t set up. Install it (winget install Microsoft.DotNet.Runtime.10), then run this setup again.',
           mbError, MB_OK);
    Exit;
  end;
  SetStatus('Installing the congee sensor service (Windows asks for administrator permission)...');
  if RunServiceSetup(ExpandConstant('{app}\sensors\congee-sensors.exe'), '--install', Code) then Exit;
  if Code = 1223 then
    MsgBox('The congee sensor service wasn''t installed, because administrator permission was declined. ' +
           'congee still works; for temperatures and fans, run this setup again, or use LibreHardwareMonitor.',
           mbInformation, MB_OK)
  else
    MsgBox('The congee sensor service could not be installed (exit code ' + IntToStr(Code) + '). ' +
           'congee still works; for temperatures and fans, run this setup again, or use LibreHardwareMonitor.' + #13#10#13#10 +
           'To see the error, run in an administrator terminal:' + #13#10 +
           '  "' + ExpandConstant('{app}\sensors\congee-sensors.exe') + '" --install',
           mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if InstallWinget then
    begin
      MsgBox('Setup will open App Installer (which includes winget) in the Microsoft Store. ' +
             'Run this setup again after installing it to install the other items with winget.', mbInformation, MB_OK);
      OpenPage(URL_WINGET);
    end;
    if InstallNet then InstallDep('Microsoft.DotNet.DesktopRuntime.10', '', '.NET 10 Desktop Runtime', URL_NET);
    if InstallPy then InstallDep('Python.Python.3.12', ' --scope user', 'Python 3.12', URL_PY);
    if InstallNetRt then InstallDep('Microsoft.DotNet.Runtime.10', '', '.NET 10 Runtime', URL_NET);
    if InstallPawnIO then InstallDep('namazso.PawnIO', '', 'PawnIO', URL_PAWNIO);
    if InstallLhm then InstallDep('LibreHardwareMonitor.LibreHardwareMonitor', '', 'LibreHardwareMonitor', URL_LHM);
  end;
  if CurStep = ssPostInstall then
  begin
    UpgradeUserConfig();
    if WizardIsComponentSelected('python') then SetupPythonVenv();
    ConfigureLhm();
    if InstallSvc then InstallSensorService()
    else if SensorServiceInstalled() and SensorServiceOutdated() then
    begin
      SetStatus('Updating the congee sensor service (Windows asks for administrator permission)...');
      InstallSensorService();
    end;
  end;
end;

{ ------------------------------------------------------------- uninstalling }

{ Ends LibreHardwareMonitor so winget can remove its files. It runs as administrator, so if
  ending it directly isn't allowed, taskkill runs as administrator (Windows asks). }
procedure StopLhmForUninstall();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM LibreHardwareMonitor.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Code = 1 then  { access denied }
    ShellExec('runas', ExpandConstant('{sys}\taskkill.exe'), '/F /IM LibreHardwareMonitor.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Lists the apps setup or congee installed (and that are still there) with checkboxes, all
  unticked, and removes the ticked ones with winget. Skipped for silent uninstalls. }
procedure OfferToRemoveDeps();
var
  Ids: TArrayOfString;
  Names: array of String;
  Present: array of String;
  I, N, Code, Y: Integer;
  Name: String;
  Form: TSetupForm;
  Intro, Note: TNewStaticText;
  List: TNewCheckListBox;
  OkBtn, SkipBtn: TNewButton;
  Failed: String;
begin
  if UninstallSilent or (WingetExe() = '') then Exit;
  if not RegGetValueNames(HKCU, DEPS_KEY, Ids) then Exit;
  N := 0;
  SetArrayLength(Present, GetArrayLength(Ids));
  SetArrayLength(Names, GetArrayLength(Ids));
  for I := 0 to GetArrayLength(Ids) - 1 do
  begin
    if not RegQueryStringValue(HKCU, DEPS_KEY, Ids[I], Name) then Name := Ids[I];
    { still installed? }
    if (Ids[I] = 'LibreHardwareMonitor.LibreHardwareMonitor') and not LhmInstalled() then Continue;
    if (Ids[I] = 'Microsoft.DotNet.DesktopRuntime.10') and not HasDesktopRuntime10() then Continue;
    if (Ids[I] = 'Python.Python.3.12') and (FindPython312() = '') then Continue;
    if (Ids[I] = 'namazso.PawnIO') and not PawnIOInstalled() then Continue;
    if (Ids[I] = 'Microsoft.DotNet.Runtime.10') and not HasNetRuntime10() then Continue;
    Present[N] := Ids[I];
    Names[N] := Name;
    N := N + 1;
  end;
  if N = 0 then Exit;

  Form := CreateCustomForm(ScaleX(440), ScaleY(260), False, True);
  Form.Caption := 'Uninstall congee';
  Intro := TNewStaticText.Create(Form);
  Intro.Parent := Form;
  Intro.Left := ScaleX(16); Intro.Top := ScaleY(16);
  Intro.Width := Form.ClientWidth - ScaleX(32);
  Intro.AutoSize := False; Intro.WordWrap := True; Intro.Height := ScaleY(46);
  Intro.Caption := 'These were installed along with congee. Tick any you also want to remove. ' +
                   'Leave them if other programs use them.';
  List := TNewCheckListBox.Create(Form);
  List.Parent := Form;
  List.Left := ScaleX(16); List.Top := ScaleY(66);
  List.Width := Form.ClientWidth - ScaleX(32); List.Height := ScaleY(90);
  for I := 0 to N - 1 do
    List.AddCheckBox(Names[I], '', 0, False, True, False, False, nil);
  Y := List.Top + List.Height + ScaleY(8);
  Note := TNewStaticText.Create(Form);
  Note.Parent := Form;
  Note.Left := ScaleX(16); Note.Top := Y;
  Note.Width := Form.ClientWidth - ScaleX(32);
  Note.AutoSize := False; Note.WordWrap := True; Note.Height := ScaleY(32);
  Note.Caption := 'They are removed with winget. Removing the .NET runtime or PawnIO asks for administrator ' +
                  'permission. LibreHardwareMonitor also uses PawnIO.';
  OkBtn := TNewButton.Create(Form);
  OkBtn.Parent := Form;
  OkBtn.Caption := 'Remove ticked';
  OkBtn.Width := ScaleX(110); OkBtn.Height := ScaleY(25);
  OkBtn.Left := Form.ClientWidth - ScaleX(16) - ScaleX(110) - ScaleX(8) - ScaleX(110);
  OkBtn.Top := Form.ClientHeight - ScaleY(16) - ScaleY(25);
  OkBtn.ModalResult := mrOk;
  SkipBtn := TNewButton.Create(Form);
  SkipBtn.Parent := Form;
  SkipBtn.Caption := 'Keep them';
  SkipBtn.Width := ScaleX(110); SkipBtn.Height := ScaleY(25);
  SkipBtn.Left := Form.ClientWidth - ScaleX(16) - ScaleX(110);
  SkipBtn.Top := OkBtn.Top;
  SkipBtn.ModalResult := mrCancel;
  SkipBtn.Cancel := True;
  Form.ActiveControl := SkipBtn;
  if Form.ShowModal() <> mrOk then Exit;

  Failed := '';
  for I := 0 to N - 1 do
    if List.Checked[I] then
    begin
      UninstallProgressForm.StatusLabel.Caption := 'Removing ' + Names[I] + ' with winget...';
      UninstallProgressForm.StatusLabel.Update;
      if Present[I] = 'LibreHardwareMonitor.LibreHardwareMonitor' then StopLhmForUninstall();
      if Exec(WingetExe(), 'uninstall --id ' + Present[I] + ' -e --accept-source-agreements --disable-interactivity',
              '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
        RegDeleteValue(HKCU, DEPS_KEY, Present[I])
      else
        Failed := Failed + #13#10 + '  - ' + Names[I] + ' (winget exit code ' + IntToStr(Code) + '): winget uninstall --id ' + Present[I];
    end;
  if Failed <> '' then
    MsgBox('These could not be removed automatically. You can remove them from Settings > Apps, or with:' + Failed,
           mbError, MB_OK);
end;

{ The service is part of congee, so it goes too (for every user of this PC: there's one service).
  Windows asks for administrator permission. }
procedure RemoveSensorService();
var
  Exe: String;
  Code: Integer;
begin
  if not SensorServiceInstalled() then Exit;
  Exe := ExpandConstant('{app}\sensors\congee-sensors.exe');
  if not FileExists(Exe) then Exe := InstalledServiceDir() + '\congee-sensors.exe';
  UninstallProgressForm.StatusLabel.Caption := 'Removing the congee sensor service (Windows asks for administrator permission)...';
  UninstallProgressForm.StatusLabel.Update;
  if RunServiceSetup(Exe, '--uninstall', Code) then Exit;
  if not UninstallSilent then
    MsgBox('The congee sensor service could not be removed (' + IntToStr(Code) + '). To remove it, run in an ' +
           'administrator terminal:' + #13#10 + '  "' + InstalledServiceDir() + '\congee-sensors.exe" --uninstall',
           mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveSensorService();
    OfferToRemoveDeps();
  end;
end;
