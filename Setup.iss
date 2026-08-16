; Archivo de Script para Inno Setup
; Este script creará un instalador para AnimeLocalTracker

[Setup]
; Información básica de la aplicación
AppName=AnimeLocalTracker
AppVersion=1.0.0
AppPublisher=TuNombre
AppPublisherURL=https://tu-sitio-web.com/
AppSupportURL=https://tu-sitio-web.com/
AppUpdatesURL=https://tu-sitio-web.com/

; El nombre del archivo instalador generado (Ej. Setup_AnimeTracker_v1.0.0.exe)
OutputBaseFilename=Setup_AnimeTracker_v1.0
; Dónde se guardará el instalador generado
OutputDir=..\InstaladorGenerado

; Dónde se instalará por defecto (Ej: C:\Archivos de Programa\AnimeLocalTracker)
DefaultDirName={autopf}\AnimeLocalTracker
; Carpeta del menú de inicio
DefaultGroupName=AnimeLocalTracker

; Opciones de compresión y estilo
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes
PrivilegesRequired=admin
SetupIconFile=compiler:SetupClassicIcon.ico

[Tasks]
; Tarea para crear icono en el escritorio (opcional, marcado por defecto)
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Aquí le decimos a Inno Setup que copie todos los archivos generados en "publish_output"
; Nota: El origen (Source) asume que este script está en la carpeta raíz del proyecto (al lado de AnimeLocalTracker.sln)
Source: "AnimeLocalTracker\publish_output\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Crear acceso directo en el Menú de Inicio
Name: "{group}\AnimeLocalTracker"; Filename: "{app}\AnimeLocalTracker.exe"
; Crear acceso directo en el Escritorio si el usuario marcó la casilla
Name: "{autodesktop}\AnimeLocalTracker"; Filename: "{app}\AnimeLocalTracker.exe"; Tasks: desktopicon

[Run]
; Ofrecer ejecutar la aplicación al terminar de instalar
Filename: "{app}\AnimeLocalTracker.exe"; Description: "{cm:LaunchProgram,AnimeLocalTracker}"; Flags: nowait postinstall skipifsilent
