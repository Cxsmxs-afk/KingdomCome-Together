# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
<#
.SYNOPSIS
    One zip a player can extract into the KCD2 Modding Tools folder.

.DESCRIPTION
    The zip root is the game folder. Extracting it there (overwrite) puts the
    mod in Mods\kdcmp and the launcher, agent, relay, DLL and runtime in
    KingdomComeTogether\. The launcher still checks that those programs are
    one build, and copies the staged mod into the game if it is not there yet.
#>
param(
    [string]$ReleaseDir = "",
    [string]$OutZip = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $root "VERSION") -Raw).Trim()
if ($version -cnotmatch '\A[0-9A-Za-z][0-9A-Za-z.+-]*\z') { throw "Invalid package version in VERSION: '$version'" }
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $root "release\KCDMP" }
if (-not $OutZip) { $OutZip = Join-Path $root "release\KingdomComeTogether-$version.zip" }
$OutZip = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutZip)
if (Test-Path -LiteralPath $OutZip -PathType Container) { throw "ZIP output is a directory: $OutZip" }
$pak = Join-Path $root "kdcmp\Data\kdcmp.pak"
$manifest = Join-Path $root "kdcmp\mod.manifest"
$launcher = Join-Path $ReleaseDir "KCDMP_launcher.exe"
$dll = Join-Path $ReleaseDir "KCDMP.dll"
foreach ($p in @($ReleaseDir, $pak, $manifest, $launcher, $dll)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "missing: $p" }
}

$id = [guid]::NewGuid().ToString("N")
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "kct-dropin-$id"
$zipTemp = Join-Path (Split-Path -Parent $OutZip) "kct-dropin-$id.tmp"
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    $app = Join-Path $stage "KingdomComeTogether"
    Copy-Item -LiteralPath $ReleaseDir -Destination $app -Recurse -Force
    foreach ($rel in @("Mods\kdcmp", "KingdomComeTogether\mod\kdcmp")) {
        $dest = Join-Path $stage $rel
        New-Item -ItemType Directory -Force -Path (Join-Path $dest "Data") | Out-Null
        Copy-Item $manifest (Join-Path $dest "mod.manifest") -Force
        Copy-Item $pak (Join-Path $dest "Data\kdcmp.pak") -Force
    }
    # The launcher locks Host and Join unless this file's first line says PASS.
    # Setup writes it when it finishes; a drop-in never ran Setup, and an older
    # folder may still hold FAIL or nothing. The zip has to bring PASS with it.
    $verify = @"
PASS  drop-in $version
version $version
"@
    [System.IO.File]::WriteAllText((Join-Path $app "install-verify.txt"), $verify.Replace("`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))

    $install = @"
Kingdom Come: Together $version

Installieren
1. Spiel und Launcher schließen.
2. In Steam bei Kingdom Come: Deliverance II Modding Tools „Verwalten“ und „Lokale Dateien durchsuchen“ wählen.
3. Die ZIP in den geöffneten Ordner entpacken und vorhandene Dateien überschreiben.
   Die Ordner KingdomComeTogether und Mods liegen danach direkt im Ordner der Modding Tools.
4. KingdomComeTogether\KCDMP_launcher.exe starten.
5. Im Launcher „Host“ oder „Beitreten“ wählen, dann „Spielen“. Alle verwenden dieselbe ZIP.

Aktualisieren
Spiel und Launcher schließen. Die neue ZIP wie in Schritt 3 darüber entpacken.

Entfernen
Die Ordner KingdomComeTogether und Mods\kdcmp löschen.
"@
    [System.IO.File]::WriteAllText((Join-Path $stage "INSTALL.txt"), $install.Replace("`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))

    $test = @"
Test zu zweit. Beide dieselbe Zip. Zuerst nur zu zweit.

1. Nebeneinander durch ein Dorf gehen. Die Leute sollen eine Gangart halten, nicht zwischen zwei Stellen springen.
2. Der andere geht, sprintet und bleibt stehen.
3. Kurz reiten und einen Kampf anfangen.
4. Eine Truhe: jeder sieht seine eigene. Ein Laden muss nicht gleich aussehen.

Wenn das zu zweit stimmt, denselben Weg mit einer dritten und vierten Person in der Nähe wiederholen.
"@
    [System.IO.File]::WriteAllText((Join-Path $stage "TEST.txt"), $test.Replace("`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipTemp)
    & (Join-Path $PSScriptRoot "package/Test-DropIn.ps1") -ZipPath $zipTemp
    Move-Item -LiteralPath $zipTemp -Destination $OutZip -Force
    $zip = Get-Item -LiteralPath $OutZip
    Write-Host "Drop-in zip: $($zip.FullName) ($($zip.Length) bytes)"
    $zip
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $zipTemp -Force -ErrorAction SilentlyContinue
}
