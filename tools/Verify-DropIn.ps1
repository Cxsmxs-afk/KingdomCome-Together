# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$Agent,
    [switch]$ExpectMissingStartSaves
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
try {
    $entries = @{}
    $saves = @{}
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName.Replace('\', '/')
        if ($entries.ContainsKey($path)) { throw "Duplicate drop-in entry: $path" }
        $entries[$path] = $entry
        if ($path -like 'KingdomComeTogether/start-save/*.whs') {
            if ($path -notmatch '^KingdomComeTogether/start-save/(soldier|adviser|scout)/[^/:]+\.whs$') {
                throw "Invalid start save drop-in entry: $path"
            }
            $style = $Matches[1]
            if ($saves.ContainsKey($style) -or $entry.Length -eq 0) { throw "Duplicate or empty $style start save: $path" }
            $saves[$style] = $entry
        }
    }
    foreach ($path in @('INSTALL.txt', 'KingdomComeTogether/KCDMP_launcher.exe', 'Mods/kdcmp/Data/kdcmp.pak', 'KingdomComeTogether/install-verify.txt')) {
        if (-not $entries.ContainsKey($path) -or $entries[$path].Length -eq 0) { throw "Missing or empty drop-in entry: $path" }
    }
    $reader = [IO.StreamReader]::new($entries['KingdomComeTogether/install-verify.txt'].Open())
    try { if ($reader.ReadLine() -cnotmatch '^PASS(?:\s|$)') { throw 'Drop-in install verification is not PASS' } }
    finally { $reader.Dispose() }
    $styles = @('soldier', 'adviser', 'scout')
    $missing = @($styles | Where-Object { -not $saves.ContainsKey($_) })
    if ($ExpectMissingStartSaves -and $missing.Count -eq $styles.Count) {
        $source = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets/start-save'
        if ((Test-Path -LiteralPath $source) -and @(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.whs').Count -gt 0) {
            throw 'The source start saves exist but none were packaged in the drop-in.'
        }
        Write-Warning 'EXPECTED FAILURE H21: soldier, adviser and scout start saves are missing from the source and drop-in.'
        Write-Output 'Drop-in contents PASS: instructions, launcher, pak and install-verify'
        return
    }
    if ($missing.Count -gt 0) { throw "Missing start saves from the drop-in: $($missing -join ', ')" }

    $agentPath = 'KingdomComeTogether/KcdMpClient.exe'
    if (-not $entries.ContainsKey($agentPath) -or -not (Test-Path -LiteralPath $Agent -PathType Leaf)) {
        throw 'The drop-in agent or its matching published executable is missing.'
    }
    $packedAgent = $entries[$agentPath].Open()
    try {
        if ((Get-FileHash -InputStream $packedAgent).Hash -ne (Get-FileHash -LiteralPath $Agent).Hash) {
            throw 'The published validation agent differs from the agent in the drop-in.'
        }
    }
    finally { $packedAgent.Dispose() }

    $dir = Join-Path ([IO.Path]::GetTempPath()) ('kcdmp-dropin-saves-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        $validator = Join-Path $PSScriptRoot 'Validate-StartSave.ps1'
        $powerShell = (Get-Process -Id $PID).Path
        foreach ($style in $styles) {
            # Validate the archived bytes in an isolated directory. The archive's
            # filename never determines where the entry is written.
            $file = Join-Path $dir "$style.whs"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($saves[$style], $file)
            & $powerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $validator -Path $file -Style $style -Agent $Agent
            if ($LASTEXITCODE -ne 0) { throw "Drop-in start save failed validation: $style" }
        }
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
    if ($ExpectMissingStartSaves) { throw 'All three start saves passed. Enable the required H21 start-save gate.' }
    Write-Output 'Drop-in contents PASS: instructions, launcher, pak and install-verify'
    Write-Output 'Drop-in start saves PASS: soldier, adviser, scout'
}
finally { $zip.Dispose() }
