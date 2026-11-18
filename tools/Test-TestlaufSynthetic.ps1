# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS).
# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../kdcmp/Data/Scripts/Startup/kdcmp.lua') -Raw
$start = $source.IndexOf('-- ===== T5: guided test run console', [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'The production guided-run command block is missing.' }
$end = $source.IndexOf("`n-- ===== ", $start, [StringComparison]::Ordinal)
if ($end -lt 0) { throw 'The production guided-run command block has no ending marker.' }

$block = [IO.Path]::GetTempFileName()
try {
    Set-Content -LiteralPath $block -Value $source.Substring($start, $end - $start) -Encoding utf8
    & (Join-Path $PSScriptRoot 'Test-NpcSmoothSynthetic.ps1') `
        -KdcmpLua $block -Scenario (Join-Path $PSScriptRoot 'Test-TestlaufSynthetic.lua') `
        -Title 'T5 guided-run console synthetic test'
    $status = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $block -Force
}
exit $status
