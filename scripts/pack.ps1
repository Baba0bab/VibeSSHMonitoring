# SPDX-License-Identifier: GPL-3.0-or-later
# Construit l'installateur et les paquets de mise à jour (Velopack) pour une version donnée.
# Utilisé tel quel par le workflow de release ; utilisable en local :
#   ./scripts/pack.ps1 -Version 1.2.0
#   ./scripts/pack.ps1 -Version 1.3.0-beta.1
# Sortie (dossier Releases) : VibeSSHMonitoring-win-Setup.exe, VibeSSHMonitoring-win-Portable.zip,
# paquets complet et delta (.nupkg) et index releases.win.json lus par les mises à jour.

param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDir = "Releases",
    [string] $Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "Version invalide '$Version' : attendu 1.2.3 ou 1.2.3-beta.1"
    }
    $prefix, $suffix = $Version.Split('-', 2)
    $publishDir = Join-Path $root "artifacts/publish-$Runtime"
    $notesFile = Join-Path $root "artifacts/release-notes.md"

    # 1. Application autonome (runtime .NET inclus : rien à installer côté utilisateur)
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish VibeSSHMonitoring.csproj -c Release -r $Runtime --self-contained true `
        "-p:VersionPrefix=$prefix" "-p:VersionSuffix=$suffix" -o $publishDir
    if ($LASTEXITCODE) { throw "dotnet publish a échoué" }

    # 2. Notes de version : la section de CHANGELOG.md, affichée dans l'application et sur GitHub
    $changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw
    $pattern = '(?ms)^## \[' + [regex]::Escape($Version) + '\][^\n]*\n(.*?)(?=^## \[|\z)'
    $section = [regex]::Match($changelog, $pattern).Groups[1].Value
    $section = ($section -split "`n" | Where-Object { $_ -notmatch '^\[[^\]]+\]:' }) -join "`n"
    if (-not $section.Trim()) {
        Write-Warning "Aucune section « ## [$Version] » dans CHANGELOG.md : notes de version vides."
        $section = "Version $Version"
    }
    New-Item -ItemType Directory -Force (Split-Path $notesFile) | Out-Null
    Set-Content $notesFile $section.Trim() -Encoding utf8

    # 3. Paquets Velopack (delta automatique si la version précédente est déjà dans $OutputDir)
    dotnet tool restore | Out-Null
    dotnet vpk pack `
        --packId VibeSSHMonitoring `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe VibeSSHMonitoring.exe `
        --packTitle "Vibe SSH Monitoring" `
        --packAuthors Baba0bab `
        --icon icon.ico `
        --releaseNotes $notesFile `
        --shortcuts "Desktop,StartMenuRoot" `
        --runtime $Runtime `
        --outputDir $OutputDir
    if ($LASTEXITCODE) { throw "vpk pack a échoué" }

    Get-ChildItem $OutputDir | Sort-Object Name | ForEach-Object { "{0,-50} {1,10:N0} Ko" -f $_.Name, ($_.Length / 1KB) }
}
finally {
    Pop-Location
}
