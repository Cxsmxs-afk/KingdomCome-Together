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
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $root "release\KCDMP" }
if (-not $OutZip) { $OutZip = Join-Path $root "release\KingdomComeTogether-DropIn.zip" }
$pak = Join-Path $root "kdcmp\Data\kdcmp.pak"
$manifest = Join-Path $root "kdcmp\mod.manifest"
$launcher = Join-Path $ReleaseDir "KCDMP_launcher.exe"
$agent = Join-Path $ReleaseDir "KcdMpClient.exe"
$relay = Join-Path $ReleaseDir "KcdMpServer.exe"
$dll = Join-Path $ReleaseDir "KCDMP.dll"
$installManifest = Join-Path $ReleaseDir "install-manifest.txt"
foreach ($p in @($pak, $manifest, $launcher, $agent, $relay, $dll, $installManifest)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "missing: $p" }
}
if (Get-ChildItem -LiteralPath $ReleaseDir -Recurse -Force -File | Where-Object Extension -ieq '.whs') {
    throw 'The player drop-in cannot contain game saves.'
}

$version = (Get-Content (Join-Path $root "VERSION") -TotalCount 1).Trim()
$buildId = (Get-Content -LiteralPath (Join-Path $root "BUILD_ID") -Raw).Trim()
if ($buildId -cnotmatch '^[0-9a-f]{7,40}-[0-9]{10}$') { throw "Missing or invalid drop-in BUILD_ID." }
$stage = Join-Path $env:TEMP ("kct-dropin-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    $app = Join-Path $stage "KingdomComeTogether"
    Copy-Item $ReleaseDir $app -Recurse -Force
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
PASS  drop-in $version : these files are the verified build
version $version
build $buildId
"@
    [System.IO.File]::WriteAllText((Join-Path $app "install-verify.txt"), $verify.Replace("`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))

    $install = @"
Kingdom Come: Together $version
In den Ordner der KCD2 Modding Tools entpacken. Vorhandenes überschreiben.

Installieren
1. Spiel und Launcher schließen.
2. Steam: Kingdom Come: Deliverance II Modding Tools, Verwalten, Durchsuchen. Das ist der Ordner.
3. Diese Zip dort hinein entpacken. Überschreiben wählen.
4. KingdomComeTogether\KCDMP_launcher.exe starten. Nicht das Spiel direkt aus Steam.
5. Der Launcher prüft, ob die Programme zusammenpassen, und legt den Mod ins Spiel, falls er fehlt.
6. Host oder Beitreten, dann Spielen. Beide brauchen dieselbe Zip.

Aktualisieren
7. Spiel und Launcher schließen. Die neue Zip wie in Schritt 3 darüber entpacken.

Entfernen
8. Ordner KingdomComeTogether löschen.
9. Ordner Mods\kdcmp löschen. Das Spiel selbst bleibt.
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

    if (Test-Path $OutZip) { Remove-Item $OutZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $OutZip)
    Write-Output "Drop-in zip: $OutZip ($((Get-Item $OutZip).Length) bytes)"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
