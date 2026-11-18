# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
[CmdletBinding()]
param([string] $KdcmpLua = '', [string] $MoonSharpVersion = '2.0.0.0')
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $KdcmpLua) { $KdcmpLua = Join-Path $toolsDir '..\kdcmp\Data\Scripts\Startup\kdcmp.lua' }
$source = Get-Content $KdcmpLua -Raw
$at = $source.IndexOf('-- ===== P0/T3:', [StringComparison]::Ordinal)
if ($at -lt 0) { throw 'P0/T3 console block missing from kdcmp.lua' }
$end = $source.IndexOf('-- ===== P1:', $at, [StringComparison]::Ordinal)
if ($end -lt 0) { $end = $source.Length }
$isolated = Join-Path ([IO.Path]::GetTempPath()) ('kcd-t3-console-' + [Guid]::NewGuid().ToString('N') + '.lua')
try {
    [IO.File]::WriteAllText($isolated, $source.Substring($at, $end - $at), (New-Object Text.UTF8Encoding($false)))
    & (Join-Path $toolsDir 'Test-NpcSmoothSynthetic.ps1') -KdcmpLua $isolated -MoonSharpVersion $MoonSharpVersion `
        -Scenario (Join-Path $toolsDir 'Test-T3ConsoleSynthetic.lua') -Title 'P0/T3 console synthetic test'
    $code = $LASTEXITCODE
} finally { Remove-Item $isolated -Force -ErrorAction SilentlyContinue }
exit $code
