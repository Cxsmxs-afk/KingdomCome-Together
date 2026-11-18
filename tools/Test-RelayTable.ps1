# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
<#
.SYNOPSIS
    WO-203 II.5 gate: the relay passes every message type. Every type the protocol defines has a
    relay rule, every rule crosses the REAL relay at its limits, and every W164 builder's worst
    case fits its row.

.DESCRIPTION
    Session C (0.62.0) lost 463 W164 frames (0x74) at the relay -- every WO-201 speech line and
    some votes -- counted only as "unknown-or-wrong-length" in a log nobody read. The type was in
    the relay's table; its row's text limit (80 bytes) was older than the senders. This gate runs
    the two test sets that would have caught it:

      1. dotnet\KcdMp.Relay.Tests, the RelayTableGate tests: reflection over Protocol's message ids
         against Protocol.RelayUp + Protocol.JoinWire (no type without a rule, no rule without a
         type, no id used twice or both ways); every join row through the real relay at its
         minimum and maximum, one byte past each dropped AND counted, the framing surviving; every
         other up type round-tripped with a minimal valid body and its wrong lengths counted; Act
         I's and Act II's channels (0x76/0x78) carrying 1400-byte texts both ways.
      2. dotnet\KcdMp.Client.Tests, the Wo203W164Fit tests: every W164 kind's builder at its worst
         case fits the row, and LootMsg.BuildUp refuses a text over its row before it is sent.

    Each filter must run its full baseline count without any skipped tests.
    Prints one RESULT line; exit 0 = PASS, 1 = a test failed or did not run.
    Run by tools\Build-Installer.ps1 just before the relay round-trip gate.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Test-RelayTable.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Required-TestResults.ps1')

# The .NET 8 SDK is user-scope on the build machine; the machine-wide dotnet may be another version.
$profileDir = if ($env:USERPROFILE) { $env:USERPROFILE } else { [Environment]::GetFolderPath('UserProfile') }
$sdk = Join-Path $profileDir '.dotnet-sdk8'
if (Test-Path (Join-Path $sdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $sdk
    $env:PATH = "$sdk;$env:PATH"
} elseif (-not $env:DOTNET_ROOT) {
    Write-Host "  note: $sdk not found and DOTNET_ROOT unset -- using the dotnet on PATH"
}

try {
    Invoke-KcdManagedTests (Join-Path $root 'dotnet/KcdMp.Relay.Tests/KcdMp.Relay.Tests.csproj') relay-table 119 `
        -Filter 'FullyQualifiedName~KcdMp.Relay.Tests.RelayTableGateTests.' -Configuration $Configuration
    Invoke-KcdManagedTests (Join-Path $root 'dotnet/KcdMp.Client.Tests/KcdMp.Client.Tests.csproj') w164-fit 3 `
        -Filter 'FullyQualifiedName~KcdMp.Client.Tests.Wo203W164FitTests.' -Configuration $Configuration
    Write-Host 'RESULT: PASS relay-table and w164-fit' -ForegroundColor Green
}
catch {
    Write-Host "RESULT: FAIL $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
exit 0
