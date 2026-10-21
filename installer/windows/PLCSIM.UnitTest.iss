#ifndef SourceRoot
  #error SourceRoot must be defined by build-windows-installer.ps1
#endif
#ifndef DistributionVersion
  #error DistributionVersion must be defined by build-windows-installer.ps1
#endif
#ifndef WindowsVersion
  #error WindowsVersion must be defined by build-windows-installer.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be defined by build-windows-installer.ps1
#endif
#ifndef LicenseFile
  #error LicenseFile must be defined by build-windows-installer.ps1
#endif
#ifndef SetupIconFile
  #error SetupIconFile must be defined by build-windows-installer.ps1
#endif
#ifndef WizardImageFile
  #error WizardImageFile must be defined by build-windows-installer.ps1
#endif
#ifndef WizardSmallImageFile
  #error WizardSmallImageFile must be defined by build-windows-installer.ps1
#endif
#ifndef GeneratedComponentsFile
  #error GeneratedComponentsFile must be defined by build-windows-installer.ps1
#endif
#ifndef GeneratedFilesFile
  #error GeneratedFilesFile must be defined by build-windows-installer.ps1
#endif

#define ProductName "PLCSIM.UnitTest"
#define PublisherName "Lorenz Software"
#define PublisherUrl "https://www.lorenz-software.solutions/"
#define WpfExecutable "PLCSIM.UnitTest.App.Wpf.exe"
#define CliExecutable "PLCSIM.UnitTest.App.Cli.exe"

