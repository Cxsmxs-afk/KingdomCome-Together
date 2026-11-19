# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
[CmdletBinding()]
param([string]$Verifier = (Join-Path $PSScriptRoot 'Verify-DropIn.ps1'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = Join-Path ([IO.Path]::GetTempPath()) ('kct-test-dropin-' + [guid]::NewGuid().ToString('N'))
$buildId = 'a1b2c3d-2611192238'
$otherId = 'b2c3d4e-2611192238'
$failures = [Collections.Generic.List[string]]::new()

function Write-FixtureText([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Invoke-Fixture([string]$Name, [string]$ExpectedError, [scriptblock]$AlterPayload, [scriptblock]$AlterArchive) {
    $directory = Join-Path $work $Name
    $root = Join-Path $directory 'zip'
    $app = Join-Path $root 'KingdomComeTogether'
    $mod = Join-Path $root 'Mods/kdcmp'
    $staged = Join-Path $app 'mod/kdcmp'
    $published = Join-Path $directory 'published'
    New-Item -ItemType Directory -Force -Path (Join-Path $mod 'Data'), (Join-Path $staged 'Data'), $published | Out-Null
    foreach ($component in @('KCDMP_launcher', 'KcdMpClient', 'KcdMpServer')) {
        Write-FixtureText (Join-Path $app "$component.exe") 'synthetic app host'
        Copy-Item (Join-Path $work 'expected.dll') (Join-Path $app "$component.dll")
    }
    Copy-Item (Join-Path $work 'expected.dll') (Join-Path $app 'KcdMp.Protocol.dll')
    Copy-Item (Join-Path $work 'expected.dll') (Join-Path $app 'KCDMP.dll')
    $native = [IO.File]::Open((Join-Path $app 'KCDMP.dll'), [IO.FileMode]::Append)
    try { $native.Write([Text.Encoding]::ASCII.GetBytes(([char]0) + $buildId + ([char]0))) }
    finally { $native.Dispose() }
    Write-FixtureText (Join-Path $mod 'Data/kdcmp.pak') 'synthetic pak'
    Write-FixtureText (Join-Path $mod 'mod.manifest') 'synthetic manifest'
    Copy-Item (Join-Path $mod 'Data/kdcmp.pak') (Join-Path $staged 'Data/kdcmp.pak')
    Copy-Item (Join-Path $mod 'mod.manifest') (Join-Path $staged 'mod.manifest')

    if ($AlterPayload) { & $AlterPayload $app }
    $manifest = Join-Path $app 'install-manifest.txt'
    & (Join-Path $PSScriptRoot 'New-InstallManifest.ps1') -AppDir $app -OutFile $manifest -ModFiles @{
        'Data/kdcmp.pak' = (Join-Path $mod 'Data/kdcmp.pak')
        'mod.manifest' = (Join-Path $mod 'mod.manifest')
    }
    Copy-Item $manifest (Join-Path $published 'install-manifest.txt')
    Copy-Item (Join-Path $app 'KcdMpClient.exe') (Join-Path $published 'KcdMpClient.exe')
    Write-FixtureText (Join-Path $root 'INSTALL.txt') 'synthetic instructions'
    Write-FixtureText (Join-Path $app 'install-verify.txt') "PASS drop-in 0.0.0`nversion 0.0.0`nbuild $buildId"
    if ($AlterArchive) { & $AlterArchive $root }
    $zip = Join-Path $directory 'fixture.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($root, $zip)

    $params = @{
        ZipPath = $zip
        Agent = (Join-Path $published 'KcdMpClient.exe')
        ExpectMissingStartSaves = $true
    }
    # The archived baseline has no BuildId option. Keep the red test runnable there.
    if ((Get-Command $Verifier).Parameters.ContainsKey('BuildId')) { $params.BuildId = $buildId }
    $errorText = $null
    $verification = @()
    try { $verification = @(& $Verifier @params 3>&1) }
    catch { $errorText = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $errorText -or $errorText -notlike "*$ExpectedError*") {
            $actual = if ($errorText) { $errorText } else { 'accepted invalid package' }
            $failures.Add("$Name - $actual")
            Write-Output "FAIL $Name - $actual"
            return
        }
    }
    elseif ($errorText -or -not ($verification -like 'Drop-in contents PASS:*')) {
        $actual = if ($errorText) { $errorText } else { 'integrity PASS was missing' }
        $failures.Add("$Name - $actual")
        Write-Output "FAIL $Name - $actual"
        return
    }
    Write-Output "PASS $Name - $errorText"
}

New-Item -ItemType Directory -Path $work | Out-Null
try {
    $expectedSource = '[assembly: System.Reflection.AssemblyMetadata("KcdMpBuildId", "{0}")] public class ExpectedBuild {{}}' -f $buildId
    $otherSource = '[assembly: System.Reflection.AssemblyMetadata("KcdMpBuildId", "{0}")] public class OtherBuild {{}}' -f $otherId
    Add-Type -TypeDefinition $expectedSource -OutputAssembly (Join-Path $work 'expected.dll')
    Add-Type -TypeDefinition $otherSource -OutputAssembly (Join-Path $work 'other.dll')
    Invoke-Fixture 'Complete_code_only_drop_in_passes'
    Invoke-Fixture 'Missing_agent_is_rejected' 'Missing or empty drop-in entry: KingdomComeTogether/KcdMpClient.exe' -AlterArchive {
        param($root) Remove-Item -LiteralPath (Join-Path $root 'KingdomComeTogether/KcdMpClient.exe')
    }
    Invoke-Fixture 'Missing_relay_is_rejected' 'Missing or empty drop-in entry: KingdomComeTogether/KcdMpServer.exe' -AlterArchive {
        param($root) Remove-Item -LiteralPath (Join-Path $root 'KingdomComeTogether/KcdMpServer.exe')
    }
    Invoke-Fixture 'Missing_native_dll_is_rejected' 'Missing or empty drop-in entry: KingdomComeTogether/KCDMP.dll' -AlterArchive {
        param($root) Remove-Item -LiteralPath (Join-Path $root 'KingdomComeTogether/KCDMP.dll')
    }
    Invoke-Fixture 'Wrong_agent_hash_is_rejected' 'SHA-256 mismatch: KingdomComeTogether/KcdMpClient.exe' -AlterArchive {
        param($root) Write-FixtureText (Join-Path $root 'KingdomComeTogether/KcdMpClient.exe') 'synthetic app lost'
    }
    Invoke-Fixture 'Staged_mod_hash_mismatch_is_rejected' 'SHA-256 mismatch: KingdomComeTogether/mod/kdcmp/Data/kdcmp.pak' -AlterArchive {
        param($root) Write-FixtureText (Join-Path $root 'KingdomComeTogether/mod/kdcmp/Data/kdcmp.pak') 'synthetic bad'
    }
    Invoke-Fixture 'Mixed_managed_build_is_rejected_after_hash_verification' 'Build-ID mismatch: KingdomComeTogether/KcdMpServer.dll' -AlterPayload {
        param($app) Copy-Item (Join-Path $work 'other.dll') (Join-Path $app 'KcdMpServer.dll') -Force
    }
    Invoke-Fixture 'Mixed_native_build_is_rejected_after_hash_verification' 'Build-ID mismatch: KingdomComeTogether/KCDMP.dll' -AlterPayload {
        param($app)
        $nativePath = Join-Path $app 'KCDMP.dll'
        $bytes = [IO.File]::ReadAllBytes($nativePath)
        [Text.Encoding]::ASCII.GetBytes($otherId).CopyTo($bytes, $bytes.Length - $otherId.Length - 1)
        [IO.File]::WriteAllBytes($nativePath, $bytes)
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}

if ($failures.Count) { throw "Drop-in synthetic gate failed ($($failures.Count) failures)" }
Write-Output 'Drop-in synthetic gate PASS: 8 cases'
