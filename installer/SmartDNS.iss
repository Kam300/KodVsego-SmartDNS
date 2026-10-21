#ifndef SourceExe
  #error SourceExe must point to a built Smart DNS executable
#endif
#ifndef OutputPath
  #error OutputPath is required
#endif
#define AppVersionText GetStringFileInfo(SourceExe, "ProductVersion")
#define AppFileVersion GetStringFileInfo(SourceExe, "FileVersion")
#ifdef TestId
  #define Identity "KodVsego.SmartDNS.PackagingTest." + TestId
  #define DisplayName "Smart DNS Packaging Test " + TestId
#else
  #define Identity "KodVsego.SmartDNS"
  #define DisplayName "Код Всего · Smart DNS"
#endif

[Setup]
AppId={#Identity}
AppName={#DisplayName}
AppVersion={#AppVersionText}
AppVerName={#DisplayName} {#AppVersionText}
AppPublisher=Код Всего
AppPublisherURL=https://t.me/TotalC0de
AppSupportURL=https://github.com/Kam300/KodVsego-SmartDNS/issues
AppUpdatesURL=https://github.com/Kam300/KodVsego-SmartDNS/releases
DefaultDirName={localappdata}\Programs\KodVsego\SmartDNS
DefaultGroupName={#DisplayName}
PrivilegesRequired=lowest
MinVersion=10.0
OutputDir={#OutputPath}
OutputBaseFilename=KodVsego_SmartDNS.Setup.{#AppVersionText}
SetupIconFile=..\res\app.ico
UninstallDisplayIcon={app}\SmartDNS.exe
UninstallDisplayName={#DisplayName}
VersionInfoVersion={#AppFileVersion}
VersionInfoProductName={#DisplayName}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersionText}
LicenseFile=license-screen.ru.txt
InfoBeforeFile=install-info.ru.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UsePreviousTasks=yes
CloseApplications=yes
CloseApplicationsFilter=SmartDNS.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "SmartDNS.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.ru.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\res\OFL-Unbounded.txt"; DestDir: "{app}\res"; Flags: ignoreversion
Source: "..\res\OFL-Manrope.txt"; DestDir: "{app}\res"; Flags: ignoreversion
Source: "..\docs\licenses\HB-AI-LICENSE.ru.md"; DestDir: "{app}\docs\licenses"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#DisplayName}"; Filename: "{app}\SmartDNS.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\{#DisplayName}"; Filename: "{app}\SmartDNS.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\SmartDNS.exe"; Description: "Запустить Smart DNS"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersion: String;
  OldVersion, NewVersion: Int64;
begin
  Result := '';
  if GetVersionNumbersString(ExpandConstant('{app}\SmartDNS.exe'), ExistingVersion) then
    if StrToVersion(ExistingVersion, OldVersion) and StrToVersion('{#AppFileVersion}', NewVersion) then
    if ComparePackedVersion(OldVersion, NewVersion) > 0 then
      Result := 'В этой папке уже установлена более новая версия Smart DNS. Используйте её или выберите другую папку.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not UninstallSilent then
    Result := MsgBox('Перед удалением рекомендуется выключить DNS в программе для восстановления прежних настроек.' + #13#10 + #13#10 + 'Удаление программы само не изменяет DNS. Настройки и резервные копии останутся на компьютере.' + #13#10 + #13#10 + 'Продолжить удаление?', mbConfirmation, MB_YESNO) = IDYES;
end;