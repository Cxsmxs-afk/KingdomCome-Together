# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = @{}
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName.Replace('\', '/')
        if ($entries.ContainsKey($path)) { throw "Duplicate drop-in entry: $path" }
        $entries[$path] = $entry
    }

    $app = 'KingdomComeTogether'
    $mod = 'Mods/kdcmp'
    $appFiles = @(
        'KCDMP_launcher.exe'
        'KCDMP_launcher.dll'
        'KCDMP.dll'
        'KcdMpClient.exe'
        'KcdMpClient.dll'
        'KcdMpServer.exe'
        'KcdMpServer.dll'
        'KcdMp.Protocol.dll'
        'KcdMp.Farkle.dll'
        'KcdMpSetup.exe'
        'MasterServer/KcdMpMasterServer.exe'
        'MasterServer/KcdMpMasterServer.dll'
    )
    $modFiles = @('mod.manifest', 'Data/kdcmp.pak')
    $requiredPayload = @($appFiles | ForEach-Object { "$app/$_" })
    foreach ($file in $modFiles) {
        $requiredPayload += "$mod/$file", "$app/mod/kdcmp/$file"
    }
    $verifyPath = "$app/install-verify.txt"
    $manifestPath = "$app/install-manifest.txt"
    foreach ($path in $requiredPayload + @('INSTALL.txt', $verifyPath, $manifestPath)) {
        if (-not $entries.ContainsKey($path) -or $entries[$path].Length -eq 0) {
            throw "Missing or empty drop-in entry: $path"
        }
    }

    $reader = [System.IO.StreamReader]::new($entries[$verifyPath].Open())
    try {
        if ($reader.ReadLine() -cnotmatch '^PASS(?:\s|$)') { throw 'Drop-in install verification is not PASS' }
    }
    finally { $reader.Dispose() }

    # Check the published payload, including runtime dependencies, against the build's install manifest.
    $verified = @{}
    $hash = [System.Security.Cryptography.SHA256]::Create()
    $reader = [System.IO.StreamReader]::new($entries[$manifestPath].Open())
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line.StartsWith('#') -or $line.Length -eq 0) { continue }
            if ($line -cnotmatch '^(APP|MOD)\|([^|]+)\|(\d+)\|([0-9a-fA-F]{64})$') {
                throw "Invalid install-manifest line: $line"
            }
            $folder = if ($Matches[1] -eq 'APP') { $app } else { $mod }
            $path = "$folder/$($Matches[2].Replace('\', '/'))"
            $expectedSize = [long]$Matches[3]
            $expectedHash = $Matches[4]
            if ($verified.ContainsKey($path)) { throw "Duplicate install-manifest entry: $path" }
            if (-not $entries.ContainsKey($path) -or $entries[$path].Length -ne $expectedSize) {
                throw "Drop-in entry is missing or has the wrong size: $path"
            }

            $stream = $entries[$path].Open()
            try { $actualHash = [System.BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose() }
            if ($actualHash -ne $expectedHash) { throw "Drop-in entry has the wrong SHA-256: $path" }
            $verified[$path] = $true
        }
    }
    finally {
        $reader.Dispose()
        $hash.Dispose()
    }
    foreach ($path in $requiredPayload) {
        if (-not $verified.ContainsKey($path)) { throw "Required drop-in entry is absent from install-manifest.txt: $path" }
    }
    Write-Host 'Drop-in contents PASS: instructions, executables, DLL, mod and install manifest verified'
}
finally { $zip.Dispose() }
