# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
<#
.SYNOPSIS
    Apply a KCDMP DirectInstall zip, and prove it landed. Ships inside the zip
    as Apply.ps1.

.DESCRIPTION
    The DirectInstall zip is the update-only route: "unpack it over the top".
    That instruction is also, word for word, how an install ends up half
    applied -- Explorer merges the folders, skips or fails on whatever is in
    use, and says nothing. Setup.exe grew a process gate and a post-install
    verification in WO-32 and WO-74; this route had neither, which made it the
    remaining way to reach the exact state WO-74 exists to fix.

    So this script does what Setup does, in the same order and against the
    same manifest that ships inside App\:

      1. refuse to run while the launcher, agent, relay, master server or the
         game is running -- those hold the files being replaced;
      2. verify the existing install and complete package, then copy App\ over the install directory and Mod\ over the mod folder,
         clearing read-only first (one read-only file aborted a whole 0.18.8
         install; see installer\KCDMP.iss);
      3. remove any .dll/.exe/.pdb/.deps.json/.runtimeconfig.json in the
         install directory that this release does not ship -- a foreign
         assembly there is enough to stop the relay cold-starting (WO-69);
      4. verify every component by sha256 and write install-verify.txt;
      5. exit NON-ZERO and say which components failed if any did.

    Config, saves and logs are never touched: settings.json, favorites.json,
    custom_servers.json and anything else that is not a managed extension stay
    exactly as they are.

.PARAMETER AppDir
    Install directory. Defaults to the one the registry records, then to
    %LocalAppData%\KCDMP (DefaultDirName in installer\KCDMP.iss).

.PARAMETER ModDir
    The mod folder inside the Modding Tools install. Defaults to the registry's
    record. Required if there is no registry entry to read it from. The existing
    mod must match the package identity. Use Setup.exe for a fresh installation.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Apply.ps1
