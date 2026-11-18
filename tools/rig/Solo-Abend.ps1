# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS).
# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param(
    [switch]$TestSoloAbend,
    [switch]$NurPruefen,
    [ValidateSet('normal','schlecht')][string]$Netz = 'normal',
    [string]$Python = 'python',
    [string]$Dotnet = 'dotnet',
    [string]$Install = $env:KCD2MP_INSTALL,
    [string]$BuildDirectory = $env:KCDMP_RIG_BUILD,
    [string]$ModTemplate = $env:KCDMP_RIG_MOD_TEMPLATE,
    [string]$Injector = $env:KCDMP_RIG_INJECTOR,
    [string]$HostSave,
    [string]$JoinerSave,
    [string]$RouteMeasure,
    [string]$RouteWalk,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$Bandit,
    [string]$Ausgabe,
    [switch]$Unbegleitet
)

function Assert-SoloAbendOptIn {
    param([string]$Variable, [bool]$Schalter)
    if ($Variable -cne '1' -or -not $Schalter) {
        throw 'Der Solo-Abend braucht KCDMP_TEST_SOLO_ABEND=1 und -TestSoloAbend.'
    }
}

function Test-SoloAbendHardware {
    param([long]$InstalledRamBytes, [object[]]$Adapters)
    if ($InstalledRamBytes -ne 32GB) { return $false }
    foreach ($adapter in $Adapters) {
        if ($adapter.name -match '^(NVIDIA\s+GeForce\s+)?RTX\s+4080\s+SUPER$' -and
            -not $adapter.software -and $adapter.dedicated_mb -ge (15 * 1024)) { return $true }
    }
    return $false
}

