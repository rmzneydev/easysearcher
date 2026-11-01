; Instalador de EasySearcher (Inno Setup 6). Se compila con tools\build-installer.ps1, que primero
; publica la app y el servicio en artifacts\publish y pasa la version con /DAppVersion=x.y.z

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "EasySearcher"
#define AppExe "EasySearcher.exe"
#define ServiceExe "EasySearcher.Service.exe"
#define ServiceName "EasySearcher"

[Setup]
AppId={{846B453B-62BC-446D-A507-87097A912B6D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Neyder R
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; El servicio y el menu contextual para todos los usuarios requieren administrador
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=EasySearcherSetup-{#AppVersion}
SetupIconFile=..\src\EasySearcher.App\Assets\EasySearcher.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Cierra la app si esta abierta al actualizar o desinstalar
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "contextmenu"; Description: "Agregar ""Buscar con EasySearcher"" al clic derecho del Explorador"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Clic derecho sobre una carpeta, en el fondo de una carpeta abierta y sobre una unidad (%V llega como D:\ y la app lo corrige)
Root: HKLM; Subkey: "Software\Classes\Directory\shell\{#AppName}"; ValueType: string; ValueData: "Buscar con EasySearcher"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Directory\shell\{#AppName}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Directory\shell\{#AppName}\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%V"""; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Directory\Background\shell\{#AppName}"; ValueType: string; ValueData: "Buscar con EasySearcher"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Directory\Background\shell\{#AppName}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Directory\Background\shell\{#AppName}\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%V"""; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Drive\shell\{#AppName}"; ValueType: string; ValueData: "Buscar con EasySearcher"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Drive\shell\{#AppName}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKLM; Subkey: "Software\Classes\Drive\shell\{#AppName}\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%V"""; Tasks: contextmenu

[Run]
; Registrar e iniciar el servicio. Si se cae, Windows lo reinicia a los 5 s (dos veces por dia)
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\{#ServiceExe}\"""" start= auto DisplayName= ""EasySearcher (indice de archivos)"""; Flags: runhidden; StatusMsg: "Registrando el servicio..."
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Mantiene en memoria el indice de archivos de las unidades NTFS para la app EasySearcher."""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000//"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden; StatusMsg: "Iniciando el servicio..."
; runasoriginaluser: la app se abre como el usuario normal, no con los permisos del instalador
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
// Antes de copiar archivos se detiene y elimina el servicio existente (actualizacion, o la instalacion de
// desarrollo), para que su ejecutable no este bloqueado y se registre con la ruta nueva.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // SCM elimina el servicio de forma asincrona
  Sleep(1500);
  Result := '';
end;
