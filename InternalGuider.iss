; Installeur du plugin NINA "Internal Guider" (Inno Setup 6, gratuit : https://jrsoftware.org/isinfo.php)
;
; Mode d'emploi :
;   1. Compiler le projet en Release (Générer > Régénérer la solution, configuration Release, NINA fermé).
;   2. Ouvrir ce fichier dans Inno Setup, puis Build > Compile (ou F9).
;   3. L'installeur est créé dans le dossier "output" : InternalGuider-Setup-<version>.exe
;
; Ce fichier peut se trouver dans le dossier du projet ou dans son sous-dossier "installer" : la DLL est cherchée dans les deux cas.

#define MyAppName "Internal Guider (plugin NINA)"
; À renseigner avant diffusion : votre nom et l'adresse du dépôt GitHub
#define MyAppPublisher "fredM1968"
#define MyAppUrl "https://github.com/fredM1968/nina-internal-guider"
; Chemin de la DLL, cherché automatiquement : ce fichier peut être dans le dossier du projet ou dans son sous-dossier "installer",
; et la compilation peut sortir dans bin\x64\Release (plateforme x64) ou bin\Release.
#define DllName "NINA.Plugin.InternalGuider.dll"
; Emplacements possibles de la DLL (selon plateforme x64 ou non, et selon l'emplacement de ce fichier).
; C'est la DLL la PLUS RÉCENTE qui est utilisée : une ancienne DLL restée dans un autre dossier ne peut plus être choisie par erreur.
#define Cand1 "bin\x64\Release\net8.0-windows\" + DllName
#define Cand2 "..\bin\x64\Release\net8.0-windows\" + DllName
#define Cand3 "bin\Release\net8.0-windows\" + DllName
#define Cand4 "..\bin\Release\net8.0-windows\" + DllName
#define Stamp1 FileExists(SourcePath + Cand1) ? GetFileDateTimeString(SourcePath + Cand1, 'yyyymmddhhnnss', '', '') : "0"
#define Stamp2 FileExists(SourcePath + Cand2) ? GetFileDateTimeString(SourcePath + Cand2, 'yyyymmddhhnnss', '', '') : "0"
#define Stamp3 FileExists(SourcePath + Cand3) ? GetFileDateTimeString(SourcePath + Cand3, 'yyyymmddhhnnss', '', '') : "0"
#define Stamp4 FileExists(SourcePath + Cand4) ? GetFileDateTimeString(SourcePath + Cand4, 'yyyymmddhhnnss', '', '') : "0"
#define DllPath Cand4
#define BestStamp Stamp4
#if Stamp3 > BestStamp
  #undef DllPath
  #undef BestStamp
  #define DllPath Cand3
  #define BestStamp Stamp3
#endif
#if Stamp2 > BestStamp
  #undef DllPath
  #undef BestStamp
  #define DllPath Cand2
  #define BestStamp Stamp2
#endif
#if Stamp1 > BestStamp
  #undef DllPath
  #undef BestStamp
  #define DllPath Cand1
  #define BestStamp Stamp1
#endif
; La version est lue dans la DLL : augmentez la version de l'assembly (AssemblyInfo.cs) avant chaque diffusion.
#define MyAppVersion GetVersionNumbersString(DllPath)
; Affiche dans la fenêtre de compilation d'Inno Setup la DLL retenue et sa version : vérifiez-la.
#pragma message "DLL utilisée : " + SourcePath + DllPath + "  (version " + MyAppVersion + ", date " + BestStamp + ")"

[Setup]
AppId={{B7E0F2A4-3C5D-4E8B-9A61-2D4F7C1E8A53}
AppName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
; Le plugin va dans le dossier des plugins de l'utilisateur : pas besoin de droits administrateur
DefaultDirName={localappdata}\NINA\Plugins\3.0.0\InternalGuider
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=InternalGuider-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
; NINA charge la DLL : si NINA est ouvert, l'installeur propose de le fermer (gestionnaire de redémarrage de Windows)
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#MyAppName}
InfoBeforeFile=lisez-moi.txt

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "{#DllPath}"; DestDir: "{app}"; Flags: ignoreversion
; Pour inclure aussi la licence : Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[UninstallDelete]
; Le dossier du plugin est supprimé avec lui. Les réglages, calibrations, darks et journaux restent dans
; %LOCALAPPDATA%\NINA\InternalGuider : ils ne sont pas touchés par une mise à jour ni par la désinstallation.
Type: dirifempty; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox('Le plugin est installé.' + #13#10 + 'Démarrez (ou redémarrez) NINA : "Internal Guider" apparaît dans la liste des guideurs, onglet Équipement > Guidage.',
           mbInformation, MB_OK);
end;
