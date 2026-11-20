# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
param([Parameter(Mandatory)][string] $Payload)

$ErrorActionPreference = 'Stop'
$payloadPath = (Resolve-Path -LiteralPath $Payload).Path
$root = Split-Path $PSScriptRoot -Parent
$nativeBuild = Join-Path $root 'native\build'
$project = Join-Path $PSScriptRoot 'EdgePayloadSmoke\EdgePayloadSmoke.csproj'
$temp = Join-Path ([IO.Path]::GetTempPath()) "kct-hand-reply-$([Guid]::NewGuid().ToString('N'))"
New-Item -Path $temp -ItemType Directory | Out-Null
try {
    $runArgs = @('run', '--project', $project, '--configuration', 'Release', "-p:EdgePayload=$payloadPath", '--',
        $payloadPath, (Join-Path $nativeBuild 'KCDMP\KCDMP.map'), (Join-Path $nativeBuild 'tests'), $temp)
    & dotnet @runArgs
    if ($LASTEXITCODE -ne 0) {
        Get-ChildItem -LiteralPath $temp -Filter 'kcdmp-native-edge-*.log' |
            Select-String -Pattern 'MAIN:|PIPE:|KCDMP.dll attached' | Select-Object -Last 20 -ExpandProperty Line
        throw "The published DLL and agent failed the 0x41 / 0xBB pipe smoke."
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
