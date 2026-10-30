#Requires -Version 7.2
<#
.SYNOPSIS
    Builds and tests everything: Rust engine, .NET solution, harness self-tests.

.EXAMPLE
    ./build.ps1                      # Debug build + all fast tests
    ./build.ps1 -Configuration Release
    ./build.ps1 -Integration         # also run harness tests that create VHDX disks (admin + Hyper-V)
    ./build.ps1 -SkipTests
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $SkipTests,
    [switch] $Integration,
    # Folder for test VHDX disks (use a fast SSD); see tests/harness/Invoke-HarnessTests.ps1.
    [string] $WorkDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Invoke-Step {
    param([string] $Name, [scriptblock] $Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $Action
    if ($LASTEXITCODE) { throw "$Name failed (exit code $LASTEXITCODE)." }
    Write-Host ("    done in {0:N1}s" -f $watch.Elapsed.TotalSeconds)
}

$cargoProfile = @(if ($Configuration -eq 'Release') { '--release' })
$manifest = Join-Path $root 'src/engine/Cargo.toml'
$solution = Join-Path $root 'OpenPartition.slnx'

Invoke-Step 'Rust: build' { cargo build --workspace --manifest-path $manifest @cargoProfile }
Invoke-Step '.NET: build' { dotnet build $solution -c $Configuration --nologo -v minimal }

if (-not $SkipTests) {
    Invoke-Step 'Rust: clippy' { cargo clippy --workspace --all-targets --manifest-path $manifest @cargoProfile -- -D warnings }
    Invoke-Step 'Rust: test' { cargo test --workspace --manifest-path $manifest @cargoProfile }
    Invoke-Step '.NET: test' { dotnet test $solution -c $Configuration --no-build --nologo -v minimal }
    Invoke-Step 'Harness: self-tests' {
        $harnessArgs = @{}
        if ($Integration) { $harnessArgs.Integration = $true }
        if ($WorkDir) { $harnessArgs.WorkDir = $WorkDir }
        & (Join-Path $root 'tests/harness/Invoke-HarnessTests.ps1') @harnessArgs
    }
}

Write-Host 'Build succeeded.' -ForegroundColor Green
