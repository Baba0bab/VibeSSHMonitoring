# SPDX-License-Identifier: GPL-3.0-or-later
# Builds the installer and the update packages (Velopack) for a given version.
# Used as is by the release workflow; also usable locally:
#   ./scripts/pack.ps1 -Version 1.2.0
#   ./scripts/pack.ps1 -Version 1.3.0-beta.1
# Output (Releases folder): VibeSSHMonitoring-win-Setup.exe, VibeSSHMonitoring-win-Portable.zip,
# full and delta packages (.nupkg) and the releases.win.json index read by the updates.

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
        throw "Invalid version '$Version': expected 1.2.3 or 1.2.3-beta.1"
    }
    $prefix, $suffix = $Version.Split('-', 2)
    $publishDir = Join-Path $root "artifacts/publish-$Runtime"
    $notesFile = Join-Path $root "artifacts/release-notes.md"

    # 1. Self-contained application (.NET runtime included: nothing to install for the user)
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish VibeSSHMonitoring.csproj -c Release -r $Runtime --self-contained true `
        "-p:VersionPrefix=$prefix" "-p:VersionSuffix=$suffix" -o $publishDir
    if ($LASTEXITCODE) { throw "dotnet publish failed" }

    # 2. Release notes: the CHANGELOG.md section, shown in the application and on GitHub
    $changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw
    $pattern = '(?ms)^## \[' + [regex]::Escape($Version) + '\][^\n]*\n(.*?)(?=^## \[|\z)'
    $section = [regex]::Match($changelog, $pattern).Groups[1].Value
    $section = ($section -split "`n" | Where-Object { $_ -notmatch '^\[[^\]]+\]:' }) -join "`n"
    if (-not $section.Trim()) {
        Write-Warning "No '## [$Version]' section in CHANGELOG.md: empty release notes."
        $section = "Version $Version"
    }
    New-Item -ItemType Directory -Force (Split-Path $notesFile) | Out-Null
    Set-Content $notesFile $section.Trim() -Encoding utf8

    # 3. Velopack packages (automatic delta if the previous version is already in $OutputDir)
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
    if ($LASTEXITCODE) { throw "vpk pack failed" }

    Get-ChildItem $OutputDir | Sort-Object Name | ForEach-Object { "{0,-50} {1,10:N0} KB" -f $_.Name, ($_.Length / 1KB) }
}
finally {
    Pop-Location
}