[Setup]
AppId={{7B251A57-B749-4FA8-96BA-E64743341D52}
AppName={#ProductName}
AppVersion={#DistributionVersion}
AppVerName={#ProductName}
AppPublisher={#PublisherName}
AppPublisherURL={#PublisherUrl}
VersionInfoVersion={#WindowsVersion}
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#WindowsVersion}
VersionInfoProductTextVersion={#DistributionVersion}
VersionInfoCompany={#PublisherName}
DefaultDirName={autopf}\PLCSIM.UnitTest
DefaultGroupName=PLCSIM.UnitTest
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#LicenseFile}
SetupIconFile={#SetupIconFile}
WizardImageFile={#WizardImageFile}
WizardSmallImageFile={#WizardSmallImageFile}
WizardImageBackColor=#F7F8F4
UninstallDisplayIcon={app}\{#WpfExecutable}
UninstallFilesDir={app}\.uninstall
OutputDir={#OutputDirectory}
OutputBaseFilename=PLCSIM.UnitTest-Setup-{#DistributionVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
CloseApplications=yes
RestartApplications=no

[Types]
Name: "full"; Description: "Typical installation"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
#include GeneratedComponentsFile

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
#include GeneratedFilesFile
Source: "RegisterAccessProfiles.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PLCSIM.UnitTest"; Filename: "{app}\{#WpfExecutable}"; WorkingDir: "{app}"
Name: "{group}\Register Access Profiles"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\RegisterAccessProfiles.ps1"""; WorkingDir: "{app}"; IconFilename: "{app}\{#WpfExecutable}"
Name: "{group}\Installation Directory"; Filename: "{app}"
Name: "{autodesktop}\PLCSIM.UnitTest"; Filename: "{app}\{#WpfExecutable}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#CliExecutable}"; Parameters: "register-profiles"; WorkingDir: "{app}"; Description: "Register access profiles (valid only for the user who started Setup)"; Flags: postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
var
  RemoveCurrentUserData: Boolean;
  RemoveInstallerOwnedInstallRoot: Boolean;

function IsInstallerOwnedInstallRoot: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\distribution\bundle-manifest.json'));
end;

function CleanInstallRootContents: String;
var
  InstallRoot: String;
  EntryPath: String;
  FindRec: TFindRec;
begin
  Result := '';
  InstallRoot := ExpandConstant('{app}');
  if not FindFirst(AddBackslash(InstallRoot) + '*', FindRec) then
    Exit;

  try
    repeat
      if (FindRec.Name <> '.') and
         (FindRec.Name <> '..') and
         (CompareText(FindRec.Name, '.uninstall') <> 0) then
      begin
        EntryPath := AddBackslash(InstallRoot) + FindRec.Name;
        if DirExists(EntryPath) then
        begin
          if not DelTree(EntryPath, True, True, True) then
          begin
            Result := 'Setup could not replace the existing installer-owned directory: ' + EntryPath;
            Exit;
          end;
        end
        else if not DeleteFile(EntryPath) then
        begin
          Result := 'Setup could not replace the existing installer-owned file: ' + EntryPath;
          Exit;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function CleanInstallerOwnedInstallRoot: String;
begin
  Result := '';
  if IsInstallerOwnedInstallRoot then
    Result := CleanInstallRootContents;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CleanInstallerOwnedInstallRoot;
end;

function HasCommandLineParameter(Value: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsDotNetDesktopRuntime8Installed: Boolean;
var
  RuntimeRoot: String;
  FindRec: TFindRec;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(AddBackslash(RuntimeRoot) + '8.*', FindRec) then
  begin
    repeat
      if DirExists(AddBackslash(RuntimeRoot) + FindRec.Name) then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
end;

function InitializeUninstall: Boolean;
var
  PromptResult: Integer;
  UserDataPath: String;
begin
  Result := True;
  RemoveInstallerOwnedInstallRoot := IsInstallerOwnedInstallRoot;
  RemoveCurrentUserData := not HasCommandLineParameter('/KEEPUSERDATA');
  if UninstallSilent or not RemoveCurrentUserData then
    Exit;

  UserDataPath := ExpandConstant('{localappdata}\PLCSIM.UnitTest');
  PromptResult := MsgBox(
    'Remove PLCSIM.UnitTest application data for the current Windows account?' + #13#10 + #13#10 +
    'This removes settings, logs, caches, and application profile registrations from:' + #13#10 +
    UserDataPath + #13#10 + #13#10 +
    'Choose No to keep this data.',
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON1);
  RemoveCurrentUserData := PromptResult = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CleanupError: String;
  UserDataPath: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  if RemoveInstallerOwnedInstallRoot then
  begin
    CleanupError := CleanInstallRootContents;
    if CleanupError <> '' then
    begin
      Log(CleanupError);
      if not UninstallSilent then
        MsgBox(
          'PLCSIM.UnitTest was uninstalled, but some installer-owned files could not be removed:' + #13#10 +
          ExpandConstant('{app}'),
          mbError,
          MB_OK);
    end;
  end;

  if not RemoveCurrentUserData then
    Exit;

  UserDataPath := ExpandConstant('{localappdata}\PLCSIM.UnitTest');
  if DirExists(UserDataPath) and not DelTree(UserDataPath, True, True, True) then
  begin
    Log('Could not completely remove current-account application data: ' + UserDataPath);
    if not UninstallSilent then
      MsgBox(
        'PLCSIM.UnitTest was uninstalled, but some current-account application data could not be removed:' + #13#10 +
        UserDataPath,
        mbError,
        MB_OK);
  end;
end;

function IsDotNetFramework48Installed: Boolean;
var
  ReleaseValue: Cardinal;
begin
  Result :=
    RegQueryDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
      'Release',
      ReleaseValue) and
    (ReleaseValue >= 528040);
end;

function InitializeSetup: Boolean;
var
  MissingPrerequisites: String;
begin
  MissingPrerequisites := '';
  if not IsDotNetDesktopRuntime8Installed then
    MissingPrerequisites := MissingPrerequisites + '- .NET 8 Desktop Runtime x64' + #13#10;
  if not IsDotNetFramework48Installed then
    MissingPrerequisites := MissingPrerequisites + '- .NET Framework 4.8' + #13#10;

  Result := MissingPrerequisites = '';
  if not Result then
    MsgBox(
      'PLCSIM.UnitTest requires the following installed prerequisites:' + #13#10 + #13#10 +
      MissingPrerequisites + #13#10 +
      'Install the missing runtime components and start setup again.',
      mbCriticalError,
      MB_OK);
end;
