; Установщик DynamicIsland.
;
; Приложение ставится в папку пользователя, а не в Program Files: прав
; администратора не требуется, и обновление не упирается в UAC. Для оверлея,
; который живёт в трее, это правильный компромисс.
;
; Сборка самодостаточная — .NET на машине не нужен.
;
; Собрать:  ISCC.exe installer\DynamicIsland.iss

#define AppName        "DynamicIsland"

; Номер версии не пишется здесь руками. Он берётся из собранного exe, а туда
; попадает из <Version> в src\DynamicIsland.App\DynamicIsland.App.csproj —
; так установщик физически не может разъехаться с тем, что внутри него лежит.
; Список выпусков — в CHANGELOG.md.
#define PublishedExe   SourcePath + "..\publish\DynamicIsland.exe"
#ifndef AppVersion
  #define AppVersion   GetStringFileInfo(PublishedExe, "ProductVersion")
#endif

#define AppPublisher   "FSDITM"
#define AppUrl         "https://github.com/FSDITM/DynamicIsland"
#define AppExe         "DynamicIsland.exe"

[Setup]
AppId={{8F3A6C1E-4B77-4B1E-9E24-5A1D0C7E9B42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; Без прав администратора: ставим в профиль пользователя.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist
OutputBaseFilename=DynamicIsland-{#AppVersion}-setup
SetupIconFile=..\src\DynamicIsland.App\Resources\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern

; Сборка большая, поэтому жмём сильнее обычного.
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Островок рисуется через DirectComposition и Direct3D 12.
MinVersion=10.0.19041

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "{cm:AutoStartDescription}"; GroupDescription: "{cm:AdditionalOptions}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalOptions}"; Flags: unchecked

[CustomMessages]
russian.AutoStartDescription=Запускать при входе в систему
russian.AdditionalOptions=Дополнительно:
russian.CreateDesktopIcon=Создать ярлык на рабочем столе
russian.LaunchApp=Запустить {#AppName}
english.AutoStartDescription=Start when I sign in
english.AdditionalOptions=Additional options:
english.CreateDesktopIcon=Create a desktop shortcut
english.LaunchApp=Launch {#AppName}

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Автозапуск. Приложение умеет включать его само из настроек, но при установке
; удобнее сразу: оверлей без автозапуска большого смысла не имеет.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Работающий островок держит свои файлы — закрываем перед удалением.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopIsland"

[Code]
// Перед установкой поверх старой версии приложение надо остановить, иначе
// файлы окажутся заняты и установка прервётся на середине.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