#>
[CmdletBinding()]
param(
    [string] $AppDir,
    [string] $ModDir,
    [string] $Source = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$reg = 'HKCU:\Software\KCDMP'
if (-not $AppDir) {
    $AppDir = (Get-ItemProperty $reg -ErrorAction SilentlyContinue).InstallDir
    if (-not $AppDir) { $AppDir = Join-Path $env:LOCALAPPDATA 'KCDMP' }
}
if (-not $ModDir) {
    $ModDir = (Get-ItemProperty $reg -ErrorAction SilentlyContinue).ModsPath
}
if (-not $ModDir) {
    throw "Cannot tell where the mod goes: no HKCU\Software\KCDMP\ModsPath. Run the full Setup.exe once, or pass -ModDir <ModdingTools>\Mods\kdcmp."
}

$appSrc = Join-Path $Source 'App'
$modSrc = Join-Path $Source 'Mod'
foreach ($d in @($appSrc, $modSrc)) {
    if (-not (Test-Path $d)) { throw "This does not look like a DirectInstall zip: $d is missing" }
}
$manifestSrc = Join-Path $appSrc 'install-manifest.txt'
if (-not (Test-Path $manifestSrc)) {
    throw "No install-manifest.txt in App\ -- this zip predates WO-74 and cannot be verified. Use Setup.exe instead."
}

# Check both installed targets and the complete package before the first write.
function Get-PlainTree($root) {
    foreach ($item in Get-ChildItem -LiteralPath $root -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Cannot apply an update through a file or directory link: $($item.FullName)"
        }
        $item
        if ($item.PSIsContainer) { Get-PlainTree $item.FullName }
    }
}
function Relative-To($full, $root) { $full.Substring($root.Length + 1).Replace('\', '/') }
function Same-File($path, $entry) {
    (Test-Path -LiteralPath $path -PathType Leaf) -and
        ((Get-Item -LiteralPath $path).Length -eq $entry.Size) -and
        ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $entry.Hash)
}
function Read-Entries($path) {
    foreach ($line in Get-Content -LiteralPath $path) {
        $line = $line.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        if ($line -notmatch '^(APP|MOD)\|([^|]+)\|(\d+)\|([0-9a-f]{64})$') {
            throw "Cannot apply an update with an invalid install manifest: $path"
        }
        $entry = [pscustomobject]@{ Kind = $Matches[1]; Rel = $Matches[2].Replace('\', '/'); Size = [long]$Matches[3]; Hash = $Matches[4] }
        if ($entry.Rel -match '^/|[:*?]|//|(^|/)\.{1,2}(/|$)|\.whs$') {
            throw "Cannot apply an update with an unsupported package path: $($entry.Rel)"
        }
        $entry
    }
}

$roots = @($AppDir, $ModDir, $appSrc, $modSrc) | ForEach-Object {
    $item = Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue
    if (-not $item -or -not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Cannot apply an update without an existing ordinary directory: $_"
    }
    $item.FullName.TrimEnd([char]92, [char]47)
}
for ($i = 0; $i -lt $roots.Count; $i++) {
    for ($j = 0; $j -lt $roots.Count; $j++) {
        if ($i -eq $j) { continue }
        $parent = $roots[$i] + [IO.Path]::DirectorySeparatorChar
        $child = $roots[$j] + [IO.Path]::DirectorySeparatorChar
        if ($child.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Cannot apply an update with overlapping package or install directories.'
        }
    }
}
$AppDir, $ModDir, $appSrc, $modSrc = $roots
$manifest = @(Read-Entries $manifestSrc)
$appEntries = @($manifest | Where-Object Kind -eq APP)
$modEntries = @($manifest | Where-Object Kind -eq MOD)
if ($modEntries.Count -ne 2 -or -not ($modEntries.Rel -contains 'mod.manifest') -or -not ($modEntries.Rel -contains 'Data/kdcmp.pak')) {
    throw 'Cannot apply an update without exactly the two expected mod files.'
}

foreach ($half in @(
    @{ Root = $appSrc; Entries = $appEntries; Extra = @('install-manifest.txt') },
    @{ Root = $modSrc; Entries = $modEntries; Extra = @() }
)) {
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rel in $half.Extra) { [void]$expected.Add($rel) }
    foreach ($entry in $half.Entries) {
        if (-not $expected.Add($entry.Rel) -or -not (Same-File (Join-Path $half.Root $entry.Rel) $entry)) {
            throw "Cannot apply an update with a duplicate or mismatched package file: $($entry.Rel)"
        }
    }
    foreach ($file in Get-PlainTree $half.Root | Where-Object { -not $_.PSIsContainer }) {
        $rel = Relative-To $file.FullName $half.Root
        if (-not $expected.Remove($rel)) { throw "Cannot apply an update with an unlisted package file: $rel" }
    }
    if ($expected.Count -ne 0) { throw 'Cannot apply an update with missing package files.' }
}

# Verify directory identity before copying or pruning. A failed check leaves both targets untouched.
$null = Get-PlainTree $AppDir
$null = Get-PlainTree $ModDir
$oldManifest = Join-Path $AppDir 'install-manifest.txt'
$priorLauncher = if (Test-Path -LiteralPath $oldManifest -PathType Leaf) {
    @(Read-Entries $oldManifest | Where-Object { $_.Kind -eq 'APP' -and $_.Rel -eq 'KCDMP_launcher.exe' })
}
if (-not ($appEntries.Rel -contains 'KCDMP_launcher.exe') -or @($priorLauncher).Count -ne 1 -or
    -not (Same-File (Join-Path $AppDir 'KCDMP_launcher.exe') $priorLauncher)) {
    throw 'Cannot apply an update without a verified KCDMP installation. Use Setup.exe to install or repair it.'
}
$modIdentity = $modEntries | Where-Object Rel -eq 'mod.manifest'
if (-not (Same-File (Join-Path $ModDir 'mod.manifest') $modIdentity)) {
    throw 'Cannot apply an update to an unverified mod folder. Use Setup.exe to review it.'
}

# ------------------------------------------------------- 1  process gate

# Same list as installer\KCDMP.iss's FirstInstallBlocker, and for the same
# reason: these hold open exactly the files about to be replaced. The game is
# on the list because KCDMP.dll is loaded into it.
$blockers = @('KCDMP_launcher', 'KcdMpClient', 'KcdMpServer', 'KcdMpMasterServer', 'KingdomCome')
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $blockers -contains $_.ProcessName }
if ($running) {
    Write-Host 'These are running and hold the files this update replaces:' -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host ("  pid {0,-7} {1}" -f $_.Id, $_.ProcessName) }
    throw 'Close the launcher, the agent, the relay, the master server and the game, then run this again. Applying over running programs is how an update half-applies.'
}

Write-Host "Install directory : $AppDir"
Write-Host "Mod folder        : $ModDir"
Write-Host ''

# --------------------------------------------------------------- 2  copy

# Stamped before anything is written, so an apply that dies half way can never
# leave a previous run's PASS behind as the record of what happened.
$verifyPath = Join-Path $AppDir 'install-verify.txt'
New-Item -ItemType Directory -Force -Path $AppDir, $ModDir, (Join-Path $ModDir 'Data') | Out-Null
Set-Content $verifyPath -Encoding ASCII -Value @(
    'FAIL  update in progress -- Apply.ps1 has not finished',
    '  If this line is still here, the update stopped before it could verify anything.')

