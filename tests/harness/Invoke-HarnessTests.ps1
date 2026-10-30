#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the harness test suites (suites\*.Tests.ps1).

.DESCRIPTION
    Each suite file defines functions named Test-*. Every one is run in turn; a test passes
    if it returns without throwing, and is skipped if it calls Skip-Test. Tests that need an
    elevated Hyper-V host skip themselves elsewhere, so this script runs on any machine.

.PARAMETER Suite
    Wildcard on suite file names, e.g. 'Harness*'.

.PARAMETER Integration
    Also run tests that create and mount VHDX disks (slow; needs admin + Hyper-V).
#>
param(
    [string] $Suite = '*',
    [switch] $Integration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'OpenPartition.Harness.psm1') -Force
$env:OPM_HARNESS_INTEGRATION = if ($Integration) { '1' } else { '' }
$outDir = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$env:OPM_HARNESS_OUT = $outDir

$results = [System.Collections.Generic.List[object]]::new()
foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'suites') -Filter "$Suite.Tests.ps1" | Sort-Object Name) {
    $before = @(Get-ChildItem function:Test-* | ForEach-Object Name)
    . $file.FullName
    $tests = Get-ChildItem function:Test-* | Where-Object Name -notin $before | Sort-Object Name

    foreach ($test in $tests) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $status = 'Passed'
        $message = ''
        try {
            & $test.Name | Out-Null
        }
        catch {
            if ($_.Exception.GetType().Name -eq 'SkipTestException') {
                $status = 'Skipped'
            }
            else {
                $status = 'Failed'
            }
            $message = $_.Exception.Message
        }
        $watch.Stop()
        $results.Add([pscustomobject] @{
            Suite = $file.BaseName; Test = $test.Name; Status = $status
            Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2); Message = $message
        })
        $colour = @{ Passed = 'Green'; Skipped = 'Yellow'; Failed = 'Red' }[$status]
        Write-Host ("  [{0,-7}] {1}::{2}{3}" -f $status, $file.BaseName, $test.Name, $(if ($message) { " - $message" })) -ForegroundColor $colour
    }
    # Unload this suite's tests so names can repeat across suites.
    $tests | ForEach-Object { Remove-Item "function:$($_.Name)" }
}

$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $outDir 'results.json') -Encoding utf8
$failed = @($results | Where-Object Status -eq 'Failed').Count
$skipped = @($results | Where-Object Status -eq 'Skipped').Count
Write-Host ("Harness: {0} passed, {1} skipped, {2} failed" -f ($results.Count - $failed - $skipped), $skipped, $failed)
exit ([int]($failed -gt 0))
