# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('EndToEnd', 'Abort')][string]$Check
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
        A_retired_join_cannot_cross_the_engine_HTTP_boundary = 4
        A_retired_join_cannot_send_an_already_queued_ready = 4
        A_late_engine_read_keeps_the_file_safe_and_cannot_make_an_aborted_join_ready = 4
        Guest_transport_loss_after_load_returns_to_the_own_world_only_after_the_engine_finishes = 1
        Targeted_join_loads_with_other_real_relay_peers_present = 2
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
$aborts = @(foreach ($line in $outputLines) {
    if ($line -cmatch '^MP-JOIN joiner: join (0x[0-9a-f]{8}) ABORTED \(clock-unavailable\) -- JoinAbort ') { $Matches[1] }
})
if ($aborts.Count -ne 5 -or @($aborts | Sort-Object -Unique).Count -ne 5) {
    throw "H19 probe requires five distinct production aborts (recorded $($aborts.Count), exit $status)"
}
$lifecycle = @($outputLines | Where-Object { $_ -ceq 'JOIN-ABORT-LIFECYCLE playline_reused=1 submitted_preserved=1' })
if ($lifecycle.Count -ne 1) { throw 'H19 did not prove playline reuse and preservation of a submitted load' }
if ($status -ne 0 -or $outputLines[-1] -cne 'JOIN-ABORT-GATE attempts=5 retained_slots=0 own_save_unchanged=1') {
    throw "Required join-abort check expected five attempts with zero retained slots and an unchanged personal save (exit $status)"
}

. (Join-Path $PSScriptRoot 'Required-TestResults.ps1')
$clientTests = Join-Path $root 'dotnet/KcdMp.Client.Tests/KcdMp.Client.Tests.csproj'
$requiredAbortTests = @(
    'JoinCalendarIntegrationTests.Repeated_preload_aborts_reuse_the_playline_and_require_manual_retry_until_the_host_world_changes',
    'JoinWorldJournalTests.Restart_reclaims_a_torn_private_stage_without_touching_the_visible_save',
    'JoinWorldJournalTests.A_replacement_between_identity_check_and_retirement_is_restored_without_deleting_it',
    'JoinWorldJournalTests.Restart_restores_a_foreign_file_left_in_quarantine',
    'JoinLateEngineReceiptTests.Late_engine_completion_releases_only_the_retired_join_file',
    'JoinLateEngineReceiptTests.Unrelated_or_new_process_receipts_do_not_release_a_submitted_join_file',
    'JoinLateEngineReceiptTests.A_pathless_terminal_after_two_unsettled_reads_proves_neither_attempt'
)
foreach ($case in $requiredAbortTests) {
    Invoke-KcdManagedTests $clientTests "H19 $case" 1 -Filter "FullyQualifiedName=KcdMp.Client.Tests.$case"
}
$case = 'JoinWorldJournalTests.A_replacement_after_quarantine_verification_is_never_deleted_or_truncated'
Invoke-KcdManagedTests $clientTests "H19 $case" 2 -Filter "FullyQualifiedName=KcdMp.Client.Tests.$case"
