# Self-tests for the harness itself.

function Test-LayoutCatalogueIsValid {
    $layouts = @(Get-TestLayout)
    if ($layouts.Count -eq 0) { throw 'Layout catalogue is empty.' }
    foreach ($layout in $layouts) {
        $problems = @(Test-LayoutDefinition $layout)
        if ($problems.Count) { throw "$($layout.Name): $($problems -join '; ')" }
    }
}

function Test-CatalogueCoversRequiredFormats {
    $layouts = @(Get-TestLayout)
    foreach ($style in 'GPT', 'MBR') {
        if (-not ($layouts | Where-Object Style -eq $style)) { throw "No $style layout in the catalogue." }
    }
    foreach ($format in '512n', '512e', '4Kn') {
        if (-not ($layouts | Where-Object SectorFormat -eq $format)) { throw "No $format layout in the catalogue." }
    }
}

function Test-LayoutValidatorRejectsOverlap {
    $bad = [pscustomobject] @{
        Style = 'GPT'; SectorFormat = '512n'; DiskSizeMB = 100
        Partitions = @(@{ SizeMB = 50; FillPercent = 0 }, @{ OffsetMB = 20; SizeMB = 10; FillPercent = 0 })
    }
    if (-not @(Test-LayoutDefinition $bad).Count) { throw 'Overlapping partitions were not reported.' }
}

function Test-CreatedDiskHashesAreStable {
    if (-not $env:OPM_HARNESS_INTEGRATION) { Skip-Test 'integration tests not requested (-Integration)' }
    if (-not (Test-HarnessHostReady)) { Skip-Test 'needs an elevated session with the Hyper-V module' }

    $path = Join-Path $env:OPM_HARNESS_OUT "selftest-$([guid]::NewGuid()).vhdx"
    try {
        $manifest = New-TestDisk -Layout 'gpt-512n-single' -Path $path -Seed 42
        $vhd = Mount-VHD -Path $path -ReadOnly -NoDriveLetter -Passthru
        try {
            $again = Get-TestDiskManifest -DiskNumber $vhd.DiskNumber
        }
        finally {
            Dismount-VHD -Path $path
        }
        $first = $manifest.Partitions | ForEach-Object { $_.Sha256 }
        $second = $again.Partitions | ForEach-Object { $_.Sha256 }
        if (Compare-Object $first $second) { throw 'Partition hashes changed between two read-only mounts.' }
    }
    finally {
        Remove-Item $path, "$path.manifest.json" -ErrorAction SilentlyContinue
    }
}

function Test-CatalogueHasRecoveryAfterC {
    $layout = Get-TestLayout -Name 'gpt-512e-recovery-after-c'
    if (-not $layout) { throw 'Layout gpt-512e-recovery-after-c is missing.' }
    $kinds = @($layout.Partitions | ForEach-Object { if ($_.ContainsKey('Kind')) { $_.Kind } else { 'Primary' } })
    if ($kinds[-1] -ne 'Recovery' -or $kinds[0] -ne 'Primary') { throw "Expected OS then Recovery, got $($kinds -join ', ')." }
}

function Test-LayoutValidatorChecksKinds {
    $recovery = [pscustomobject] @{
        Style = 'GPT'; SectorFormat = '512n'; DiskSizeMB = 100
        Partitions = @(@{ SizeMB = 40; FillPercent = 0 }, @{ Kind = 'Recovery'; SizeMB = 20; FillPercent = 0 })
    }
    if (@(Test-LayoutDefinition $recovery).Count) { throw "A GPT recovery partition was rejected: $(Test-LayoutDefinition $recovery)" }
    $unknown = [pscustomobject] @{
        Style = 'GPT'; SectorFormat = '512n'; DiskSizeMB = 100
        Partitions = @(@{ Kind = 'Swap'; SizeMB = 20; FillPercent = 0 })
    }
    if (-not @(Test-LayoutDefinition $unknown).Count) { throw 'An unknown Kind was not reported.' }
}

function Test-WinReVmScriptParses {
    # The VM test for WinRE is run by hand; make sure it at least parses.
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\Test-WinReAfterMove.ps1'), [ref]$null, [ref]$errors) | Out-Null
    if ($errors) { throw "Test-WinReAfterMove.ps1: $($errors | ForEach-Object Message)" }
}
