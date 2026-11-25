# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.

function Assert-KcdTrxResults {
    param([string]$Path, [string]$Suite, [ValidateRange(1, 2147483647)][int]$MinimumCount)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Suite did not produce a TRX result" }
    [xml]$report = Get-Content -LiteralPath $Path -Raw
    $names = [System.Xml.XmlNamespaceManager]::new($report.NameTable)
    $names.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $totals = $report.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $names)
    if (-not $totals) { throw "$Suite did not report test counters" }

    $counts = @{}
    foreach ($metric in @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted',
                         'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted',
                         'disconnected', 'inProgress', 'pending')) {
        $count = 0
        if (-not [int]::TryParse($totals.GetAttribute($metric), [ref]$count) -or $count -lt 0) {
            throw "$Suite reported an invalid $metric count"
        }
        $counts[$metric] = $count
    }

    $summary = "total=$($counts.total) executed=$($counts.executed) passed=$($counts.passed) failed=$($counts.failed) notExecuted=$($counts.notExecuted)"
    if ($counts.total -lt $MinimumCount) { throw "$Suite count below baseline $MinimumCount ($summary)" }
    foreach ($metric in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted',
                         'notRunnable', 'notExecuted', 'disconnected', 'inProgress', 'pending')) {
        if ($counts[$metric] -ne 0) { throw "$Suite reported $metric=$($counts[$metric]) ($summary)" }
    }

    $results = $report.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $names)
    if ($totals.ParentNode.GetAttribute('outcome') -ne 'Completed' -or
        $counts.executed -ne $counts.total -or $counts.passed -ne $counts.total -or $results.Count -ne $counts.total) {
        throw "$Suite has missing or inconsistent test results ($summary, rows=$($results.Count))"
    }
    $executions = [System.Collections.Generic.HashSet[guid]]::new()
    $resourceCycles = @{}
    $resourceSummaries = [System.Collections.Generic.HashSet[string]]::new()
    $handleBudgets = [System.Collections.Generic.HashSet[string]]::new()
    $threadBudgets = [System.Collections.Generic.HashSet[string]]::new()
    $fileBudgets = [System.Collections.Generic.HashSet[string]]::new()
    $resourceScenarios = @('mpexit', 'mpexit-receipt', 'transfer-player-a', 'transfer-player-b', 'transfer-host', 'transfer-eof',
                          'missing-personal-save', 'cancel-rejoin-2', 'cancel-rejoin-3', 'cancel-rejoin-4', 'return-deadline',
                          'active-guests-3', 'active-guests-4', 'host-return-2', 'host-return-3', 'host-return-4',
                          'mpexit-retained-timer', 'mpexit-retained-handle')
    $timerOrigin = 0
    $timerTwin = 0
    $handleTwin = 0
    foreach ($result in $results) {
        $id = [guid]::Empty
        if (-not [guid]::TryParse($result.GetAttribute('executionId'), [ref]$id) -or
            $id -eq [guid]::Empty -or -not $executions.Add($id)) {
            throw "$Suite has a missing or duplicate execution id ($($result.GetAttribute('testName')))"
        }
        if ($result.GetAttribute('outcome') -ne 'Passed') {
            throw "$Suite did not pass $($result.GetAttribute('testName')) ($($result.GetAttribute('outcome')))"
        }
        if ($Suite -eq 'Client' -and $result.GetAttribute('testName').StartsWith('KcdMp.Client.Tests.JoinCalendar')) {
            $stdout = $result.SelectSingleNode('t:Output/t:StdOut', $names)
            if ($stdout) {
                foreach ($line in $stdout.InnerText -split '\r?\n') {
                    if ($line -match '^\s*F4-RESOURCES scenario=(?<scenario>[a-z0-9-]+) cycle=(?<cycle>[0-5])(?: [a-z_]+=[0-9]+)+\s*$') {
                        $scenario = $Matches.scenario
                        if (-not $resourceCycles.ContainsKey($scenario)) {
                            $resourceCycles[$scenario] = [System.Collections.Generic.HashSet[int]]::new()
                        }
                        if (-not $resourceCycles[$scenario].Add([int]$Matches.cycle)) { throw "Duplicate resource cycle for $scenario" }
                    } elseif ($line -match '^\s*F4-RESOURCES scenario=(?<scenario>[a-z0-9-]+)(?: [a-z_]+=[0-9]+->[0-9]+)+\s*$') {
                        $scenario = $Matches.scenario
                        if (-not $resourceSummaries.Add($scenario)) { throw "Duplicate resource summary for $scenario" }
                    } elseif ($line -match '^\s*F4-HANDLE-TYPES scenario=(?<scenario>[a-z0-9-]+) cycle=[0-5] (?:count=[0-9]+ types=[A-Za-z0-9_=,]+(?: thread_[a-z_]+=[0-9]+)*|status=[0-9a-f]{8}|bad_count=1)\s*$') {
                        $scenario = $Matches.scenario
                    } elseif ($line -match '^\s*F4-HANDLE-BUDGET scenario=(?<scenario>[a-z0-9-]+) before=(?<before>[0-9]+) after=(?<after>[0-9]+) winsock_pairs=(?<pairs>[0-9]+) regex_cache_events=(?<regex>[01]) worker_starts=(?<workers>[0-9]+) worker_name_events=(?<workerNames>[0-9]+) compared_after=(?<compared>[0-9]+)\s*$') {
                        $scenario = $Matches.scenario
                        if (-not $handleBudgets.Add($scenario) -or
                            [int]$Matches.workerNames -gt [int]$Matches.workers -or
                            [int]$Matches.compared -ne ([int]$Matches.after - 2 * [int]$Matches.pairs - [int]$Matches.regex - 3 * [int]$Matches.workers - [int]$Matches.workerNames)) {
                            throw "Invalid or duplicate handle accounting for $scenario"
                        }
                    } elseif ($line -match '^\s*F4-THREAD-BUDGET scenario=(?<scenario>[a-z0-9-]+) before=(?<before>[0-9]+) after=(?<after>[0-9]+) idle_new_workers=(?<idle>[0-9]+) compared_after=(?<compared>[0-9]+)\s*$') {
                        $scenario = $Matches.scenario
                        if (-not $threadBudgets.Add($scenario) -or [int]$Matches.compared -ne ([int]$Matches.after - [int]$Matches.idle)) {
                            throw "Invalid or duplicate thread accounting for $scenario"
                        }
                    } elseif ($line -match '^\s*F4-FILE-BUDGET scenario=(?<scenario>[a-z0-9-]+) before=(?<before>[0-9]+) after=(?<after>[0-9]+)\s*$') {
                        $scenario = $Matches.scenario
                        $overBudget = [int]$Matches.after -gt [int]$Matches.before + 4
                        if (-not $fileBudgets.Add($scenario) -or $overBudget -ne ($scenario -eq 'mpexit-retained-handle')) {
                            throw "Invalid or duplicate file handle accounting for $scenario"
                        }
                    } elseif ($line -match '^\s*F4-HANDLE-ORIGINS scenario=(?<scenario>[a-z0-9-]+) enabled=[01] .+\s*$') {
                        $scenario = $Matches.scenario
                    } elseif ($line -match '^\s*F4-POOL-WARMUP scenario=(?<scenario>[a-z0-9-]+) primed=[0-9]+ worker_min=[0-9]+ worker_max=[0-9]+ after_join_workers=[0-9]+ reached=[0-9]+ target=[0-9]+\s*$') {
                        $scenario = $Matches.scenario
                    } elseif ($line -match '^\s*F4-TIMER-DRAIN scenario=(?<scenario>[a-z0-9-]+) cycle=[05] owner=ProcessorFrameScope cancelled=[0-9]+ runtime_timers=[0-9]+->[0-9]+\s*$') {
                        $scenario = $Matches.scenario
                    } elseif ($line -match '^\s*F4-TIMER-SAMPLE scenario=(?<scenario>[a-z0-9-]+) phase=(?:baseline|observed|settled) count=[0-9]+ frame_deadlines=[0-9]+ owner=ProcessorFrameScope\s*$') {
                        $scenario = $Matches.scenario
                    } elseif ($line -match '^\s*F4-TIMER-TWIN scenario=(?<scenario>mpexit-retained-timer) retained=5 result=budget-rejected\s*$') {
                        $scenario = $Matches.scenario
                        $timerTwin++
                    } elseif ($line -match '^\s*F4-HANDLE-TWIN scenario=(?<scenario>mpexit-retained-handle) retained=10 result=budget-rejected\s*$') {
                        $scenario = $Matches.scenario
                        $handleTwin++
                    } elseif ($line -match '^\s*F4-TIMER-ORIGIN cancelled=2 unowned_active=1 owner=ProcessorFrameScope\s*$') {
                        $timerOrigin++
                        Write-Host $line.Trim()
                        continue
                    } else { continue }
                    if ($scenario -cnotin $resourceScenarios) { throw 'Unknown join resource scenario' }
                    Write-Host $line.Trim()
                }
            }
        }
    }
    if ($Suite -eq 'Client') {
        if ($timerOrigin -ne 1 -or $timerTwin -ne 1 -or $handleTwin -ne 1) { throw 'Missing or duplicate F4 control result' }
        foreach ($scenario in $resourceScenarios) {
            if (-not $resourceSummaries.Contains($scenario) -or -not $handleBudgets.Contains($scenario) -or -not $threadBudgets.Contains($scenario) -or
                ($IsWindows -and -not $fileBudgets.Contains($scenario)) -or
                -not $resourceCycles.ContainsKey($scenario) -or
                -not $resourceCycles[$scenario].SetEquals([int[]](0..5))) {
                throw "Incomplete join resource checks for $scenario"
            }
        }
    }
    Write-Host "Required suite $Suite PASS: $summary (minimum $MinimumCount)"
}

