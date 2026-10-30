# Real moves with opm-engine on throwaway VHDX disks: data, identity and file system must
# survive, and a move killed part way must recover. Only VHD/VHDX disks can be written by the
# engine (write guard), so these tests cannot touch real disks.

function Get-EnginePath {
    if ($env:OPM_ENGINE_EXE -and (Test-Path $env:OPM_ENGINE_EXE)) { return $env:OPM_ENGINE_EXE }
    $root = Join-Path $PSScriptRoot '..\..\..\src\engine\target'
    $exe = Get-ChildItem $root -Recurse -Filter opm-engine.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -in 'debug', 'release' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'opm-engine.exe not found; run cargo build first or set OPM_ENGINE_EXE.' }
    return $exe.FullName
}

function Find-PartitionByLabel {
    param([int] $DiskNumber, [string] $Label)
    foreach ($p in Get-Partition -DiskNumber $DiskNumber) {
        $v = $p | Get-Volume -ErrorAction SilentlyContinue
        if ($v -and $v.FileSystemLabel -eq $Label) { return [pscustomobject] @{ Partition = $p; Volume = $v } }
    }
    throw "No partition labelled '$Label' on disk $DiskNumber."
}

function Invoke-Engine {
    param([string[]] $Arguments)
    $output = & (Get-EnginePath) @Arguments 2>&1
    [pscustomobject] @{ ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
}

function Assert-MovedCorrectly {
    <#
        After a move (or recovery): partition at the target with its identity and type intact,
        other partitions untouched, every file byte-identical (checked on a read-only mount),
        and a clean file system scan.
    #>
    param($Path, $Manifest, $Before, [long] $Target, [string] $Style)

    $vhd = Get-VHD -Path $Path
    $partitions = @(Get-Partition -DiskNumber $vhd.DiskNumber)
    $moved = if ($Style -eq 'GPT') { $partitions | Where-Object Guid -eq $Before.Partition.Guid } else { $partitions | Where-Object Offset -eq $Target }
    if (-not $moved) { throw 'Moved partition not found.' }
    if ($moved.Offset -ne $Target) { throw "Partition is at $($moved.Offset), expected $Target." }
    if ($moved.Size -ne $Before.Partition.Size) { throw "Partition size changed: $($moved.Size)." }
    if ($Style -eq 'GPT' -and $moved.GptType -ne $Before.Partition.GptType) { throw "Partition type is $($moved.GptType), not restored." }
    if ($Style -eq 'MBR' -and $moved.MbrType -ne $Before.Partition.MbrType) { throw "MBR type is $($moved.MbrType), not restored." }
    foreach ($other in $Manifest.Partitions | Where-Object Offset -ne $Before.Partition.Offset) {
        if (-not ($partitions | Where-Object { $_.Offset -eq $other.Offset -and $_.Size -eq $other.Size })) {
            throw "Another partition (offset $($other.Offset)) moved or changed size."
        }
    }

    Dismount-VHD -Path $Path
    $vhd = Mount-VHD -Path $Path -ReadOnly -NoDriveLetter -Passthru
    $found = Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $Before.Volume.FileSystemLabel
    if ($Style -eq 'GPT' -and $found.Volume.Path -ne $Before.Volume.Path) {
        throw "Volume GUID path changed: $($found.Volume.Path) (was $($Before.Volume.Path))."
    }
    $data = $Manifest.Data | Where-Object Label -eq $Before.Volume.FileSystemLabel
    $problems = @(Test-DeterministicData -Root $found.Volume.Path -TargetBytes $data.Bytes -Seed $data.Seed)
    if ($problems.Count) { throw "File contents differ after the move: $($problems -join '; ')" }
    # File system check. chkdsk.exe can't be launched from every host, so use the storage
    # service's online scan (same engine as chkdsk /scan). It needs a read-write mount, so it
    # runs after the byte-for-byte check above, which used a read-only one.
    Dismount-VHD -Path $Path
    $vhd = Mount-VHD -Path $Path -NoDriveLetter -Passthru
    $found = Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $Before.Volume.FileSystemLabel
    $scan = Repair-Volume -Path $found.Volume.Path -Scan
    if ("$scan" -ne 'NoErrorsFound') { throw "File system scan reported: $scan" }
}

function Invoke-MoveScenario {
    param(
        [Parameter(Mandatory)] [string] $Layout,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [long] $TargetMB,
        # When set, the move is hard-killed after this many seconds and then recovered.
        [double] $KillAfterSeconds = 0,
        [string[]] $ExtraArguments = @()
    )
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $path = Join-Path $env:OPM_HARNESS_OUT "engine-$Layout-$id.vhdx"
    $journal = Join-Path $env:OPM_HARNESS_OUT "engine-$Layout-$id.journal"
    $style = (Get-TestLayout -Name $Layout).Style
    try {
        $manifest = New-TestDisk -Layout $Layout -Path $path -Seed 11
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        $before = Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $Label
        $target = $TargetMB * 1MB
        $arguments = @('move', '--disk', $vhd.DiskNumber, '--from', $before.Partition.Offset, '--to', $target, '--journal', $journal) + $ExtraArguments

        if ($KillAfterSeconds -gt 0) {
            $log = "$journal.log"
            # Retry the launch: antivirus scanning a freshly built exe can briefly deny access.
            $process = $null
            foreach ($attempt in 1..3) {
                try {
                    $process = Start-Process -FilePath (Get-EnginePath) -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $log
                    break
                }
                catch {
                    if ($attempt -eq 3) { throw "Could not start opm-engine: $($_.Exception.Message)" }
                    Start-Sleep -Seconds 2
                }
            }
            Start-Sleep -Milliseconds ([int]($KillAfterSeconds * 1000))
            if ($process.HasExited) { throw "Move finished before the kill at $KillAfterSeconds s; slow it down with --delay-ms." }
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
            $progress = @(Get-Content $log | Where-Object { $_ -like 'progress*' }).Count

            $recovery = Invoke-Engine @('recover', '--disk', $vhd.DiskNumber, '--journal', $journal)
            if ($recovery.ExitCode -ne 0) {
                throw "Recovery after a kill at $KillAfterSeconds s ($progress chunks done) failed: $($recovery.Output -join ' | ')"
            }
            if ($recovery.Output -match '^result=nothing-to-do') {
                # Killed before the journal was written (e.g. while hashing the source): the
                # disk must be untouched, and simply running the move again must work.
                $still = Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $Label
                if ($still.Partition.Offset -ne $before.Partition.Offset) { throw 'Nothing to recover, yet the partition moved.' }
                $retry = Invoke-Engine ($arguments | Where-Object { $_ -notin '--delay-ms', '150' })
                if ($retry.ExitCode -ne 0) { throw "Retry after an early kill failed: $($retry.Output | Where-Object { $_ -notlike 'progress*' })" }
            }
            elseif (-not ($recovery.Output -match '^result=done')) {
                throw "Unexpected recovery output: $($recovery.Output -join ' | ')"
            }
        }
        else {
            $result = Invoke-Engine $arguments
            if ($result.ExitCode -ne 0) { throw "Move failed: $($result.Output | Where-Object { $_ -notlike 'progress*' })" }
        }

        Assert-MovedCorrectly -Path $path -Manifest $manifest -Before $before -Target $target -Style $style
    }
    finally {
        Dismount-VHD -Path $path -ErrorAction SilentlyContinue
        Remove-Item $path, "$path.manifest.json", $journal, "$journal.log" -ErrorAction SilentlyContinue
    }
}

function Assert-EngineHost {
    if (-not $env:OPM_HARNESS_INTEGRATION) { Skip-Test 'integration tests not requested (-Integration)' }
    if (-not (Test-HarnessHostReady)) { Skip-Test 'needs an elevated session with the Hyper-V module' }
}

function Test-EngineRefusesRealDisks {
    # Every non-virtual disk on this machine must be refused before anything is opened for writing.
    $real = @(Get-Disk | Where-Object { $_.BusType -ne 'File Backed Virtual' })
    if (-not $real.Count) { Skip-Test 'no physical disks on this machine' }
    foreach ($disk in $real) {
        $result = Invoke-Engine @('info', '--disk', $disk.Number)
        if ($result.ExitCode -eq 0 -or -not ($result.Output -match 'refused: disk \d+ is not a VHD/VHDX')) {
            throw "Disk $($disk.Number) ($($disk.FriendlyName)) was not refused: $($result.Output -join ' | ')"
        }
    }
}

function Test-MoveGptRight {
    Assert-EngineHost
    Invoke-MoveScenario -Layout 'gpt-512n-single' -Label 'DATA1' -TargetMB 144
}

function Test-MoveGptLeftOverlapping {
    Assert-EngineHost
    # 273 MiB -> 16 MiB: shift 257 MiB, partition 512 MiB, so source and target overlap.
    Invoke-MoveScenario -Layout 'gpt-512n-free-left' -Label 'SHIFTME' -TargetMB 16
}

function Test-MoveBoxedInRightmostPartition {
    Assert-EngineHost
    # First step of the "make room" plan: move the rightmost partition into the free space.
    Invoke-MoveScenario -Layout 'gpt-512e-boxed-in' -Label 'RIGHT' -TargetMB 1600
}

function Test-MoveOn4KnDisk {
    Assert-EngineHost
    Invoke-MoveScenario -Layout 'gpt-4kn-three' -Label 'C' -TargetMB 1200
}

function Test-MoveTightFit {
    Assert-EngineHost
    # Uses 48 of the ~49 MiB free: the shift is small, so the chunks are small and overlapping.
    Invoke-MoveScenario -Layout 'gpt-512n-tight' -Label 'FULL' -TargetMB 64
}

function Test-MoveMbr {
    Assert-EngineHost
    Invoke-MoveScenario -Layout 'mbr-512n-single' -Label 'DATA1' -TargetMB 129
}

function Test-KilledMovesRecover {
    Assert-EngineHost
    # Small chunks plus a delay so the kill lands mid-copy. Random kill times; the seed is
    # printed in the failure message so a failure can be replayed.
    $seed = [System.Environment]::TickCount
    $random = [System.Random]::new($seed)
    $scenarios = @(
        @{ Layout = 'gpt-512n-single'; Label = 'DATA1'; TargetMB = 144 }
        @{ Layout = 'gpt-512n-free-left'; Label = 'SHIFTME'; TargetMB = 16 }
        @{ Layout = 'mbr-512n-single'; Label = 'DATA1'; TargetMB = 129 }
    )
    foreach ($s in $scenarios) {
        foreach ($round in 1..2) {
            $kill = 0.5 + $random.NextDouble() * 3
            try {
                Invoke-MoveScenario @s -KillAfterSeconds $kill -ExtraArguments @('--chunk', 16MB, '--delay-ms', 150)
            }
            catch {
                throw "$($s.Layout) round $round (seed $seed, kill at $([Math]::Round($kill, 2)) s): $($_.Exception.Message)"
            }
        }
    }
}
