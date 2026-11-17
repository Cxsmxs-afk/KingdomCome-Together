# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
    F5: full worn-set repair, slot-removal order and created pieces against the real mod under MoonSharp.
.DESCRIPTION
    Engine inventory and equipment are modeled; real in-game slot behavior remains a two-PC test.
    Picked up automatically by Build-Installer.ps1's Test-*Synthetic.ps1 release gate.
#>
[CmdletBinding()]
param([string] $KdcmpLua = '', [string] $MoonSharpVersion = '2.0.0.0')
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $toolsDir 'Test-NpcSmoothSynthetic.ps1') `
    -KdcmpLua $KdcmpLua -MoonSharpVersion $MoonSharpVersion `
    -Scenario (Join-Path $toolsDir 'Test-OutfitRepairSynthetic.lua') `
    -Title 'F5 full worn-set repair synthetic test'
exit $LASTEXITCODE
