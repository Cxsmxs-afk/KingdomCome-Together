# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
param([string] $Applier = (Join-Path $PSScriptRoot 'Apply-DirectInstall.ps1'))
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('kct-direct-' + [guid]::NewGuid().ToString('n'))
$passed = 0
try {
    foreach ($case in 'owned', 'foreign-mod', 'foreign-app', 'bad-package', 'extra-package-file') {
        $caseDir = Join-Path $root $case
        $app = Join-Path $caseDir 'InstalledApp'; $mod = Join-Path $caseDir 'InstalledMod'
        $source = Join-Path $caseDir 'Package'; $appSrc = Join-Path $source 'App'; $modSrc = Join-Path $source 'Mod'
        New-Item -ItemType Directory -Force -Path $app, "$mod/Data", $appSrc, "$modSrc/Data" | Out-Null
        Set-Content "$app/KCDMP_launcher.exe" 'old launcher' -NoNewline
        Set-Content "$app/settings.json" 'personal settings' -NoNewline
        Set-Content "$app/obsolete.dll" 'previous mod runtime' -NoNewline
        Set-Content "$appSrc/KCDMP_launcher.exe" 'new launcher' -NoNewline
        $installedIdentity = if ($case -eq 'foreign-mod') { 'foreign manifest' } else { 'KCT mod identity' }
        Set-Content "$mod/mod.manifest" $installedIdentity -NoNewline
        Set-Content "$mod/Data/kdcmp.pak" 'old package' -NoNewline
        Set-Content "$mod/Data/kdcmp_keys.pak" 'local key delta' -NoNewline
        Set-Content "$mod/keep.bin" 'original bytes' -NoNewline
        Set-Content "$modSrc/mod.manifest" 'KCT mod identity' -NoNewline
        Set-Content "$modSrc/Data/kdcmp.pak" 'new package' -NoNewline
        function Row($kind, $rel, $path) { "$kind|$rel|$((Get-Item $path).Length)|$((Get-FileHash $path -Algorithm SHA256).Hash)" }
        $header = '# KCDMP install manifest v2 -- <kind>|<relative path>|<size>|<sha256>'
        if ($case -ne 'foreign-app') {
            Set-Content "$app/install-manifest.txt" @($header, (Row APP KCDMP_launcher.exe "$app/KCDMP_launcher.exe"))
        }
        Set-Content "$appSrc/install-manifest.txt" @($header, (Row APP KCDMP_launcher.exe "$appSrc/KCDMP_launcher.exe"), (Row MOD mod.manifest "$modSrc/mod.manifest"), (Row MOD 'Data\kdcmp.pak' "$modSrc/Data/kdcmp.pak"))
        if ($case -eq 'bad-package') { Set-Content "$modSrc/Data/kdcmp.pak" 'corrupt' -NoNewline }
        if ($case -eq 'extra-package-file') { Set-Content "$appSrc/extra.bin" 'unlisted' -NoNewline }
        function Snapshot { @(Get-ChildItem -Path $app, $mod -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName, (Get-FileHash $_.FullName).Hash }) }
        $before = Snapshot
        $runner = (Get-Process -Id $PID).Path
        $output = & $runner -NoProfile -NonInteractive -File $Applier -Source $source -AppDir $app -ModDir $mod 2>&1
        $code = $LASTEXITCODE
        $same = -not (Compare-Object $before (Snapshot) -SyncWindow 0)
        if ($case -eq 'owned') {
            $ok = $code -eq 0 -and (Get-Content "$app/install-verify.txt" -First 1) -match '^PASS' -and
                (Get-Content "$app/KCDMP_launcher.exe" -Raw) -eq 'new launcher' -and
                (Get-Content "$mod/Data/kdcmp.pak" -Raw) -eq 'new package' -and
                (Get-Content "$app/settings.json" -Raw) -eq 'personal settings' -and
                (Get-Content "$mod/Data/kdcmp_keys.pak" -Raw) -eq 'local key delta' -and
                -not (Test-Path "$app/obsolete.dll") -and -not (Test-Path "$mod/keep.bin")
        } else { $ok = $code -ne 0 -and $same -and $output -match 'Cannot apply' }
        if ($ok) { $passed++ }
        Write-Host "case=$case exit=$code unchanged=$same test=$(if ($ok) {'PASS'} else {'FAIL'})"
        $output | ForEach-Object { Write-Host $_ }
    }
} finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "$passed/5 passed"
if ($passed -ne 5) { exit 1 }
