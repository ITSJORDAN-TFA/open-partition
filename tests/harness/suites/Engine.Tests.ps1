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
    # GPT volumes keep their GUID through the partition GUID; MBR ones because the engine
    # rewrites MountedDevices (plan gap G1).
    if ($found.Volume.Path -ne $Before.Volume.Path) {
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

function Get-FreeDriveLetter {
    $used = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object Name) + @(Get-Volume | Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter })
    $free = [char[]]('PQRSTUVWXYZ'.ToCharArray()) | Where-Object { [string]$_ -notin $used }
    if (-not $free) { throw 'No free drive letter for the test.' }
    return [string]$free[0]
}

function Invoke-ExtendedPartitionScenario {
    <#
        The Azure field case: SYS is followed by a primary and an extended partition with three
        logical drives. Move the extended partition (with its logical drives) and then the
        primary to the end of the disk, extend SYS with Windows' own Resize-Partition, and
        check every volume kept its files, volume GUID and (with -DriveLetters) drive letter.
    #>
    param([double] $KillAfterSeconds = 0, [switch] $DriveLetters)
    $layout = 'mbr-512n-logical-boxed-in'
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $path = Join-Path $env:OPM_HARNESS_OUT "engine-$layout-$id.vhdx"
    $journal = Join-Path $env:OPM_HARNESS_OUT "engine-$layout-$id.journal"
    $shift = 256MB
    try {
        $manifest = New-TestDisk -Layout $layout -Path $path -Seed 13
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        $disk = $vhd.DiskNumber
        $letters = @{}
        foreach ($label in @(if ($DriveLetters) { 'PRIM', 'LOG2' })) {
            $letter = Get-FreeDriveLetter
            (Find-PartitionByLabel -DiskNumber $disk -Label $label).Partition | Set-Partition -NewDriveLetter $letter
            $letters[$label] = $letter
        }
        $labels = @($manifest.Data | ForEach-Object Label)
        $paths = @{}
        foreach ($label in $labels) { $paths[$label] = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Volume.Path }
        $extended = Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended'
        $primary = (Find-PartitionByLabel -DiskNumber $disk -Label 'PRIM').Partition
        $sys = (Find-PartitionByLabel -DiskNumber $disk -Label 'SYS').Partition

        # Rightmost first, so each target is free.
        $moves = @(
            @{ From = $extended.Offset; To = $extended.Offset + $shift; Size = $extended.Size; Name = 'extended partition' }
            @{ From = $primary.Offset; To = $primary.Offset + $shift; Size = $primary.Size; Name = 'PRIM' }
        )
        foreach ($m in $moves) {
            $arguments = @('move', '--disk', $disk, '--from', $m.From, '--to', $m.To, '--journal', $journal)
            if ($KillAfterSeconds -gt 0 -and $m.Name -eq 'extended partition') {
                $log = "$journal.log"
                $process = Start-Process -FilePath (Get-EnginePath) -ArgumentList ($arguments + @('--chunk', 16MB, '--delay-ms', 150)) -PassThru -NoNewWindow -RedirectStandardOutput $log
                while (-not $process.HasExited -and -not @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -like 'progress*' }).Count) { Start-Sleep -Milliseconds 100 }
                Start-Sleep -Milliseconds ([int]($KillAfterSeconds * 1000))
                if ($process.HasExited) { throw "Move finished before the kill: $(Get-Content $log -Raw)" }
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit()
                if (Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended') { throw 'Extended partition still in the table while its data was being moved.' }
                $result = Invoke-Engine @('recover', '--disk', $disk, '--journal', $journal)
                if ($result.ExitCode -ne 0 -or -not ($result.Output -match '^result=done')) { throw "Recovery failed: $($result.Output -join ' | ')" }
            }
            else {
                $result = Invoke-Engine $arguments
                if ($result.ExitCode -ne 0) { throw "Moving the $($m.Name) failed: $($result.Output | Where-Object { $_ -notlike 'progress*' })" }
            }
            $now = Get-Partition -DiskNumber $disk | Where-Object { $_.Offset -eq $m.To -and $_.Size -eq $m.Size }
            if (-not $now) { throw "The $($m.Name) is not at $($m.To) after the move." }
        }

        # The freed space is now right after SYS: extend it with Windows' own tooling.
        $sys = Get-Partition -DiskNumber $disk | Where-Object Offset -eq $sys.Offset
        $max = (Get-PartitionSupportedSize -DiskNumber $disk -PartitionNumber $sys.PartitionNumber).SizeMax
        if ($max -lt $sys.Size + $shift - 1MB) { throw "SYS can only grow to $max bytes; the space after it was not freed." }
        $sys | Resize-Partition -Size $max

        foreach ($label in $letters.Keys) {
            $p = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Partition
            if ([string]$p.DriveLetter -ne $letters[$label]) { throw "$label lost drive letter $($letters[$label]): now '$($p.DriveLetter)'." }
        }
        foreach ($label in $labels) {
            $now = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Volume.Path
            if ($now -ne $paths[$label]) { throw "$label volume GUID changed: $now (was $($paths[$label]))." }
        }

        Dismount-VHD -Path $path
        $vhd = Mount-VHD -Path $path -ReadOnly -NoDriveLetter -Passthru
        foreach ($data in $manifest.Data) {
            $found = Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $data.Label
            $problems = @(Test-DeterministicData -Root $found.Volume.Path -TargetBytes $data.Bytes -Seed $data.Seed)
            if ($problems.Count) { throw "$($data.Label): $($problems -join '; ')" }
        }
        Dismount-VHD -Path $path
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        foreach ($label in $labels) {
            $volume = (Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $label).Volume
            try { $scan = Repair-Volume -Path $volume.Path -Scan }
            catch { throw "$label ($([Math]::Round($volume.SizeRemaining / 1MB)) MiB free of $([Math]::Round($volume.Size / 1MB))) file system scan failed: $($_.Exception.Message.Split("`n")[0])" }
            if ("$scan" -ne 'NoErrorsFound') { throw "$label file system scan reported: $scan" }
        }
    }
    finally {
        Dismount-VHD -Path $path -ErrorAction SilentlyContinue
        Remove-Item $path, "$path.manifest.json", $journal, "$journal.log" -ErrorAction SilentlyContinue
    }
}

function Test-MoveExtendedPartitionAndExtendSys {
    Assert-EngineHost
    Invoke-ExtendedPartitionScenario
}

function Test-KilledExtendedPartitionMoveRecovers {
    Assert-EngineHost
    Invoke-ExtendedPartitionScenario -KillAfterSeconds 1
}

function Test-DriveLettersSurviveMbrMoves {
    Assert-EngineHost
    # The Remote Desktop client redirects every new drive letter into its sessions and keeps it
    # open, so the engine (correctly) can't lock the volume.
    if (Get-Process msrdc, mstsc -ErrorAction SilentlyContinue) {
        Skip-Test 'a Remote Desktop client is running and holds new drive letters open; close it to run this test'
    }
    Invoke-ExtendedPartitionScenario -DriveLetters
}

function Test-FailedVerificationRollsBack {
    <#
        The Azure field case of 2026-10-31: an extended partition moved far right (source and
        target don't overlap) whose finished copy then fails the final hash check. Recovery must
        refuse to reveal it, compare must point at the bad sector, and rollback must put every
        logical drive back exactly as it was.
    #>
    Assert-EngineHost
    $layout = 'mbr-512n-logical-boxed-in'
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $path = Join-Path $env:OPM_HARNESS_OUT "engine-rollback-$id.vhdx"
    $journal = Join-Path $env:OPM_HARNESS_OUT "engine-rollback-$id.journal"
    try {
        $manifest = New-TestDisk -Layout $layout -Path $path -Seed 17
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        $disk = $vhd.DiskNumber
        $labels = @($manifest.Data | ForEach-Object Label)
        $paths = @{}
        foreach ($label in $labels) { $paths[$label] = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Volume.Path }
        $before = @(Get-Partition -DiskNumber $disk | Sort-Object Offset | ForEach-Object { "$($_.Offset)/$($_.Size)/$($_.Type)" })
        $extended = Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended'
        $target = 640MB
        if ($target -lt $extended.Offset + $extended.Size) { throw 'Test layout: target overlaps the source.' }

        # Kill the move once the first chunk (the last 16 MiB of the partition) is copied.
        $log = "$journal.log"
        $arguments = @('move', '--disk', $disk, '--from', $extended.Offset, '--to', $target, '--journal', $journal, '--chunk', 16MB, '--delay-ms', 300, '--full-copy')
        $process = Start-Process -FilePath (Get-EnginePath) -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $log
        while (-not $process.HasExited -and -not @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -like 'progress stage=copying*' }).Count) { Start-Sleep -Milliseconds 50 }
        if ($process.HasExited) { throw "Move finished before the kill: $(Get-Content $log -Raw)" }
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()

        # Damage one sector of the copied part, in unpartitioned space.
        $bad = $target + $extended.Size - 8MB
        # Raw write: only to the test VHDX mounted above, never to whatever disk now has its number.
        $raw = Get-Disk -Number $disk
        if ($raw.BusType -ne 'File Backed Virtual' -or $raw.Location -ne $path) { throw "Disk $disk is not the test VHDX $path; refusing the raw write." }
        $stream = [IO.FileStream]::new("\\.\PhysicalDrive$disk", 'Open', 'ReadWrite', 'ReadWrite')
        try {
            $stream.Position = $bad
            $junk = [byte[]]::new(512); [Array]::Fill($junk, [byte]0xEE)
            $stream.Write($junk, 0, 512)
            $stream.Flush()
        }
        finally { $stream.Dispose() }

        $result = Invoke-Engine @('recover', '--disk', $disk, '--journal', $journal)
        if ($result.ExitCode -eq 0 -or -not ($result.Output -match 'verification failed')) { throw "Recovery did not catch the bad copy: $($result.Output -join ' | ')" }
        if (Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended') { throw 'A copy that failed verification was put back in the table.' }

        $result = Invoke-Engine @('compare', '--disk', $disk, '--journal', $journal)
        $diffs = @($result.Output | Where-Object { $_ -like 'diff *' })
        if ($result.ExitCode -ne 0 -or $diffs.Count -ne 1 -or -not ($diffs[0] -match "source_byte=$($extended.Offset + $extended.Size - 8MB) length=512 ")) {
            throw "compare did not find exactly the damaged sector: $($result.Output | Where-Object { $_ -notlike 'progress*' })"
        }

        $result = Invoke-Engine @('rollback', '--disk', $disk, '--journal', $journal)
        if ($result.ExitCode -ne 0 -or -not ($result.Output -match '^result=rolled-back')) { throw "Rollback failed: $($result.Output -join ' | ')" }
        $after = @(Get-Partition -DiskNumber $disk | Sort-Object Offset | ForEach-Object { "$($_.Offset)/$($_.Size)/$($_.Type)" })
        if (($after -join ',') -ne ($before -join ',')) { throw "Layout after rollback: $($after -join ', '); before: $($before -join ', ')" }
        foreach ($label in $labels) {
            $now = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Volume.Path
            if ($now -ne $paths[$label]) { throw "$label volume GUID changed: $now (was $($paths[$label]))." }
        }
        $result = Invoke-Engine @('recover', '--disk', $disk, '--journal', $journal)
        if (-not ($result.Output -match '^result=nothing-to-do')) { throw "Journal still unfinished after rollback: $($result.Output -join ' | ')" }

        Dismount-VHD -Path $path
        $vhd = Mount-VHD -Path $path -ReadOnly -NoDriveLetter -Passthru
        foreach ($data in $manifest.Data) {
            $found = Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $data.Label
            $problems = @(Test-DeterministicData -Root $found.Volume.Path -TargetBytes $data.Bytes -Seed $data.Seed)
            if ($problems.Count) { throw "$($data.Label): $($problems -join '; ')" }
        }
        Dismount-VHD -Path $path
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        foreach ($label in $labels) {
            $scan = Repair-Volume -Path (Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $label).Volume.Path -Scan
            if ("$scan" -ne 'NoErrorsFound') { throw "$label file system scan reported: $scan" }
        }
    }
    finally {
        Dismount-VHD -Path $path -ErrorAction SilentlyContinue
        Remove-Item $path, "$path.manifest.json", $journal, "$journal.log" -ErrorAction SilentlyContinue
    }
}

function Get-OpmPath {
    $root = Join-Path $PSScriptRoot '..\..\..\src\dotnet\OpenPartition.Cli\bin'
    $exe = Get-ChildItem $root -Recurse -Filter opm.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'opm.exe not found; run dotnet build first.' }
    return $exe.FullName
}

function Invoke-Opm {
    param([string[]] $Arguments)
    $env:OPM_ENGINE_EXE = Get-EnginePath
    $output = & (Get-OpmPath) @Arguments 2>&1
    [pscustomobject] @{ ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
}

function Invoke-OpmFreeAndExtendScenario {
    <#
        The same Azure field case, driven through `opm move` / `opm extend` / `opm resume`: the
        planner, write gate and runner the GUI uses. With -Interrupt the engine is killed during
        the extended-partition move and `opm resume` finishes it.
    #>
    param([switch] $Interrupt)
    $layout = 'mbr-512n-logical-boxed-in'
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $path = Join-Path $env:OPM_HARNESS_OUT "opm-$layout-$id.vhdx"
    try {
        $manifest = New-TestDisk -Layout $layout -Path $path -Seed 17
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        $disk = $vhd.DiskNumber
        $labels = @($manifest.Data | ForEach-Object Label)
        $paths = @{}
        foreach ($label in $labels) { $paths[$label] = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Volume.Path }
        $sys = (Find-PartitionByLabel -DiskNumber $disk -Label 'SYS').Partition
        $extended = Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended'

        $dry = Invoke-Opm @('move', $disk, 'extended', '--to', 'end')
        if ($dry.ExitCode -ne 0 -or -not ($dry.Output -match 'Dry run')) { throw "Dry run failed: $($dry.Output -join ' | ')" }
        if ((Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended').Offset -ne $extended.Offset) { throw 'A dry run changed the disk.' }

        if ($Interrupt) {
            $env:OPM_ENGINE_DELAY_MS = '400'
            $env:OPM_ENGINE_EXE = Get-EnginePath
            $log = Join-Path $env:OPM_HARNESS_OUT "opm-$id.log"
            $opm = Start-Process -FilePath (Get-OpmPath) -ArgumentList @('move', $disk, 'extended', '--to', 'end', '--yes') -PassThru -NoNewWindow -RedirectStandardOutput $log
            $deadline = (Get-Date).AddMinutes(2)
            while (-not $opm.HasExited -and (Get-Date) -lt $deadline -and -not @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match '%' }).Count) { Start-Sleep -Milliseconds 100 }
            $engine = Get-Process opm-engine -ErrorAction SilentlyContinue
            if (-not $engine) { throw "The engine was not running to interrupt: $(Get-Content $log -Raw)" }
            $engine | Stop-Process -Force
            $opm.WaitForExit()
            Remove-Item Env:OPM_ENGINE_DELAY_MS
            if (Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended') { throw 'Extended partition still in the table mid-move.' }
            $resume = Invoke-Opm @('resume', '--yes')
            if ($resume.ExitCode -ne 0 -or -not ($resume.Output -match 'Finished')) { throw "opm resume failed: $($resume.Output -join ' | ')" }
            Remove-Item $log -ErrorAction SilentlyContinue
        }
        else {
            $r = Invoke-Opm @('move', $disk, 'extended', '--to', 'end', '--yes')
            if ($r.ExitCode -ne 0) { throw "opm move extended failed: $($r.Output -join ' | ')" }
        }
        $primary = (Find-PartitionByLabel -DiskNumber $disk -Label 'PRIM').Partition
        $r = Invoke-Opm @('move', $disk, "@$($primary.Offset)", '--to', 'end', '--yes')
        if ($r.ExitCode -ne 0) { throw "opm move primary failed: $($r.Output -join ' | ')" }
        $r = Invoke-Opm @('extend', $disk, "@$($sys.Offset)", '--yes')
        if ($r.ExitCode -ne 0) { throw "opm extend failed: $($r.Output -join ' | ')" }

        $sysNow = (Find-PartitionByLabel -DiskNumber $disk -Label 'SYS').Partition
        $primNow = (Find-PartitionByLabel -DiskNumber $disk -Label 'PRIM').Partition
        if ($sysNow.Offset + $sysNow.Size -ne $primNow.Offset) { throw "SYS ends at $($sysNow.Offset + $sysNow.Size), PRIM starts at $($primNow.Offset): space left between them." }
        foreach ($label in $labels) {
            $now = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Volume.Path
            if ($now -ne $paths[$label]) { throw "$label volume GUID changed." }
        }
        Dismount-VHD -Path $path
        $vhd = Mount-VHD -Path $path -ReadOnly -NoDriveLetter -Passthru
        foreach ($data in $manifest.Data) {
            $found = Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $data.Label
            $problems = @(Test-DeterministicData -Root $found.Volume.Path -TargetBytes $data.Bytes -Seed $data.Seed)
            if ($problems.Count) { throw "$($data.Label): $($problems -join '; ')" }
        }
        Dismount-VHD -Path $path
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        foreach ($label in $labels) {
            $scan = Repair-Volume -Path (Find-PartitionByLabel -DiskNumber $vhd.DiskNumber -Label $label).Volume.Path -Scan
            if ("$scan" -ne 'NoErrorsFound') { throw "$label file system scan reported: $scan" }
        }
    }
    finally {
        Remove-Item Env:OPM_ENGINE_DELAY_MS -ErrorAction SilentlyContinue
        Dismount-VHD -Path $path -ErrorAction SilentlyContinue
        Remove-Item $path, "$path.manifest.json" -ErrorAction SilentlyContinue
    }
}

function Test-OpmFreesSpaceAndExtends {
    Assert-EngineHost
    Invoke-OpmFreeAndExtendScenario
}

function Test-OpmResumesAnInterruptedMove {
    Assert-EngineHost
    Invoke-OpmFreeAndExtendScenario -Interrupt
}

function Test-OpmGrowsLastLogicalDrive {
    <#
        Free space after an extended partition: grow the container (`opm extend <disk>
        extended`), then extend its last logical drive with Windows' own resize. The other
        logical drives and every file must be untouched.
    #>
    Assert-EngineHost
    $layout = 'mbr-512n-logical-boxed-in'
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $path = Join-Path $env:OPM_HARNESS_OUT "opm-grow-$id.vhdx"
    try {
        $manifest = New-TestDisk -Layout $layout -Path $path -Seed 23
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        $disk = $vhd.DiskNumber
        $before = @{}
        foreach ($data in $manifest.Data) { $before[$data.Label] = Find-PartitionByLabel -DiskNumber $disk -Label $data.Label }
        $extended = Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended'

        $refused = Invoke-Opm @('extend', $disk, "@$($before['LOG2'].Partition.Offset)")
        if ($refused.ExitCode -eq 0) { throw 'A logical drive that is not the last one was allowed to grow.' }

        $r = Invoke-Opm @('extend', $disk, 'extended', '--yes')
        if ($r.ExitCode -ne 0) { throw "opm extend extended failed: $($r.Output -join ' | ')" }
        $grown = Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Extended'
        if ($grown.Offset -ne $extended.Offset -or $grown.Size -le $extended.Size) { throw "Extended partition did not grow: $($grown.Size)." }

        $r = Invoke-Opm @('extend', $disk, "@$($before['LOG3'].Partition.Offset)", '--yes')
        if ($r.ExitCode -ne 0) { throw "opm extend LOG3 failed: $($r.Output -join ' | ')" }
        $log3 = Find-PartitionByLabel -DiskNumber $disk -Label 'LOG3'
        if ($log3.Partition.Offset + $log3.Partition.Size -ne $grown.Offset + $grown.Size) { throw 'LOG3 does not reach the end of the extended partition.' }
        foreach ($label in 'SYS', 'PRIM', 'LOG1', 'LOG2') {
            $now = (Find-PartitionByLabel -DiskNumber $disk -Label $label).Partition
            if ($now.Offset -ne $before[$label].Partition.Offset -or $now.Size -ne $before[$label].Partition.Size) { throw "$label changed." }
        }
        foreach ($data in $manifest.Data) {
            $found = Find-PartitionByLabel -DiskNumber $disk -Label $data.Label
            if ($found.Volume.Path -ne $before[$data.Label].Volume.Path) { throw "$($data.Label) volume GUID changed." }
            $problems = @(Test-DeterministicData -Root $found.Volume.Path -TargetBytes $data.Bytes -Seed $data.Seed)
            if ($problems.Count) { throw "$($data.Label): $($problems -join '; ')" }
            $scan = Repair-Volume -Path $found.Volume.Path -Scan
            if ("$scan" -ne 'NoErrorsFound') { throw "$($data.Label) file system scan reported: $scan" }
        }
    }
    finally {
        Dismount-VHD -Path $path -ErrorAction SilentlyContinue
        Remove-Item $path, "$path.manifest.json" -ErrorAction SilentlyContinue
    }
}
