; FORK (feed-overlay): Inno Setup installer for KikiVRCTan.
;
; Single KikiVRCTan-Setup.exe. Default install folder is the directory the
; setup itself is in (when that folder holds nothing but the setup);
; otherwise it defaults to %LocalAppData%\KikiVRCTan. The user can change it
; on the directory page. Everything (app, config.json, logs) stays in that
; one folder - no %APPDATA% usage.
;
; Build (Windows):
;   1. dotnet publish KikitanTranslator.WinForms/KikitanTranslator.WinForms.csproj -c Release -r win-x64 --self-contained true -o KikitanTranslator.WinForms/publish/app
;   2. dotnet publish KikitanTranslator.Overlay/KikitanTranslator.Overlay.csproj -c Release -r win-x64 --self-contained true -o KikitanTranslator.WinForms/publish/app
;   3. iscc /DPublishDir="<repo>\KikitanTranslator.WinForms\publish\app" /DIconFile="<repo>\KikitanTranslator.Photino\Resources\wwwroot\kikitan_logo.ico" installer\kikivrctan.iss
; (Or let .github/workflows/winforms-installer.yml do it.)

#define MyAppName "KikiVRCTan"
#define MyAppVersion "2.1.0"
#define MyAppExeName "KikitanTranslator.WinForms.exe"

#ifndef PublishDir
  #define PublishDir "..\KikitanTranslator.WinForms\publish\app"
#endif
#ifndef IconFile
  #define IconFile "..\KikitanTranslator.Photino\Resources\wwwroot\kikitan_logo.ico"
#endif

[Setup]
AppId={{AB09EE90-461C-41FD-9528-1E1C9636ABBF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={src}
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=KikiVRCTan-Setup
SetupIconFile={#IconFile}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Code]
{ True when the setup's own folder holds nothing but the setup itself. }
function IsSrcDirEmpty(): Boolean;
var
  FindRec: TFindRec;
  Count: Integer;
begin
  Count := 0;
  if FindFirst(ExpandConstant('{src}\*'), FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..')
         and (CompareText(FindRec.Name, 'KikiVRCTan-Setup.exe') <> 0) then
        Inc(Count);
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
  Result := Count = 0;
end;

procedure InitializeWizard();
begin
  { Running from e.g. Downloads (not empty)? Default to LocalAppData instead
    of polluting the download folder. }
  if not IsSrcDirEmpty() then
    WizardForm.DirEdit.Text := ExpandConstant('{localappdata}\KikiVRCTan');
end;
