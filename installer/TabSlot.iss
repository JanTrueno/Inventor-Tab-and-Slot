; Inno Setup script for the Tab and Slot add-in (Inventor 2025).
; Build with build-installer.ps1, which compiles the add-in first and passes the version.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define BuildDir "..\bin\Release\net8.0-windows"

[Setup]
AppId={{e8c41b7a-2d56-4f93-a0b1-7f3d5c9e6b12}
AppName=Tab and Slot for Inventor
AppVersion={#AppVersion}
AppPublisher=JanTrueno
AppPublisherURL=https://github.com/JanTrueno/Inventor-Tab-and-Slot
DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\TabSlotManager.bundle
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=TabSlot.ico
UninstallDisplayIcon={app}\TabSlot.ico
UninstallDisplayName=Tab and Slot for Inventor 2025
WizardStyle=modern
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
OutputDir=..\dist
OutputBaseFilename=TabSlot-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=no

[Files]
Source: "..\deploy\PackageContents.xml";          DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt";             DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";                             DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "TabSlot.ico";                            DestDir: "{app}"; Flags: ignoreversion
Source: "..\deploy\TabSlotAddIn.Inventor.addin";  DestDir: "{app}\Contents\2025"; Flags: ignoreversion
Source: "{#BuildDir}\TabSlotAddIn.dll";           DestDir: "{app}\Contents\2025"; Flags: ignoreversion
Source: "{#BuildDir}\TabSlotAddIn.pdb";           DestDir: "{app}\Contents\2025"; Flags: ignoreversion
Source: "{#BuildDir}\TabSlotAddIn.deps.json";     DestDir: "{app}\Contents\2025"; Flags: ignoreversion

[Code]
function InventorRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // tasklist exits 0 either way; findstr sets the result when Inventor.exe is listed
  Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Inventor.exe" | findstr /I "Inventor.exe" >NUL',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 0;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  while InventorRunning() do
  begin
    if MsgBox('Inventor is running. Close Inventor, then click OK to continue.', mbInformation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  while InventorRunning() do
  begin
    if MsgBox('Inventor is running. Close Inventor, then click OK to continue.', mbInformation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