function Copy-PackageFiles($from, $to, $paths) {
    foreach ($rel in $paths) {
        $dest = Join-Path $to $rel
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
        if (Test-Path -LiteralPath $dest) { (Get-Item -LiteralPath $dest).IsReadOnly = $false }
        Copy-Item -LiteralPath (Join-Path $from $rel) -Destination $dest -Force
    }
}
Write-Host 'Copying App\ ...'
Copy-PackageFiles $appSrc $AppDir (@($appEntries.Rel) + 'install-manifest.txt')
Write-Host 'Copying Mod\ ...'
Copy-PackageFiles $modSrc $ModDir $modEntries.Rel

# --------------------------------------------------------------- 3  sweep

$appRel = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($e in $appEntries) { [void]$appRel.Add($e.Rel) }

$appFull = (Get-Item $AppDir).FullName
$removed = @()
foreach ($f in Get-ChildItem -LiteralPath $appFull -Recurse -File) {
    if ($f.Name -like 'unins*') { continue }
    $managed = ($f.Extension -in '.dll', '.exe', '.pdb') -or
               ($f.Name -like '*.deps.json') -or ($f.Name -like '*.runtimeconfig.json')
    if (-not $managed) { continue }
    $rel = Relative-To $f.FullName $appFull
    if ($appRel.Contains($rel)) { continue }
    $f.IsReadOnly = $false
    Remove-Item -LiteralPath $f.FullName -Force
    $removed += $rel
}
# Loose pak sources inside the mod folder take over the engine's table root and
# make every base table fail to resolve ("114 tables are not loaded").
#
# The empty directories go too, not just the files in them. The engine mounts
# the DIRECTORY -- an emptied Data\Libs\Tables is no safer than a full one, and
# a files-only sweep leaves exactly that behind. installer\KCDMP.iss's
# PruneModFolder deletes stray directories outright for the same reason.
$modFull = (Get-Item $ModDir).FullName
foreach ($stray in Get-ChildItem -LiteralPath $modFull -Recurse -File -ErrorAction SilentlyContinue) {
    $rel = Relative-To $stray.FullName $modFull
    # Locally generated deltas and their source/run proof survive an update. The launcher
    # revalidates the facial delta before the next start, removing it after asset changes.
    if ($rel -notin @('mod.manifest', 'Data/kdcmp.pak', 'Data/kdcmp_keys.pak',
                     'Data/kdcmp_facial_fallback.pak', 'kdcmp_facial_fallback.state.xml')) {
        $stray.IsReadOnly = $false
        Remove-Item -LiteralPath $stray.FullName -Force
        $removed += "mod: $rel"
    }
}
# Deepest first, so a nested tree collapses in one pass. Only 'Data' survives,
# and only because kdcmp.pak lives in it.
foreach ($dir in (Get-ChildItem -LiteralPath $modFull -Recurse -Directory -ErrorAction SilentlyContinue |
                  Sort-Object { $_.FullName.Length } -Descending)) {
    $rel = Relative-To $dir.FullName $modFull
    if ($rel -eq 'Data') { continue }
    if ((Get-ChildItem -LiteralPath $dir.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item -LiteralPath $dir.FullName -Force
        $removed += "mod: $rel\"
    }
}

# ------------------------------------------------------------- 4  verify

$failures = @()
foreach ($e in $manifest) {
    $base = if ($e.Kind -eq 'MOD') { $ModDir } else { $AppDir }
    $full = Join-Path $base $e.Rel
    $label = if ($e.Kind -eq 'MOD') { "game mod: $($e.Rel)" } else { $e.Rel }
    if (-not (Test-Path -LiteralPath $full)) { $failures += "$label  (missing)"; continue }
    $item = Get-Item -LiteralPath $full
    if ($item.Length -ne [int64]$e.Size) {
        $failures += ("{0}  ({1} bytes, expected {2})" -f $label, $item.Length, $e.Size); continue
    }
    if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $e.Hash) {
        $failures += "$label  (wrong content -- sha256 differs)"
    }
}

$verdict = @()
if ($failures.Count -eq 0) {
    $verdict += "PASS  $($manifest.Count) component(s) verified by sha256 against the install manifest"
} else {
    $verdict += "FAIL  $($failures.Count) component(s) did not install correctly:"
    $verdict += $failures | ForEach-Object { "  $_" }
}
$verdict += "applied by Apply.ps1   repaired $($removed.Count) stale file(s) that no release ships"
$verdict += $removed | ForEach-Object { "  removed $_" }
Set-Content $verifyPath -Value $verdict -Encoding ASCII

$verdict | ForEach-Object { Write-Host $_ -ForegroundColor $(if ($failures.Count -eq 0) { 'Green' } else { 'Red' }) }

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'THIS UPDATE IS NOT COMPLETE. Close everything KCDMP and run this again.' -ForegroundColor Red
    exit 1
}
Write-Host ''
Write-Host 'Update applied and verified.' -ForegroundColor Green
