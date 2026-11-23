; =====================================================================================================================
; RedChat — script Inno Setup 6.3 ou plus récent  (https://jrsoftware.org/isinfo.php)
;
; Ne se lance pas à la main : utilisez  installer\build-installer.ps1  (il publie l'application PUIS compile ce script
; avec les bons paramètres /DMyAppVersion=… et /DPublishDir=…).
;
; CHOIX DE CONCEPTION (le « pourquoi ») :
;  • Installation par utilisateur par défaut (aucun droit administrateur) ; l'assistant propose « pour tous les
;    utilisateurs » via une boîte de dialogue (PrivilegesRequiredOverridesAllowed=dialog).
;  • Le runtime .NET est EMBARQUÉ dans la publication (self-contained) : aucun prérequis à installer sur le PC cible.
;  • Mise à jour = relancer un installateur plus récent : même AppId → remplacement sur place, données conservées.
;  • Les données de l'utilisateur (conversations, contacts, CLÉ D'IDENTITÉ) vivent dans %LOCALAPPDATA%\RedChat : elles
;    ne sont JAMAIS supprimées sans une question explicite à la désinstallation (réponse par défaut : Non).
;  • Pare-feu : règle optionnelle (administrateur seulement), limitée au profil réseau « Privé » et à RedChat.exe.
; =====================================================================================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

#define MyAppName "RedChat"
#define MyAppExeName "RedChat.exe"
#define MyAppPublisher "RedChat"
#define FirewallRuleName "RedChat"

[Setup]
; AppId identifie l'application pour les mises à jour et la désinstallation : NE JAMAIS LE CHANGER après diffusion.
; (les deux accolades ouvrantes sont l'échappement Inno d'une seule)
AppId={{B06EE9E5-9114-4CA5-8FFD-052FFBD38760}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Installateur de {#MyAppName}

DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Windows 10 1809 minimum (cohérent avec la cible net10.0-windows10.0.17763.0), 64 bits uniquement.
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Si RedChat tourne pendant la mise à jour, Windows (Restart Manager) lui demande de se fermer proprement.
CloseApplications=yes
RestartApplications=no

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\RedChat.Wpf\Assets\redchat.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
InfoBeforeFile=info-avant-installation.txt
OutputDir=Output
OutputBaseFilename=RedChat-Setup-{#MyAppVersion}
; Journal d'installation dans %TEMP% (Setup Log …txt) : utile pour comprendre un échec chez un utilisateur.
SetupLogging=yes
; SIGNATURE (optionnelle) : avec un certificat de signature de code, déclarez un outil dans Inno Setup puis activez
;   SignTool=monsigntool $f      et      SignedUninstaller=yes
; Sans signature, Windows SmartScreen affiche « éditeur inconnu » au premier lancement (voir README-INSTALLATEUR.md).

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le &Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked
; Proposée seulement si l'installation se fait pour tous les utilisateurs (la règle de pare-feu exige les droits administrateur).
Name: "firewall"; Description: "Autoriser RedChat dans le pare-feu Windows (réseaux privés uniquement)"; GroupDescription: "Réseau local :"; Check: IsAdminInstallMode

[Files]
; Pas de symboles de débogage ni de documentation XML dans le programme livré.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Règle entrante limitée à CE programme (TCP et UDP : découverte UDP 45821 + connexions TCP) et au profil « Privé ».
; On supprime d'abord une éventuelle règle existante pour ne jamais créer de doublon lors d'une mise à jour.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRuleName}"" program=""{app}\{#MyAppExeName}"""; Flags: runhidden; Tasks: firewall; StatusMsg: "Configuration du pare-feu…"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRuleName}"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes profile=private"; Flags: runhidden; Tasks: firewall; StatusMsg: "Configuration du pare-feu…"
Filename: "{app}\{#MyAppExeName}"; Description: "Lancer {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRuleName}"" program=""{app}\{#MyAppExeName}"""; Flags: runhidden; RunOnceId: "RemoveRedChatFirewallRule"; Check: IsAdminInstallMode

[Code]
{ Dossier des données utilisateur : profils (identité chiffrée DPAPI, contacts, historique), pièces jointes, statuts, journaux. }
function UserDataDir(): string;
begin
  Result := ExpandConstant('{localappdata}\{#MyAppName}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Après la suppression des programmes seulement ; jamais en désinstallation silencieuse. }
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) and DirExists(UserDataDir()) then
  begin
    if MsgBox('Voulez-vous aussi supprimer vos données RedChat ?' + #13#10 + #13#10 +
              'Cela efface vos conversations, vos contacts, vos statuts, les pièces jointes reçues ET votre identité cryptographique : ' +
              'vos contacts ne vous reconnaîtront plus si vous réinstallez RedChat.' + #13#10 + #13#10 +
              'Cette action est IRRÉVERSIBLE. Choisissez « Non » pour conserver vos données (recommandé).',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(UserDataDir(), True, True, True);
    end;
  end;
end;
