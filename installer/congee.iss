; congee installer (Inno Setup 6). Build with installer\build.ps1.
;
; - Lets the user pick the .NET edition, the Python edition, or both.
; - A "Dependencies" page checks for Python 3.12, the .NET 10 Desktop Runtime and
;   LibreHardwareMonitor, and offers to install whatever is missing through winget.
; - Installs per user (no admin needed for congee itself) into
;   %LOCALAPPDATA%\Programs\congee, creates %APPDATA%\congee\congee.conf if it
;   doesn't exist yet, and adds Start menu / optional sign-in shortcuts.

#define AppName "congee"
#define AppVersion "1.0.0"

[Setup]
AppId={{6C7B1E64-3F0A-4E2B-9B7C-2C0E5A1D9F43}
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
UninstallDisplayIcon={app}\congee.exe

[Types]
Name: "dotnet"; Description: ".NET edition (recommended)"
Name: "python"; Description: "Python edition"
Name: "both"; Description: "Both editions"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "dotnet"; Description: ".NET edition: best-looking text (needs the .NET 10 Desktop Runtime)"; Types: dotnet both
Name: "python"; Description: "Python edition: smallest memory use (needs Python 3.12)"; Types: python both

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
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
; The user's own config: created once, never overwritten or removed
Source: "..\congee.conf"; DestDir: "{userappdata}\congee"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\congee"; Filename: "{app}\congee.exe"; Components: dotnet
Name: "{group}\congee (Python edition)"; Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; Components: python
Name: "{group}\Edit congee config"; Filename: "{userappdata}\congee\congee.conf"
Name: "{group}\congee README"; Filename: "{app}\README.md"
Name: "{group}\Uninstall congee"; Filename: "{uninstallexe}"
Name: "{userdesktop}\congee"; Filename: "{app}\congee.exe"; Tasks: desktopicon; Components: dotnet
Name: "{userdesktop}\congee"; Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; Tasks: desktopicon; Check: PythonIsMain
; Only one edition at sign-in (two would draw on top of each other): .NET if installed
Name: "{userstartup}\congee"; Filename: "{app}\congee.exe"; Tasks: startup; Components: dotnet
Name: "{userstartup}\congee"; Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; Tasks: startup; Check: PythonIsMain

[Run]
Filename: "{code:LhmExe}"; Description: "Start LibreHardwareMonitor now (asks for admin)"; Verb: "runas"; Flags: postinstall shellexec nowait skipifsilent; Check: LhmInstalled
Filename: "{app}\congee.exe"; Description: "Start congee now"; Flags: postinstall nowait skipifsilent; Components: dotnet; Check: HasDesktopRuntime10
Filename: "{app}\python\.venv\Scripts\pythonw.exe"; Parameters: """{app}\python\congee.py"""; WorkingDir: "{app}\python"; Description: "Start congee now"; Flags: postinstall nowait skipifsilent; Check: PythonIsMain and VenvReady

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im congee.exe /f"; Flags: runhidden; RunOnceId: "KillDotnet"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Command ""Get-CimInstance Win32_Process -Filter \""Name='pythonw.exe'\"" | Where-Object CommandLine -like '*congee.py*' | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }"""; Flags: runhidden; RunOnceId: "KillPython"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\python\.venv"
Type: filesandordirs; Name: "{app}\python\__pycache__"

[Messages]
FinishedLabel=Setup has installed [name] on your computer.%n%nYour config file is %APPDATA%\congee\congee.conf (Start menu → "Edit congee config"). Save it and the widget updates on its own.%n%nFor temperatures and fans after a reboot, tick Options → Run On Windows Startup in LibreHardwareMonitor.

[Code]
const
  DEP_PY = 0;
  DEP_NET = 1;
  DEP_LHM = 2;
  WINGET_FLAGS = ' -e --accept-package-agreements --accept-source-agreements --disable-interactivity';

var
  DepPage: TInputOptionWizardPage;
  InstallPy, InstallNet, InstallLhm: Boolean;

{ ---------------------------------------------------------------- detection }

function WingetExe(): String;
begin
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

procedure InitializeWizard();
var
  Sub: String;
begin
  Sub := 'congee can install anything missing for you with winget, Windows'' built-in package manager. ' +
         'Ticked items are downloaded and installed when you click Install, each under its own license. ' +
         'Untick anything you would rather install yourself.';
  if WingetExe() = '' then
    Sub := 'winget (Windows'' package manager) was not found, so missing items cannot be installed automatically. ' +
           'Install them yourself (see the README), then run this setup again or just start congee.';
  DepPage := CreateInputOptionPage(wpSelectTasks, 'Dependencies', 'Software congee needs or can use', Sub, False, True);
  DepPage.Add('Python 3.12');
  DepPage.Add('.NET 10 Desktop Runtime');
  DepPage.Add('LibreHardwareMonitor (optional): temperatures, fans and GPU stats; asks for admin when it runs');
end;

procedure SetDep(Index: Integer; Needed, Present: Boolean; NeededFor: String);
begin
  DepPage.CheckListBox.ItemEnabled[Index] := Needed and not Present and (WingetExe() <> '');
  if Present then
  begin
    DepPage.CheckListBox.ItemSubItem[Index] := 'Already installed';
    DepPage.Values[Index] := False;
  end
  else if not Needed then
  begin
    DepPage.CheckListBox.ItemSubItem[Index] := 'Not needed';
    DepPage.Values[Index] := False;
  end
  else
  begin
    DepPage.CheckListBox.ItemSubItem[Index] := 'Missing: ' + NeededFor;
    DepPage.Values[Index] := WingetExe() <> '';
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = DepPage.ID then
  begin
    SetDep(DEP_PY, WizardIsComponentSelected('python'), FindPython312() <> '', 'needed for the Python edition');
    SetDep(DEP_NET, WizardIsComponentSelected('dotnet'), HasDesktopRuntime10(), 'needed for the .NET edition');
    SetDep(DEP_LHM, True, LhmInstalled(), 'for temperatures, fans and GPU stats');
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Missing: String;
begin
  Result := True;
  if CurPageID = DepPage.ID then
  begin
    InstallPy := DepPage.Values[DEP_PY];
    InstallNet := DepPage.Values[DEP_NET];
    InstallLhm := DepPage.Values[DEP_LHM];
    Missing := '';
    if WizardIsComponentSelected('python') and not InstallPy and (FindPython312() = '') then
      Missing := Missing + #13#10 + '  - Python 3.12 (the Python edition will not run without it)';
    if WizardIsComponentSelected('dotnet') and not InstallNet and not HasDesktopRuntime10() then
      Missing := Missing + #13#10 + '  - .NET 10 Desktop Runtime (the .NET edition will not run without it)';
    if Missing <> '' then
      Result := MsgBox('These are still missing and won''t be installed:' + Missing + #13#10#13#10 +
                       'Continue anyway? You can install them later and congee will work then.',
                       mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

{ ------------------------------------------------------------ installation }

procedure SetStatus(S: String);
begin
  WizardForm.StatusLabel.Caption := S;
  WizardForm.StatusLabel.Update;
end;

function RunWinget(Id, Extra, Title: String): Boolean;
var
  Code: Integer;
begin
  SetStatus('Installing ' + Title + ' with winget (this can take a few minutes)...');
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    Result := Exec(WingetExe(), 'install --id ' + Id + WINGET_FLAGS + Extra, '', SW_SHOWMINNOACTIVE,
                   ewWaitUntilTerminated, Code) and (Code = 0);
    if not Result then
      MsgBox(Title + ' could not be installed automatically (winget exit code ' + IntToStr(Code) + ').' + #13#10#13#10 +
             'You can install it later from a terminal with:' + #13#10 + '  winget install --id ' + Id,
             mbError, MB_OK);
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
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
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im congee.exe /f', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name=''pythonw.exe''\" | Where-Object CommandLine -like ''*congee.py*'' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCongee();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if InstallNet then RunWinget('Microsoft.DotNet.DesktopRuntime.10', '', '.NET 10 Desktop Runtime');
    if InstallPy then RunWinget('Python.Python.3.12', ' --scope user', 'Python 3.12');
    if InstallLhm then RunWinget('LibreHardwareMonitor.LibreHardwareMonitor', '', 'LibreHardwareMonitor');
  end;
  if CurStep = ssPostInstall then
  begin
    if WizardIsComponentSelected('python') then SetupPythonVenv();
    ConfigureLhm();
  end;
end;
