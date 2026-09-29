#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Smoke-tests a built JJKCC-Unpacker.exe. Runs in CI and locally.

.DESCRIPTION
    Checks that the exe exists, is a sane size, that `--help` succeeds, and that a run with a
    bad input path fails cleanly with exit code 1 (without touching anything).

.EXAMPLE
    pwsh tools/smoke-test.ps1
    pwsh tools/smoke-test.ps1 -Path dist\JJKCC-Unpacker.exe
#>
[CmdletBinding()]
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'

if (-not $Path) {
    # $PSScriptRoot is not populated while parameter defaults are evaluated (Windows PowerShell 5.1)
    $here = $PSScriptRoot
    if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $Path = Join-Path (Split-Path -Parent $here) 'dist\JJKCC-Unpacker.exe'
}

$exe = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
if (-not $exe) { throw "exe not found: $Path" }
$exe = $exe.Path

$size = (Get-Item -LiteralPath $exe).Length
Write-Host "built $exe ($size bytes)"
if ($size -lt 40000) { throw "exe looks too small ($size bytes)" }

# `--help` must succeed — proves the binary starts and the CLI parses.
$help = Start-Process -FilePath $exe -ArgumentList '--help' -Wait -PassThru -NoNewWindow
if ($help.ExitCode -ne 0) { throw "--help exited with $($help.ExitCode)" }
Write-Host 'ok: --help exit=0'

# A bad input path must fail cleanly with exit code 1 and change nothing.
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('jjkcc-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    $bad = Start-Process -FilePath $exe -Wait -PassThru -NoNewWindow -ArgumentList @(
        '--cli', '--quiet', '--input', (Join-Path $scratch 'does-not-exist'),
        '--output', (Join-Path $scratch 'out'))
    if ($bad.ExitCode -ne 1) { throw "bad-input run exited with $($bad.ExitCode), expected 1" }
    if (Test-Path (Join-Path $scratch 'out')) { throw 'the failed run created an output folder' }
    Write-Host 'ok: bad input exit=1'
}
finally {
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}

Write-Host 'SMOKE TEST PASSED'
