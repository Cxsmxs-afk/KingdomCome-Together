# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$Agent,
    [string]$BuildId,
    [switch]$ExpectMissingStartSaves
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
if (-not $BuildId) { $BuildId = (Get-Content -LiteralPath (Join-Path $root 'BUILD_ID') -Raw).Trim() }
if ($BuildId -cnotmatch '^[0-9a-f]{7,40}-[0-9]{10}$') { throw 'Missing or invalid expected drop-in Build-ID.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-EntryHash($Entry) {
    $stream = $Entry.Open()
    try { (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
    finally { $stream.Dispose() }
}

function Read-EntryBytes($Entry) {
    $stream = $Entry.Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($memory)
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $stream.Dispose()
    }
}

function Read-ManagedBuildId($Entry) {
    # Read the actual assembly metadata without loading or running the packaged code.
    $memory = [IO.MemoryStream]::new([byte[]](Read-EntryBytes $Entry))
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($memory)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $metadata.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne 'MemberReference') { continue }
            $constructor = $metadata.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($constructor.Parent.Kind -ne 'TypeReference') { continue }
            $type = $metadata.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
            if ($metadata.GetString($type.Namespace) -cne 'System.Reflection' -or
                $metadata.GetString($type.Name) -cne 'AssemblyMetadataAttribute') { continue }
            $value = $metadata.GetBlobReader($attribute.Value)
            if ($value.ReadUInt16() -eq 1 -and $value.ReadSerializedString() -ceq 'KcdMpBuildId') {
                $value.ReadSerializedString()
            }
        }
    }
    finally { $pe.Dispose() }
}

