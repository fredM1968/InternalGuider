<#
.SYNOPSIS
  Prépare une version publiable d'Internal Guider : archive ZIP, somme de contrôle et manifest.json pour le dépôt de plugins NINA.

.DESCRIPTION
  À lancer APRÈS avoir compilé le projet en Release (NINA fermé). Le script lit la version dans la DLL et l'identifiant (GUID),
  le titre, l'auteur et la licence dans Properties\AssemblyInfo.cs. Il crée dans le dossier "dist" :
    - InternalGuider-<version>.zip : l'archive à déposer dans une "release" GitHub (la DLL à la racine) ;
    - manifest.json               : le manifeste à proposer au dépôt isbeorn/nina.plugin.manifests ;
    - InternalGuider-Setup-<version>.exe : l'installeur Windows, si Inno Setup 6 est installé (facultatif).

.EXAMPLE
  .\installer\Package-Release.ps1 -RepoUrl "https://github.com/moncompte/nina-internal-guider"
#>
param(
    # Adresse du dépôt GitHub qui héberge le code et la release (sans slash final)
    [Parameter(Mandatory = $true)] [string] $RepoUrl,
    # Nom affiché comme auteur ; par défaut AssemblyCompany de AssemblyInfo.cs
    [string] $Author = "",
    # Page d'accueil du plugin ; par défaut le dépôt
    [string] $Homepage = "",
    # "Release" (par défaut) ou "Beta" pour une version à tester par quelques personnes
    [ValidateSet("Release", "Beta")] [string] $Channel = "Release",
    [string] $Dll = "",
    [string] $ShortDescription = "Built-in autoguiding for N.I.N.A.: calibrates and guides your mount directly from NINA, no PHD2 needed.",
    [string] $LongDescription = "Internal Guider replaces PHD2 with a guider built into N.I.N.A. It detects stars on your guide camera (ASCOM or native), calibrates both mount axes, guides on up to 6 stars, supports dithering from the sequencer, meridian flips (automatic Dec direction handling), a guiding assistant (drift, seeing, periodic error, Dec backlash), cloud pauses, a dark library and saving/loading the whole configuration. Test version: keep PHD2 available as a backup until you have validated it on your equipment."
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $Dll) {
    $candidates = @("bin\x64\Release\net8.0-windows", "bin\Release\net8.0-windows") | ForEach-Object { Join-Path $root "$_\NINA.Plugin.InternalGuider.dll" }
    # La DLL la plus récente est retenue (évite de prendre une ancienne DLL restée dans un autre dossier)
    $Dll = $candidates | Where-Object { Test-Path $_ } | Sort-Object { (Get-Item $_).LastWriteTime } -Descending | Select-Object -First 1
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "DLL introuvable : $Dll. Compilez d'abord le projet en Release." }
$Dll = (Resolve-Path $Dll).Path

# --- Lecture de AssemblyInfo.cs ---
$infoPath = Join-Path $root "Properties\AssemblyInfo.cs"
$info = Get-Content $infoPath -Raw -Encoding UTF8
function Get-Attr([string] $pattern) { if ($info -match $pattern) { return $Matches[1] } return "" }
$guid = Get-Attr 'Guid\("([^"]+)"\)'
$title = Get-Attr 'AssemblyTitle\("([^"]+)"\)'
$company = Get-Attr 'AssemblyCompany\("([^"]+)"\)'
$license = Get-Attr 'AssemblyMetadata\("License",\s*"([^"]+)"\)'
$licenseUrl = Get-Attr 'AssemblyMetadata\("LicenseURL",\s*"([^"]+)"\)'
$minApp = Get-Attr 'AssemblyMetadata\("MinimumApplicationVersion",\s*"([^"]+)"\)'

$problems = @()
if ($guid -notmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$') { $problems += "Guid invalide dans AssemblyInfo.cs ('$guid')." }
if (-not $title) { $problems += "AssemblyTitle manquant." }
if (-not $license -or -not $licenseUrl) { $problems += "Métadonnées License / LicenseURL manquantes dans AssemblyInfo.cs." }
if (-not $minApp) { $problems += "Métadonnée MinimumApplicationVersion manquante." }
if ($problems.Count -gt 0) { throw ("AssemblyInfo.cs incomplet :`n - " + ($problems -join "`n - ")) }
if (-not $Author) { $Author = $company }
if (-not $Author -or $Author -eq "Toi") { throw "Indiquez l'auteur : -Author ""Votre nom"" (ou renseignez AssemblyCompany)." }
if (-not $Homepage) { $Homepage = $RepoUrl }
$RepoUrl = $RepoUrl.TrimEnd('/')

# --- Version ---
$v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Dll)
$ver = "{0}.{1}.{2}.{3}" -f $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart, $v.FilePrivatePart
$minParts = ($minApp -split '\.') + @("0", "0", "0", "0")

# --- Archive ---
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zipName = "InternalGuider-$ver.zip"
$zipPath = Join-Path $dist $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path $Dll -DestinationPath $zipPath
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash

# --- Manifeste ---
$tag = "v$ver"
$manifest = [ordered]@{
    Name                      = $title
    Identifier                = $guid
    Author                    = $Author
    Homepage                  = $Homepage
    Repository                = $RepoUrl
    License                   = $license
    LicenseURL                = $licenseUrl
    Tags                      = @("guiding", "autoguiding", "guider", "PHD2 alternative")
    MinimumApplicationVersion = [ordered]@{ Major = [int]$minParts[0]; Minor = [int]$minParts[1]; Patch = [int]$minParts[2]; Build = [int]$minParts[3] }
    Version                   = [ordered]@{ Major = [int]$v.FileMajorPart; Minor = [int]$v.FileMinorPart; Patch = [int]$v.FileBuildPart; Build = [int]$v.FilePrivatePart }
    ChangelogURL              = "$RepoUrl/releases/tag/$tag"
    Installer                 = [ordered]@{
        URL          = "$RepoUrl/releases/download/$tag/$zipName"
        Type         = "ARCHIVE"
        Checksum     = $hash
        ChecksumType = "SHA256"
    }
    Descriptions              = [ordered]@{
        ShortDescription = $ShortDescription
        LongDescription  = $LongDescription
    }
}
if ($Channel -eq "Beta") { $manifest["Channel"] = "Beta" }
$manifestPath = Join-Path $dist "manifest.json"
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))   # UTF-8 sans BOM

# --- Installeur Windows (facultatif) ---
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "${env:ProgramFiles}\Inno Setup 6\ISCC.exe", "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/O$dist" (Join-Path $PSScriptRoot "InternalGuider.iss") | Out-Null
    Write-Host "Installeur créé dans $dist"
} else {
    Write-Host "Inno Setup 6 non trouvé : installeur .exe non créé (compilez installer\InternalGuider.iss à la main si besoin)."
}

Write-Host ""
Write-Host "Version      : $ver ($Channel)"
Write-Host "Archive      : $zipPath"
Write-Host "SHA256       : $hash"
Write-Host "Manifeste    : $manifestPath"
Write-Host ""
Write-Host "Étapes suivantes (voir installer\PUBLIER.md) :"
Write-Host " 1. Créez sur GitHub une release nommée '$tag' et déposez-y $zipName"
Write-Host " 2. Vérifiez que ce lien télécharge bien l'archive : $($manifest.Installer.URL)"
Write-Host " 3. Proposez manifest.json au dépôt isbeorn/nina.plugin.manifests (pull request)"
