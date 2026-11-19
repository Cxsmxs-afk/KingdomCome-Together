# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('EndToEnd', 'Abort')][string]$Check,
    [switch]$ExpectKnownFailure
)

$ErrorActionPreference = 'Stop'
# We explicitly check the child process status below, including the staged H19 result.
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot

if ($Check -eq 'EndToEnd') {
    $tests = Join-Path $root 'dotnet/KcdMp.Client.Tests'
    $fixture = Join-Path $tests 'JoinCalendarIntegrationTests.cs'
    if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'Required H20 End-to-End-Join fixture missing' }

    . (Join-Path $PSScriptRoot 'Required-TestResults.ps1')
    $project = Join-Path $tests 'KcdMp.Client.Tests.csproj'
    $cases = [ordered] @{
        A_hosts_and_B_joins_from_the_main_menu = 2
        B_hosts_and_A_joins_from_the_main_menu = 2
        A_delayed_calendar_can_arrive_after_five_seconds_while_the_host_still_waits = 1
        Authenticated_stale_calendar_cannot_arm_the_latch_and_duplicates_arm_it_once = 1
        Host_loss_retires_the_join_before_the_guest_can_load = 2
        Host_does_not_offer_a_world_the_native_pipe_never_confirmed_as_held = 1
        Menu_progress_cannot_renew_the_host_hold_before_the_calendar_receipt = 1
    }
    foreach ($case in $cases.Keys) {
        Invoke-KcdManagedTests $project "End-to-End-Join $case" $cases[$case] `
            -Filter "FullyQualifiedName=KcdMp.Client.Tests.JoinCalendarIntegrationTests.$case"
    }
    return
}

$project = Join-Path $PSScriptRoot 'sim/KcdMp.Sim/KcdMp.Sim.csproj'
$outputLines = @(& dotnet run --project $project -c Release -- --check-join-abort)
$status = $LASTEXITCODE
$outputLines | Write-Host
$aborts = @($outputLines | Where-Object { $_ -cmatch '^MP-JOIN joiner: join 0x[0-9a-f]{8} ABORTED \(clock-unavailable\) -- JoinAbort ' })
if ($aborts.Count -ne 5) { throw "H19 probe recorded $($aborts.Count) of 5 production aborts (exit $status)" }
if ($ExpectKnownFailure) {
    if ($status -eq 19 -and $outputLines[-1] -ceq 'JOIN-ABORT-GATE attempts=5 retained_slots=5 own_save_unchanged=1') {
        Write-Warning 'EXPECTED FAILURE H19: five pre-load aborts retained five join slots. The personal save was preserved.'
        # The Actions pwsh wrapper exits with LASTEXITCODE after the script returns.
        exit 0
    }
    if ($status -eq 0) { throw 'H19 probe passed. Enable the required join-abort assertion.' }
    throw "H19 probe did not reproduce the exact known failure (exit $status)"
}
if ($status -ne 0 -or $outputLines[-1] -cnotmatch '^JOIN-ABORT-GATE attempts=5 retained_slots=[01] own_save_unchanged=1$') {
    throw "Required join-abort check failed (exit $status)"
}