function Invoke-KcdManagedTests {
    param([string]$Project, [string]$Suite, [ValidateRange(1, 2147483647)][int]$MinimumCount,
          [string]$Filter = '', [string]$Configuration = 'Release')

    $dir = Join-Path ([IO.Path]::GetTempPath()) ('kcdmp-trx-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        $testArgs = @('test', $Project, '-c', $Configuration, '--nologo', '-v', 'q', '--results-directory', $dir,
                      '--logger', 'console;verbosity=minimal', '--logger', 'trx;LogFileName=required.trx')
        if ($Filter) { $testArgs += @('--filter', $Filter) }
        & dotnet @testArgs
        if ($LASTEXITCODE -ne 0) { throw "Required suite $Suite failed (dotnet test exit $LASTEXITCODE)" }
        Assert-KcdTrxResults (Join-Path $dir 'required.trx') $Suite $MinimumCount
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force }
}

function Assert-KcdNativeResults {
    param([string[]]$OutputLines, [ValidateRange(1, 2147483647)][int]$MinimumCount)

    $pattern = '^(\d+) passed, (\d+) failed$'
    $summaries = @($OutputLines | Where-Object { $_ -match $pattern })
    if ($summaries.Count -ne 1 -or $OutputLines[-1] -ne $summaries[0]) {
        throw 'Native tests did not produce one final result count'
    }
    $counts = [regex]::Match($summaries[0], $pattern).Groups
    if ([int]$counts[2].Value -ne 0 -or [int]$counts[1].Value -lt $MinimumCount) {
        throw "Native test gate failed ($($summaries[0]), minimum $MinimumCount)"
    }
    Write-Host "Required suite Native PASS: $($summaries[0]) (minimum $MinimumCount)"
}