function Read-InstallManifest($Entry) {
    $manifest = @{}
    $reader = [IO.StreamReader]::new($Entry.Open())
    try {
        if ($reader.ReadLine() -cne '# KCDMP install manifest v2 -- <kind>|<relative path>|<size>|<sha256>') {
            throw 'Invalid drop-in install manifest header.'
        }
        while ($null -ne ($line = $reader.ReadLine())) {
            $parts = $line.Split('|')
            [long]$length = 0
            if ($parts.Count -ne 4 -or $parts[0] -cnotmatch '^(APP|MOD)$' -or
                $parts[2] -cnotmatch '^[0-9]+$' -or -not [long]::TryParse($parts[2], [ref]$length) -or
                $parts[3] -cnotmatch '^[0-9A-F]{64}$') { throw 'Invalid drop-in install manifest row.' }
            $path = $parts[1].Replace('\', '/')
            if (!$path -or $path -match '^/|/$|//|(^|/)\.\.?(/|$)|:') { throw 'Invalid drop-in install manifest path.' }
            $prefix = if ($parts[0] -ceq 'APP') { 'KingdomComeTogether' } else { 'Mods/kdcmp' }
            $archivePath = "$prefix/$path"
            if ($manifest.ContainsKey($archivePath)) { throw "Duplicate install manifest entry: $archivePath" }
            $manifest[$archivePath] = @{ Length = $length; Hash = $parts[3] }
        }
    }
    finally { $reader.Dispose() }
    return $manifest
}

$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
try {
    $entries = @{}
    $saves = @{}
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName.Replace('\', '/')
        if ($entries.ContainsKey($path)) { throw "Duplicate drop-in entry: $path" }
        $entries[$path] = $entry
        if ($ExpectMissingStartSaves -and $path -like '*.whs') {
            throw 'A code-only drop-in contains a save file.'
        }
        if ($path -like 'KingdomComeTogether/start-save/*.whs') {
            if ($path -notmatch '^KingdomComeTogether/start-save/(soldier|adviser|scout)/[^/:]+\.whs$') {
                throw "Invalid start save drop-in entry: $path"
            }
            $style = $Matches[1]
            if ($saves.ContainsKey($style) -or $entry.Length -eq 0) { throw "Duplicate or empty $style start save: $path" }
            $saves[$style] = $entry
        }
    }
    $managed = @('KCDMP_launcher', 'KcdMpClient', 'KcdMpServer', 'KcdMp.Protocol')
    $requiredPayload = @('KingdomComeTogether/KCDMP.dll', 'Mods/kdcmp/Data/kdcmp.pak', 'Mods/kdcmp/mod.manifest',
        'KingdomComeTogether/mod/kdcmp/Data/kdcmp.pak', 'KingdomComeTogether/mod/kdcmp/mod.manifest')
    $requiredPayload += @($managed | ForEach-Object { "KingdomComeTogether/$_.dll" })
    $requiredPayload += @('KCDMP_launcher', 'KcdMpClient', 'KcdMpServer' | ForEach-Object { "KingdomComeTogether/$_.exe" })
    $manifestPath = 'KingdomComeTogether/install-manifest.txt'
    $markerPath = 'KingdomComeTogether/install-verify.txt'
    foreach ($path in ($requiredPayload + @('INSTALL.txt', $manifestPath, $markerPath))) {
        if (-not $entries.ContainsKey($path) -or $entries[$path].Length -eq 0) { throw "Missing or empty drop-in entry: $path" }
    }

    $publishedAgent = (Resolve-Path -LiteralPath $Agent).Path
    $publishedManifest = Join-Path (Split-Path -Parent $publishedAgent) 'install-manifest.txt'
    if ((Get-EntryHash $entries[$manifestPath]) -cne (Get-FileHash -LiteralPath $publishedManifest -Algorithm SHA256).Hash) {
        throw 'The drop-in install manifest differs from the published manifest.'
    }
    $manifest = Read-InstallManifest $entries[$manifestPath]
    foreach ($path in $requiredPayload) {
        if (-not $manifest.ContainsKey($path)) { throw "Required file missing from install manifest: $path" }
    }
    foreach ($path in $manifest.Keys) {
        $entry = $entries[$path]
        if ($null -eq $entry -or $entry.Length -ne $manifest[$path].Length) { throw "Size mismatch: $path" }
        if ((Get-EntryHash $entry) -cne $manifest[$path].Hash) { throw "SHA-256 mismatch: $path" }
    }
    if ($manifest['KingdomComeTogether/KcdMpClient.exe'].Hash -cne
        (Get-FileHash -LiteralPath $publishedAgent -Algorithm SHA256).Hash) {
        throw 'The published validation agent differs from the agent in the drop-in.'
    }

    $reader = [IO.StreamReader]::new($entries[$markerPath].Open())
    try {
        if ($reader.ReadLine() -cnotmatch '^PASS(?:\s|$)') { throw 'Drop-in install verification is not PASS' }
        $recordedBuild = @($reader.ReadToEnd() -split '\r?\n' | Where-Object { $_.StartsWith('build ') })
        if ($recordedBuild.Count -ne 1 -or $recordedBuild[0] -cne "build $BuildId") { throw "Build-ID mismatch: $markerPath" }
    }
    finally { $reader.Dispose() }
    foreach ($component in $managed) {
        $path = "KingdomComeTogether/$component.dll"
        $ids = @(Read-ManagedBuildId $entries[$path])
        if ($ids.Count -ne 1 -or $ids[0] -cne $BuildId) { throw "Build-ID mismatch: $path" }
    }
    $nativePath = 'KingdomComeTogether/KCDMP.dll'
    # wo211::build_id uses a NUL-terminated C string embedded by the native compiler.
    $native = [Text.Encoding]::ASCII.GetString([byte[]](Read-EntryBytes $entries[$nativePath]))
    $nativeIds = @([regex]::Matches($native, '(?<![0-9A-Za-z.-])([0-9a-f]{7,40}-[0-9]{10})\x00') |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($nativeIds.Count -ne 1 -or $nativeIds[0] -cne $BuildId) { throw "Build-ID mismatch: $nativePath" }

    $pass = "Drop-in contents PASS: instructions, launcher, agent, relay, DLL, pak, manifests, SHA-256 and install-verify, build=$BuildId"
    $styles = @('soldier', 'adviser', 'scout')
    $missing = @($styles | Where-Object { -not $saves.ContainsKey($_) })
    if ($ExpectMissingStartSaves -and $missing.Count -eq $styles.Count) {
        $source = Join-Path $root 'assets/start-save'
        if ((Test-Path -LiteralPath $source) -and @(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.whs').Count -gt 0) {
            throw 'The source start saves exist but none were packaged in the drop-in.'
        }
        Write-Warning 'EXPECTED FAILURE H21: soldier, adviser and scout start saves are missing from the source and drop-in.'
        Write-Output $pass
        return
    }
    if ($missing.Count -gt 0) { throw "Missing start saves from the drop-in: $($missing -join ', ')" }

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
    Write-Output $pass
    Write-Output 'Drop-in start saves PASS: soldier, adviser, scout'
}
finally { $zip.Dispose() }
