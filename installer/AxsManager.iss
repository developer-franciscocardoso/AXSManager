#define AppVersion "1.0.5"

[Setup]
AppId={{F539094C-9C09-48CA-9D33-0D7D6AC5572C}
AppName=AXS Manager
AppVersion={#AppVersion}
AppPublisher=AXS Manager
DefaultDirName={autopf}\AXS Manager
DefaultGroupName=AXS Manager
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\dist
OutputBaseFilename=AXS-Manager-Setup-{#AppVersion}-offline
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=6.2
UninstallDisplayIcon={app}\AxsManager.exe
AppMutex=Local\AxsManager.UI.v1
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start the tray manager at sign-in for all users"; Flags: unchecked

[Files]
Source: "..\build\AxsManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\AxsManager.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\.tools\downloads\NDP48-x86-x64-AllOS-ENU.exe"; Flags: dontcopy nocompression

[Icons]
Name: "{group}\AXS Manager"; Filename: "{app}\AxsManager.exe"
Name: "{autodesktop}\AXS Manager"; Filename: "{app}\AxsManager.exe"; Tasks: desktopicon
Name: "{commonstartup}\AXS Manager"; Filename: "{app}\AxsManager.exe"; Parameters: "--tray"; Tasks: startup

[Run]
Filename: "{app}\AxsManager.exe"; Parameters: "--setup"; Description: "Open AXS Manager setup"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: CanLaunch

[Code]
var
  RuntimeRestart: Boolean;

function HasFramework48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release', Release) and (Release >= 528040);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if HasFramework48 then Exit;
  WizardForm.StatusLabel.Caption := 'Installing bundled Microsoft .NET Framework 4.8...';
  ExtractTemporaryFile('NDP48-x86-x64-AllOS-ENU.exe');
  if not Exec(ExpandConstant('{tmp}\NDP48-x86-x64-AllOS-ENU.exe'),
    '/q /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Could not start the bundled .NET Framework installer.';
    Exit;
  end;
  if (ExitCode = 3010) or (ExitCode = 1641) then
  begin
    RuntimeRestart := True;
    Exit;
  end;
  if (ExitCode <> 0) or not HasFramework48 then
    Result := '.NET Framework setup did not complete (exit code ' +
      IntToStr(ExitCode) + '). Install pending Windows updates, restart, and run setup again.';
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeRestart;
end;

function CanLaunch: Boolean;
begin
  Result := not RuntimeRestart and HasFramework48;
end;

procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption :=
    'Install AXS Manager to discover company/year folders and manage ARTSoft XML services.' + #13#10#13#10 +
    'This offline installer includes Microsoft .NET Framework 4.8 and installs it only if needed.' + #13#10#13#10 +
    'After installation, select your existing ARTSoft service folder and all Fich folders in Setup.' + #13#10#13#10 +
    'Uninstalling AXS Manager preserves ARTSoft services, their configurations, and company data.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupValue: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run', 'AxsManager', StartupValue) then
    if Lowercase(StartupValue) = Lowercase('"' +
      ExpandConstant('{app}\AxsManager.exe') + '" --tray') then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AxsManager');
end;
