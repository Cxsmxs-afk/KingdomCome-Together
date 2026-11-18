# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS).
# SPDX-License-Identifier: GPL-3.0-only
# Runs the production gate and round with fixture files and an injected command runner.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Solo-Abend.ps1')

$script:passed = 0
$script:failed = 0
function Assert-Test {
    param([string]$Name, [bool]$Passed)
    if ($Passed) {
        $script:passed++
        Write-Host "PASS $Name"
    } else {
        $script:failed++
        Write-Host "FAIL $Name"
    }
}

function Test-Refused {
    param([string]$Value, [bool]$Switch)
    try {
        Assert-SoloAbendOptIn -Variable $Value -Schalter $Switch
        return $false
    } catch {
        return $_.Exception.Message -match 'KCDMP_TEST_SOLO_ABEND=1' -and
            $_.Exception.Message -match 'TestSoloAbend'
    }
}

function New-Adapter {
    param([string]$Name, [int]$Mb = 16376, [bool]$Software = $false)
    [pscustomobject]@{ name = $Name; dedicated_mb = $Mb; software = $Software }
}

function New-RoundFixture {
    param([string]$Cohort = 'pair')
    $runs = Join-Path $script:scratch ([Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $runs
    $id = "abend-fixture-$Cohort"
    @{
        Cohort = $Cohort
        Runs = $runs
        Id = $id
        RunDir = Join-Path $runs $id
        Report = Join-Path $runs 'abend.json'
        Commands = [System.Collections.Generic.List[object]]::new()
        Exit = @{}
        ThrowAt = ''
        CollectB = $true
        WritePair = $true
    }
}

function Invoke-Fixture {
    param([hashtable]$Fixture, [string]$Netz = 'normal')

    $fakePython = {
        param([string]$Program, [string[]]$Arguments, [string]$Directory)
        $step = $Arguments[0]
        if ($step -eq 'up') { $step = 'up ' + $Arguments[1] }
        if ($Program -eq 'rig/scenarios.py') { $step = 'scenario' }
        if ($Program -eq 'verdict/pair.py') { $step = 'verdict' }
        $Fixture.Commands.Add([pscustomobject]@{
            Step = $step
            Program = $Program
            Arguments = @($Arguments)
            Directory = $Directory
            Run = $env:KCDMP_RIG_RUN
            Runs = $env:KCDMP_RIG_RUNS
        })
        if ($Fixture.ThrowAt -eq $step) { throw "fixture $step threw" }
        if ($step -eq 'collect') {
            foreach ($copy in @('A', 'B')) {
                if ($copy -eq 'B' -and -not $Fixture.CollectB) { continue }
                $dir = Join-Path $Fixture.RunDir "bundles/abend-$copy.packed"
                $null = New-Item -ItemType Directory -Path $dir -Force
                [IO.File]::WriteAllText((Join-Path $dir 'ERGEBNIS.txt'), "fixture $copy")
            }
        }
        if ($step -eq 'verdict' -and $Fixture.WritePair) {
            [IO.File]::WriteAllText((Join-Path $Directory 'ERGEBNIS-PAAR.md'), 'fixture pair verdict')
        }
        if ($Fixture.Exit.ContainsKey($step)) { return [int]$Fixture.Exit[$step] }
        return 0
    }.GetNewClosure()

    $result = @(Invoke-SoloAbendRound -Cohort $Fixture.Cohort -Netz $Netz -RunId $Fixture.Id `
        -RunsRoot $Fixture.Runs -Playline 4 -Save 'save440' -Report $Fixture.Report -RunPython $fakePython `
        -ScenarioArguments @('--bandit', 'fixture-bandit') 6>&1)
    [pscustomobject]@{
        Code = $result[-1]
        Info = @($result | Where-Object { $_ -is [System.Management.Automation.InformationRecord] })
        Steps = $Fixture.Commands.Step -join '|'
    }
}

$script:scratch = Join-Path ([IO.Path]::GetTempPath()) ('kcdmp-solo-test-' + [Guid]::NewGuid().ToString('N'))
$oldRun = $env:KCDMP_RIG_RUN
$oldRuns = $env:KCDMP_RIG_RUNS
try {
    $null = New-Item -ItemType Directory -Path $script:scratch
    $env:KCDMP_RIG_RUN = 'caller-run'
    $env:KCDMP_RIG_RUNS = 'caller-runs'

    Assert-SoloAbendOptIn -Variable '1' -Schalter $true
    Assert-Test 'both test opt-ins allow the gate' $true
    Assert-Test 'environment alone is refused' (Test-Refused '1' $false)
    Assert-Test 'switch alone is refused' (Test-Refused '' $true)
    Assert-Test 'environment must be an exact opt-in' (Test-Refused 'true' $true)

    $card = New-Adapter 'NVIDIA GeForce RTX 4080 SUPER'
    Assert-Test '32 GiB and a hardware RTX 4080 SUPER are accepted' (Test-SoloAbendHardware 32GB @($card))
    Assert-Test 'wrong installed RAM is rejected' (-not (Test-SoloAbendHardware 31GB @($card)) -and
        -not (Test-SoloAbendHardware 64GB @($card)))
    Assert-Test '4080 without SUPER and a misleading suffix are rejected' (
        -not (Test-SoloAbendHardware 32GB @((New-Adapter 'NVIDIA GeForce RTX 4080'))) -and
        -not (Test-SoloAbendHardware 32GB @((New-Adapter 'NVIDIA GeForce RTX 4080 SUPER Ti'))))
    Assert-Test 'software adapter and insufficient visible VRAM are rejected' (
        -not (Test-SoloAbendHardware 32GB @((New-Adapter 'RTX 4080 SUPER' 16376 $true))) -and
        -not (Test-SoloAbendHardware 32GB @((New-Adapter 'RTX 4080 SUPER' 15359))))
    Assert-Test 'target adapter can coexist with another GPU' (
        Test-SoloAbendHardware 32GB @((New-Adapter 'Other GPU'), $card))

    $pair = New-RoundFixture
    $run = Invoke-Fixture $pair
    $order = 'prepare|relay|up A|up B|host|join|scenario|collect|verdict|down'
    Assert-Test 'pair workflow runs in the exact order' ($run.Code -eq 0 -and $run.Steps -ceq $order)
    Assert-Test 'second launch preserves the manual confirmation and German prompt' (
        ($pair.Commands[3].Arguments -join '|') -ceq 'up|B|--manual-confirm' -and
        ($run.Info -join ' ') -match 'Bitte im zweiten Fenster.+Ja.+klicken')
    Assert-Test 'arguments carry loopback, test cohort, report and the selected save' (
        ($pair.Commands[1].Arguments -join '|') -ceq 'relay|--loopback|--netz|normal' -and
        ($pair.Commands[4].Arguments -join '|') -ceq 'host|A|4|save440' -and
        ($pair.Commands[6].Arguments -join '|') -ceq
            "abend|--test-solo-abend|--cohort|pair|--netz|normal|--output|$($pair.Report)|--playline|4|--save|save440|--bandit|fixture-bandit")
    Assert-Test 'pair verdict reads both current result directories from the run directory' (
        $pair.Commands[8].Directory -eq $pair.RunDir -and
        $pair.Commands[8].Arguments[0] -eq (Join-Path $pair.RunDir 'bundles/abend-A.packed') -and
        $pair.Commands[8].Arguments[1] -eq (Join-Path $pair.RunDir 'bundles/abend-B.packed') -and
        (Test-Path -LiteralPath (Join-Path $pair.RunDir 'ERGEBNIS-PAAR.md') -PathType Leaf))
    Assert-Test 'all commands, including down, see the scoped run environment' (
        @($pair.Commands | Where-Object { $_.Run -ne $pair.Id -or $_.Runs -ne $pair.Runs }).Count -eq 0)

    $start = New-RoundFixture
    $start.Exit['up B'] = 12
    $run = Invoke-Fixture $start
    Assert-Test 'startup failure stops later steps and still invokes down' (
        $run.Code -eq 2 -and $run.Steps -ceq 'prepare|relay|up A|up B|down')

    $scenario = New-RoundFixture
    $scenario.Exit['scenario'] = 17
    $run = Invoke-Fixture $scenario
    Assert-Test 'scenario failure keeps its code and still collects and evaluates' (
        $run.Code -eq 17 -and $run.Steps -ceq $order)

    $collect = New-RoundFixture
    $collect.CollectB = $false
    $run = Invoke-Fixture $collect
    Assert-Test 'a missing current result never starts the pair verdict' (
        $run.Code -eq 2 -and $run.Steps -ceq 'prepare|relay|up A|up B|host|join|scenario|collect|down')

    $verdict = New-RoundFixture
    $verdict.WritePair = $false
    $run = Invoke-Fixture $verdict
    Assert-Test 'a missing final report fails even when the verdict process returned zero' (
        $run.Code -eq 2 -and $run.Steps -ceq $order)

    $cleanup = New-RoundFixture
    $cleanup.ThrowAt = 'down'
    $run = Invoke-Fixture $cleanup
    Assert-Test 'cleanup failure is distinguished and down runs exactly once' (
        $run.Code -eq 3 -and $run.Steps -ceq $order)
    Assert-Test 'the caller environment is restored even if down throws' (
        $env:KCDMP_RIG_RUN -ceq 'caller-run' -and $env:KCDMP_RIG_RUNS -ceq 'caller-runs')

    $reused = New-RoundFixture
    $null = New-Item -ItemType Directory -Path $reused.RunDir
    $old = Join-Path $reused.RunDir 'ERGEBNIS-PAAR.md'
    [IO.File]::WriteAllText($old, 'old result')
    $run = Invoke-Fixture $reused
    Assert-Test 'preexisting output neither supplies a verdict nor stops another run' (
        $run.Code -eq 2 -and $reused.Commands.Count -eq 0 -and [IO.File]::ReadAllText($old) -ceq 'old result')

    $disconnect = New-RoundFixture 'disconnect'
    $run = Invoke-Fixture $disconnect 'schlecht'
    Assert-Test 'only the disconnect cohort requests the isolated relay cut' (
        $run.Code -eq 0 -and $run.Steps -ceq $order -and
        ($disconnect.Commands[1].Arguments -join '|') -ceq
            'relay|--loopback|--test-world-recovery=disconnect|--netz|schlecht')

    $bot = New-RoundFixture 'bot'
    $run = Invoke-Fixture $bot
    Assert-Test 'bot cohort avoids starting or evaluating a second game copy' (
        $run.Code -eq 0 -and $run.Steps -ceq 'prepare|relay|up A|host|scenario|down')
} finally {
    $env:KCDMP_RIG_RUN = $oldRun
    $env:KCDMP_RIG_RUNS = $oldRuns
    if (Test-Path -LiteralPath $script:scratch) { Remove-Item -LiteralPath $script:scratch -Recurse -Force }
}

Write-Host "Solo-Abend synthetic: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
