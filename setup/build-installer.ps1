<#
.SYNOPSIS
    Construit l'installateur Windows de RedChat (RedChat-Setup-<version>.exe).

.DESCRIPTION
    Étapes : (1) tests  (2) publication autonome win-x64 (le runtime .NET est inclus)  (3) compilation du script
    Inno Setup  (4) empreinte SHA-256 du fichier produit.
    Résultat : setup\Output\RedChat-Setup-<version>.exe (+ .sha256).

.PARAMETER Version
    Numéro de version « 1.2.3 ». Par défaut : la valeur <Version> de src\RedChat.Wpf\RedChat.Wpf.csproj.

.PARAMETER SkipTests
    Ne lance pas « dotnet test » avant la publication (déconseillé : on ne livre pas du code non testé).

.PARAMETER InstallInno
    Installe Inno Setup avec winget s'il est absent.

.EXAMPLE
    .\setup\build-installer.ps1
.EXAMPLE
    .\setup\build-installer.ps1 -Version 1.1.0 -InstallInno
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$InstallInno
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'RedChat.sln'
$project = Join-Path $root 'src\RedChat.Wpf\RedChat.Wpf.csproj'
$script = Join-Path $PSScriptRoot 'RedChat.iss'
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
$outputDir = Join-Path $PSScriptRoot 'Output'

function Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

# Une commande native (dotnet, ISCC…) qui échoue ne lève PAS d'exception PowerShell : on contrôle le code de sortie.
function Assert-Success([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code de sortie $LASTEXITCODE)." }
}

# --- 0. un SDK .NET est-il réellement utilisable ? --------------------------------------------------------------------
# Piège fréquent : une commande « dotnet » existe dans le PATH (simple runtime, sans SDK) alors que le SDK installé par
# Rider se trouve dans %USERPROFILE%\.dotnet. On teste donc « dotnet --list-sdks », pas seulement la présence de dotnet.
function Test-DotnetSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $sdks = @(& dotnet --list-sdks 2>$null); $ok = ($LASTEXITCODE -eq 0) -and ($sdks.Count -gt 0) }
    catch { $ok = $false }
    finally { $ErrorActionPreference = $previous }
    return $ok
}

if (-not (Test-DotnetSdk)) {
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet'
    if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) {
        $env:DOTNET_ROOT = $userDotnet
        $env:PATH = "$userDotnet;$env:PATH"
        Write-Host "SDK .NET cherché dans $userDotnet (ajouté au PATH de cette fenêtre)."
    }
    if (-not (Test-DotnetSdk)) {
        throw "Aucun SDK .NET utilisable. Installez le SDK .NET 10 : https://dotnet.microsoft.com/download/dotnet/10.0 (puis rouvrez le terminal)."
    }
}
$sdkList = (& dotnet --list-sdks) -join '; '
Write-Host "SDK .NET : $sdkList"

# --- 1. version ------------------------------------------------------------------------------------------------------
if (-not $Version) {
    $match = [regex]::Match((Get-Content -Raw -Path $project), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $match.Success) { throw "Aucune balise <Version> dans $project : passez -Version 1.0.0." }
    $Version = $match.Groups[1].Value
}
# SÉCURITÉ : la version est injectée dans une ligne de commande ; on n'accepte que des chiffres et des points.
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Version invalide '$Version' : attendu 1.2.3 (chiffres et points)." }
Write-Host "Version de l'installateur : $Version"

# --- 2. tests --------------------------------------------------------------------------------------------------------
if (-not $SkipTests) {
    Step "Tests (dotnet test) — l'installateur n'est construit que si tout est vert"
    & dotnet test $solution -c Release
    Assert-Success 'dotnet test'
}

# --- 3. publication autonome -----------------------------------------------------------------------------------------
Step "Publication win-x64 autonome (le runtime .NET est inclus) → $publishDir"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
& dotnet publish $project -c Release -r win-x64 --self-contained -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $publishDir
Assert-Success 'dotnet publish'
if (-not (Test-Path (Join-Path $publishDir 'RedChat.exe'))) { throw "RedChat.exe est absent de $publishDir : publication incomplète." }

# --- 4. Inno Setup ---------------------------------------------------------------------------------------------------
function Find-Iscc {
    $registeredInstall = Get-ItemProperty `
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' `
        -ErrorAction SilentlyContinue |
        Where-Object {
            $_.PSObject.Properties['DisplayName'] -and
            $_.PSObject.Properties['InstallLocation'] -and
            $_.DisplayName -like 'Inno Setup version 6*' -and
            $_.InstallLocation
        } |
        Select-Object -First 1
    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Inno Setup 6\ISCC.exe'),
        (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Inno Setup 6\ISCC.exe')
    )
    if ($registeredInstall) {
        $candidates += Join-Path $registeredInstall.InstallLocation 'ISCC.exe'
    }
    foreach ($candidate in $candidates) { if ($candidate -and (Test-Path $candidate)) { return $candidate } }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc -and $InstallInno) {
    Step "Installation d'Inno Setup avec winget"
    & winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
    Assert-Success 'winget install'
    $iscc = Find-Iscc
}
if (-not $iscc) {
    throw "Inno Setup 6 est introuvable. Relancez avec -InstallInno, ou installez-le : https://jrsoftware.org/isdl.php (puis relancez ce script)."
}

Step "Compilation de l'installateur avec Inno Setup ($iscc)"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishDir" $script
Assert-Success 'ISCC (Inno Setup)'

# --- 5. résultat -----------------------------------------------------------------------------------------------------
$setup = Join-Path $outputDir "RedChat-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "L'installateur attendu est introuvable : $setup" }
$hash = (Get-FileHash -Algorithm SHA256 -Path $setup).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash *RedChat-Setup-$Version.exe" -Encoding ascii
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)

Step "Terminé"
Write-Host "Installateur : $setup ($sizeMb Mo)"
Write-Host "SHA-256      : $hash"
Write-Host ""
Write-Host "À faire avant de le diffuser : l'essayer sur un autre PC / une machine virtuelle (voir setup\README-INSTALLATEUR.md)." -ForegroundColor Yellow
