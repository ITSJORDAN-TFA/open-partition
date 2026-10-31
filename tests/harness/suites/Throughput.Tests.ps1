# A timed, verified move on a disposable VHDX. No process interruption is used.
function Test-ThroughputEstimate {
    if ($env:OPM_HARNESS_INTEGRATION -ne '1') { Skip-Test 'requires -Integration, elevation and Hyper-V' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Skip-Test 'requires elevation' }
    if (-not (Get-Command New-VHD -ErrorAction SilentlyContinue)) { Skip-Test 'requires Hyper-V' }
    $engine = Join-Path $PSScriptRoot '../../../src/engine/target/debug/opm-engine.exe'
    $path = Join-Path $env:OPM_HARNESS_OUT ('throughput-' + [guid]::NewGuid().ToString('N') + '.vhdx')
    $journal = "$path.journal"
    try {
        $manifest = New-TestDisk -Layout 'gpt-512n-single' -Path $path -Seed 11
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        $part = Get-Partition -DiskNumber $vhd.DiskNumber | Where-Object { ($_.GetType()) -and $_.Size -gt 128MB } | Select-Object -Last 1
        $output = & $engine bench --disk $vhd.DiskNumber --offset $part.Offset --length $part.Size
        if ($LASTEXITCODE -ne 0) { throw "bench failed: $output" }
        $match = [regex]::Match(($output -join ' '), 'bytes_per_second=(\d+)')
        if (-not $match.Success) { throw "No benchmark rate: $output" }
        # Same four transfers and fixed overhead as Throughput.EstimateMove (unit tested).
        $estimate = 8.0 + 4.0 * $part.Size / [double]$match.Groups[1].Value
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $output = & $engine move --disk $vhd.DiskNumber --from $part.Offset --to (144MB) --journal $journal
        $watch.Stop()
        if ($LASTEXITCODE -ne 0) { throw "move failed: $output" }
        $actual = $watch.Elapsed.TotalSeconds
        Write-Host "Throughput estimate=$estimate seconds actual=$actual seconds ratio=$($estimate / $actual)"
        if ($estimate -gt 2 * $actual -or $actual -gt 2 * $estimate) { throw "Estimate outside 2x: estimate=$estimate actual=$actual" }
        $moved = Get-Partition -DiskNumber $vhd.DiskNumber | Where-Object Offset -eq (144MB)
        if (-not $moved) { throw 'Moved partition missing' }
        foreach ($data in $manifest.Data) {
            $volume = Get-Partition -DiskNumber $vhd.DiskNumber | Get-Volume | Where-Object FileSystemLabel -eq $data.Label
            $problems = @(Test-DeterministicData -Root $volume.Path -TargetBytes $data.Bytes -Seed $data.Seed)
            if ($problems.Count) { throw ($problems -join '; ') }
        }
    }
    finally {
        Dismount-VHD -Path $path -ErrorAction SilentlyContinue
        # Keep artifacts as evidence; no other worktree files are touched.
    }
}
