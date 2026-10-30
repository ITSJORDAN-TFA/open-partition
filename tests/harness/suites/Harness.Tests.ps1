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