function Invoke-SoloAbendRound {
    param(
        [Parameter(Mandatory)][ValidateSet('pair','reload','weatherload','disconnect','bot')][string]$Cohort,
        [Parameter(Mandatory)][ValidateSet('normal','schlecht')][string]$Netz,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$RunId,
        [Parameter(Mandatory)][string]$RunsRoot,
        [ValidateRange(1,10)][int]$Playline = 4,
        [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Save = 'save440',
        [Parameter(Mandatory)][string]$Report,
        [Parameter(Mandatory)][scriptblock]$RunPython,
        [string[]]$ScenarioArguments = @()
    )
    $dir = Join-Path $RunsRoot $RunId
    if (Test-Path -LiteralPath $dir) {
        Write-Warning "Der Ausgabeordner für $RunId existiert bereits."
        return 2
    }
    $priorRun = $env:KCDMP_RIG_RUN
    $priorRuns = $env:KCDMP_RIG_RUNS
    $env:KCDMP_RIG_RUN = $RunId
    $env:KCDMP_RIG_RUNS = $RunsRoot
    $rc = 0
    $errors = [System.Collections.Generic.List[string]]::new()
    try {
        $phases = @(
            ,@('prepare')
        )
        if ($Cohort -eq 'disconnect') {
            $phases += ,@('relay','--loopback','--test-world-recovery=disconnect','--netz',$Netz)
        } else {
            $phases += ,@('relay','--loopback','--netz',$Netz)
        }
        $phases += ,@('up','A')
        if ($Cohort -ne 'bot') { $phases += ,@('up','B','--manual-confirm') }
        $phases += ,@('host','A',[string]$Playline,$Save)
        if ($Cohort -ne 'bot') { $phases += ,@('join','B') }

        foreach ($phase in $phases) {
            if ($phase[0] -eq 'up' -and $phase[1] -eq 'B') {
                Write-Host "Bitte im zweiten Fenster ‚Ja‘ klicken"
            }
            $phaseCode = & $RunPython 'rig/pair.py' $phase $null
            if ($phaseCode -ne 0) {
                $rc = 2
                $errors.Add(('Startschritt {0} fehlgeschlagen (Exit {1}).' -f ($phase -join ' '), $phaseCode))
                break
            }
        }

        if ($rc -eq 0) {
            try {
                $scenario = @('abend','--test-solo-abend','--cohort',$Cohort,'--netz',$Netz,'--output',$Report,
                    '--playline',[string]$Playline,'--save',$Save) + $ScenarioArguments
                $scenarioCode = & $RunPython 'rig/scenarios.py' $scenario $null
                if ($scenarioCode -ne 0) {
                    $rc = $scenarioCode
                    $errors.Add(('Szenario {0} endet mit Exit {1}.' -f $Cohort, $scenarioCode))
                }
            } catch {
                $rc = 2
                $errors.Add($_.Exception.Message)
            }

            if ($Cohort -ne 'bot') {
                try {
                    $collectCode = & $RunPython 'rig/pair.py' @('collect','abend','--write-verdict') $null
                    $hostPath = Join-Path $dir 'bundles/abend-A.packed'
                    $joinerPath = Join-Path $dir 'bundles/abend-B.packed'
                    if ($collectCode -ne 0 -or
                        -not (Test-Path -LiteralPath (Join-Path $hostPath 'ERGEBNIS.txt') -PathType Leaf) -or
                        -not (Test-Path -LiteralPath (Join-Path $joinerPath 'ERGEBNIS.txt') -PathType Leaf)) {
                        throw 'Collect Logs hat nicht beide T1-Berichte für diesen Lauf geschrieben.'
                    }
                    $verdictCode = & $RunPython 'verdict/pair.py' @($hostPath,$joinerPath) $dir
                    if ($verdictCode -eq 2 -or
                        -not (Test-Path -LiteralPath (Join-Path $dir 'ERGEBNIS-PAAR.md') -PathType Leaf)) {
                        throw 'Die Paar-Auswertung hat keinen Bericht für diesen Lauf geschrieben.'
                    }
                    if ($verdictCode -ne 0) {
                        $rc = [Math]::Max($rc, [int]$verdictCode)
                        $errors.Add('Die Paar-Ampel meldet offene oder fehlgeschlagene Messungen.')
                    }
                } catch {
                    $rc = 2
                    $errors.Add($_.Exception.Message)
                }
            }
        }
    } catch {
        $rc = 2
        $errors.Add($_.Exception.Message)
    } finally {
        try {
            $down = & $RunPython 'rig/pair.py' @('down','abend') $null
            if ($down -ne 0) {
                $rc = 3
                $errors.Add(('Aufräumen oder Prüfung des Spielbestands fehlgeschlagen (Exit {0}).' -f $down))
            }
        } catch {
            $rc = 3
            $errors.Add($_.Exception.Message)
        } finally {
            $env:KCDMP_RIG_RUN = $priorRun
            $env:KCDMP_RIG_RUNS = $priorRuns
        }
    }

    foreach ($problem in $errors) { Write-Warning $problem }
    return [int]$rc
}

function Invoke-SoloAbend {
    $ErrorActionPreference = 'Stop'
    Assert-SoloAbendOptIn -Variable $env:KCDMP_TEST_SOLO_ABEND -Schalter $TestSoloAbend.IsPresent
    if ($env:OS -ne 'Windows_NT') { throw 'Der Solo-Abend braucht Windows und die KCD2 Modding Tools.' }
    if (-not $Install) { $Install = 'D:\SteamLibrary\steamapps\common\KCD2Mod' }
    $Install = (Resolve-Path -LiteralPath $Install).ProviderPath
    if (-not (Test-Path -LiteralPath $Install -PathType Container)) { throw 'Die Modding-Tools-Installation fehlt.' }
    $tools = Split-Path -Parent $PSScriptRoot
    $repo = Split-Path -Parent $tools
    $pythonExe = (Get-Command $Python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $machineJson = & $pythonExe (Join-Path $PSScriptRoot 'machine.py') --disk ([IO.Path]::GetPathRoot($Install)) --json
    if ($LASTEXITCODE -ne 0) { throw 'Die Abfrage des Testrechners ist fehlgeschlagen.' }
    $machine = ($machineJson -join [Environment]::NewLine) | ConvertFrom-Json
    $ram = (Get-CimInstance Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum
    Write-Host ('Installierter RAM {0:N1} GiB. Frei gemeldeter RAM {1:N1} GiB. Freier Datenträger {2:N1} GiB.' -f
        ($ram / 1GB), ($machine.memory.ram_free_mb / 1024), $machine.disk.free_gb)
    foreach ($card in $machine.adapters) {
        Write-Host ('Grafik {0}, {1} MB dediziert' -f $card.name, $card.dedicated_mb)
    }
    if (-not (Test-SoloAbendHardware -InstalledRamBytes $ram -Adapters $machine.adapters)) {
        throw 'Für diesen Lauf sind 32 GiB installierter RAM und eine RTX 4080 SUPER vorgesehen.'
    }
    Write-Host ('Monitore {0}. Freier Grafikspeicher {1} MB.' -f $machine.monitors, $machine.vram.free_mb)
    if ($NurPruefen) { return 0 }

    if (-not $HostSave -or -not $JoinerSave) { throw 'Gib je einen lokalen Wegwerfstand mit -HostSave und -JoinerSave an.' }
    $HostSave = (Resolve-Path -LiteralPath $HostSave).ProviderPath
    $JoinerSave = (Resolve-Path -LiteralPath $JoinerSave).ProviderPath
    foreach ($file in @($HostSave, $JoinerSave)) {
        if ([IO.Path]::GetExtension($file) -ine '.whs' -or
            -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Die Wegwerfstände müssen lokale .whs-Dateien sein.' }
    }
    if ($HostSave -ieq $JoinerSave) { throw 'Verwende zwei verschiedene lokale Wegwerfstände.' }
    if (-not $Ausgabe) { $Ausgabe = Join-Path $repo 'research/WO-206/runs' }
    $Ausgabe = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Ausgabe)
    $base = 'abend-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0,8))
    $runs = Join-Path $Ausgabe $base
    $null = New-Item -ItemType Directory -Path $runs
    $machine | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runs 'maschine.json') -Encoding UTF8
    $report = Join-Path $runs 'abend.json'
    $scenarioArgs = @()
    if ($RouteMeasure) { $scenarioArgs += @('--route-measure',(Resolve-Path -LiteralPath $RouteMeasure).ProviderPath) }
    if ($RouteWalk) { $scenarioArgs += @('--route-walk',(Resolve-Path -LiteralPath $RouteWalk).ProviderPath) }
    if ($Bandit) { $scenarioArgs += @('--bandit',$Bandit) }
    if ($Unbegleitet) { $scenarioArgs += '--unattended' }

    $envMap = @{
        KCD2MP_INSTALL = $Install
        KCDMP_RIG_SOLO = '1'
        KCDMP_RIG_SOLO_HOST_SAVE = $HostSave
        KCDMP_RIG_SOLO_JOINER_SAVE = $JoinerSave
        KCDMP_RIG_HOST = 'A'
        KCDMP_RIG_LOW = 'A,B'
        KCDMP_RIG_TALK_HOME = 'here'
        KCDMP_RIG_TEST_WORLD_RECOVERY = '1'
        KCDMP_TEST_WORLD_RECOVERY = '1'
        KCDMP_TEST_OWNER_HANDOVER = '1'
        KCDMP_TEST_LANES = '1'
        KCDMP_TEST_RP_MEASURE = '1'
        KCDMP_TEST_TIMELINE = '1'
        KCDMP_TEST_RP_CONTROLLER = '1'
        KCDMP_TEST_RP_COMBAT = '1'
        KCDMP_TEST_LOAD_SEQUENCE = '1'
        KCDMP_TEST_BASELINE_EVENTS = '1'
        KCDMP_TEST_BOT = '1'
        KCDMP_RIG_TEST_ENV = 'KCDMP_TEST_W206=1;KCDMP_TEST_W221=1;KCDMP_TEST_LANES=1;KCDMP_TEST_RP_MEASURE=1;KCDMP_TEST_WORLD_RECOVERY=1;KCDMP_TEST_LOAD_SEQUENCE=1;KCDMP_TEST_BASELINE_EVENTS=1'
        PYTHONDONTWRITEBYTECODE = '1'
    }
    if ($BuildDirectory) { $envMap['KCDMP_RIG_BUILD'] = (Resolve-Path -LiteralPath $BuildDirectory).ProviderPath }
    if ($ModTemplate) { $envMap['KCDMP_RIG_MOD_TEMPLATE'] = (Resolve-Path -LiteralPath $ModTemplate).ProviderPath }
    if ($Injector) { $envMap['KCDMP_RIG_INJECTOR'] = (Resolve-Path -LiteralPath $Injector).ProviderPath }
    $areas = @($machine.monitor_work_areas)
    if ($areas.Count -gt 0) {
        $b = $areas[0]
        foreach ($area in $areas) { if (-not $area[4]) { $b = $area; break } }
        if ($b.Count -ge 4) {
            $envMap['KCDMP_RIG_B_X'] = [string][Math]::Max($b[0], $b[2] - 1296)
            $envMap['KCDMP_RIG_B_Y'] = [string][Math]::Max($b[1], $b[3] - 780)
        }
    }
    $before = @{}
    foreach ($key in $envMap.Keys) {
        $before[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $envMap[$key], 'Process')
    }

    $runPython = {
        param([string]$Program, [string[]]$Arguments, [string]$Directory)
        if ($Directory) { Push-Location -LiteralPath $Directory }
        try {
            & $pythonExe -X utf8 (Join-Path $tools $Program) @Arguments | Out-Host
            return [int]$LASTEXITCODE
        } finally {
            if ($Directory) { Pop-Location }
        }
    }
    $rc = 0
    try {
        foreach ($cohort in @('pair','reload','weatherload','disconnect','bot')) {
            $parameters = @{
                Cohort = $cohort
                Netz = $Netz
                RunId = "$base-$cohort"
                RunsRoot = $runs
                Report = $report
                RunPython = $runPython
                ScenarioArguments = $scenarioArgs
            }
            $code = Invoke-SoloAbendRound @parameters
            $rc = [Math]::Max($rc, $code)
            if ($code -eq 3) { break }
        }
        $pairReport = Join-Path $runs "$base-pair/ERGEBNIS-PAAR.md"
        $code = & $runPython 'rig/scenarios.py' (@('abend','--test-solo-abend','--cohort','summary','--netz',$Netz,
            '--output',$report,'--pair-verdict',$pairReport,'--run-replay','--dotnet',$Dotnet) + $scenarioArgs) $repo
        $rc = [Math]::Max($rc, $code)
    } finally {
        foreach ($key in $before.Keys) { [Environment]::SetEnvironmentVariable($key, $before[$key], 'Process') }
    }
    Write-Host ('Berichte und Bundles stehen unter {0}' -f $runs)
    return $rc
}

if ($MyInvocation.InvocationName -ne '.') {
    try { exit (Invoke-SoloAbend) }
    catch { Write-Error $_.Exception.Message; exit 2 }
}
