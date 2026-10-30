<#
.SYNOPSIS
    Open Partition Manager preview - self-test for a server or Azure VM.

.DESCRIPTION
    1. Read-only: records what `opm list` / `opm show` see on this machine (all disks).
    2. Engine self-test: creates a small throwaway VHDX with diskpart (no Hyper-V needed),
       moves a partition on it, kills a second move part way and recovers it, and checks every
       file byte for byte plus a file system scan. The VHDX is deleted afterwards.

    Nothing here writes to this machine's real disks. opm-engine refuses to open any disk
    that is not a VHD/VHDX, and this script only points it at the VHDX it created.

    Runs on Windows PowerShell 5.1 or PowerShell 7. Run it elevated.

.PARAMETER WorkDir
    Folder for the throwaway VHDX (about 1 GB free space needed). Defaults to a folder next to
    this script. Do not use the Azure temporary disk (D:) if you want to keep the report.

.PARAMETER SkipEngineTest
    Only run the read-only discovery part.
#>
param(
    [string] $WorkDir = (Join-Path $PSScriptRoot 'selftest-work'),
    [switch] $SkipEngineTest
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$opm = Join-Path $here 'opm.exe'
$engine = Join-Path $here 'opm-engine.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportDir = Join-Path $here "report-$env:COMPUTERNAME-$stamp"
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$transcript = Join-Path $reportDir 'selftest.log'
Start-Transcript -Path $transcript | Out-Null

$results = New-Object System.Collections.Generic.List[object]

# Run a program and capture its output and exit code. Windows PowerShell 5.1 turns a native
# program's stderr into a terminating error when ErrorActionPreference is Stop, so relax it
# for the call.
function Invoke-Native([string] $FilePath, [object[]] $Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    [pscustomobject] @{ Output = ($output -join "`n"); ExitCode = $code }
}
function Add-Result([string] $Name, [string] $Status, [string] $Detail) {
    $results.Add([pscustomobject] @{ Check = $Name; Status = $Status; Detail = $Detail })
    $colour = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow'; INFO = 'Cyan' }[$Status]
    Write-Host ("[{0}] {1}  {2}" -f $Status, $Name, $Detail) -ForegroundColor $colour
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Please run this script from an elevated (Run as Administrator) PowerShell.' -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}

# ---------------------------------------------------------------- 1. Read-only discovery
Write-Host "`n== Read-only discovery ==" -ForegroundColor Cyan
& $opm list | Tee-Object -FilePath (Join-Path $reportDir 'opm-list.txt')
& $opm list --json | Out-File -FilePath (Join-Path $reportDir 'opm-list.json') -Encoding utf8
$snapshot = (Get-Content (Join-Path $reportDir 'opm-list.json') -Raw) | ConvertFrom-Json
foreach ($disk in $snapshot.Disks) {
    & $opm show $disk.Number | Out-File -FilePath (Join-Path $reportDir "opm-show-disk$($disk.Number).txt") -Encoding utf8
}
# Windows' own view, to compare against.
Get-Disk | Format-Table Number, FriendlyName, BusType, PartitionStyle, Size, LogicalSectorSize, PhysicalSectorSize -AutoSize |
    Out-String -Width 200 | Out-File (Join-Path $reportDir 'windows-get-disk.txt') -Encoding utf8
Get-Partition | Format-Table DiskNumber, PartitionNumber, Offset, Size, Type, GptType, Guid, DriveLetter -AutoSize |
    Out-String -Width 300 | Out-File (Join-Path $reportDir 'windows-get-partition.txt') -Encoding utf8
Get-Volume | Format-Table DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining, Path -AutoSize |
    Out-String -Width 300 | Out-File (Join-Path $reportDir 'windows-get-volume.txt') -Encoding utf8

Add-Result 'Discovery ran' 'PASS' "$(@($snapshot.Disks).Count) disk(s) found"
Add-Result 'Environment detected' 'INFO' "$($snapshot.Environment)"
if ($snapshot.Environment -eq 'Azure') {
    if ($snapshot.Azure) {
        Add-Result 'Azure metadata (IMDS)' 'PASS' "$($snapshot.Azure.VmName) $($snapshot.Azure.VmSize) $($snapshot.Azure.Location)"
    }
    else {
        Add-Result 'Azure metadata (IMDS)' 'FAIL' 'detected Azure but could not read instance metadata'
    }
    $temp = @($snapshot.Disks | Where-Object IsAzureResourceDisk)
    if ($temp.Count) { Add-Result 'Azure temporary disk labelled' 'PASS' "disk $($temp[0].Number)" }
    else { Add-Result 'Azure temporary disk labelled' 'INFO' 'none found (some VM sizes have no temp disk, or it is unformatted NVMe)' }
}

# Compare partition offsets/sizes with Windows.
$mismatch = 0
foreach ($disk in $snapshot.Disks) {
    $expected = @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Sort-Object Offset)
    $actual = @($disk.Partitions | Sort-Object Offset)
    if ($expected.Count -ne $actual.Count) { $mismatch++; continue }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        if ($expected[$i].Offset -ne $actual[$i].Offset -or $expected[$i].Size -ne $actual[$i].Size) { $mismatch++ }
    }
}
if ($mismatch) { Add-Result 'Partition map matches Get-Partition' 'FAIL' "$mismatch difference(s); see report files" }
else { Add-Result 'Partition map matches Get-Partition' 'PASS' '' }

# The engine must refuse every real disk on this machine.
$refusedAll = $true
foreach ($disk in Get-Disk | Where-Object { $_.BusType -ne 'File Backed Virtual' }) {
    $r = Invoke-Native $engine @('info', '--disk', $disk.Number)
    if ($r.ExitCode -eq 0 -or $r.Output -notmatch 'refused') { $refusedAll = $false; Add-Result "Engine refuses disk $($disk.Number)" 'FAIL' $r.Output.Trim() }
}
if ($refusedAll) { Add-Result 'Engine refuses all real disks' 'PASS' '' }

# ---------------------------------------------------------------- 2. Engine self-test
function Write-TestData([string] $Root, [long] $TargetBytes, [int] $Seed) {
    $random = New-Object System.Random($Seed)
    $written = [long]0; $index = 0
    while ($written -lt $TargetBytes) {
        $dir = Join-Path $Root ('d{0:D3}' -f ($index % 37))
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
        $size = [long][Math]::Min($TargetBytes - $written, 4KB * [Math]::Pow(2, $random.Next(0, 12)))
        $bytes = New-Object byte[] $size
        $random.NextBytes($bytes)
        [System.IO.File]::WriteAllBytes((Join-Path $dir ('f{0:D6}.bin' -f $index)), $bytes)
        $written += $size; $index++
    }
}

function Test-TestData([string] $Root, [long] $TargetBytes, [int] $Seed) {
    $random = New-Object System.Random($Seed)
    $written = [long]0; $index = 0; $bad = 0
    $sha = [System.Security.Cryptography.SHA256]::Create()
    while ($written -lt $TargetBytes) {
        $file = Join-Path (Join-Path $Root ('d{0:D3}' -f ($index % 37))) ('f{0:D6}.bin' -f $index)
        $size = [long][Math]::Min($TargetBytes - $written, 4KB * [Math]::Pow(2, $random.Next(0, 12)))
        $expected = New-Object byte[] $size
        $random.NextBytes($expected)
        try {
            $actual = [System.IO.File]::ReadAllBytes($file)
            if ([Convert]::ToBase64String($sha.ComputeHash($expected)) -ne [Convert]::ToBase64String($sha.ComputeHash($actual))) { $bad++ }
        }
        catch { $bad++ }
        $written += $size; $index++
    }
    return $bad
}

function Invoke-DiskPart([string[]] $Lines) {
    $script = Join-Path $env:TEMP "opm-diskpart-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content -Path $script -Value $Lines -Encoding ascii
    try {
        $r = Invoke-Native 'diskpart.exe' @('/s', $script)
        if ($r.ExitCode -ne 0) { throw "diskpart failed: $($r.Output)" }
        return $r.Output
    }
    finally { Remove-Item $script -ErrorAction SilentlyContinue }
}

function New-SelfTestDisk([string] $Path) {
    $existing = @(Get-Disk | ForEach-Object Number)
    Invoke-DiskPart @("create vdisk file=`"$Path`" maximum=1024 type=expandable", "select vdisk file=`"$Path`"", 'attach vdisk') | Out-Null
    $disk = $null
    foreach ($i in 1..20) {
        Start-Sleep -Milliseconds 500
        $disk = Get-Disk | Where-Object { $_.Number -notin $existing -and $_.BusType -eq 'File Backed Virtual' } | Select-Object -First 1
        if ($disk) { break }
    }
    if (-not $disk) { throw "Attached VHDX did not appear as a disk." }
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT
    # No explicit offset: Initialize-Disk adds a Microsoft Reserved partition whose size
    # depends on the Windows version (16 MB on current builds, 128 MB on Server 2016).
    $partition = New-Partition -DiskNumber $disk.Number -Size 512MB
    $partition | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'OPMTEST' -Confirm:$false | Out-Null
    return $disk.Number
}

function Remove-SelfTestDisk([string] $Path) {
    if (Test-Path $Path) {
        try { Invoke-DiskPart @("select vdisk file=`"$Path`"", 'detach vdisk') | Out-Null } catch { }
        Remove-Item $Path -Force -ErrorAction SilentlyContinue
    }
}

function Get-TestVolume([int] $DiskNumber) {
    foreach ($p in Get-Partition -DiskNumber $DiskNumber) {
        $v = $p | Get-Volume -ErrorAction SilentlyContinue
        if ($v -and $v.FileSystemLabel -eq 'OPMTEST') { return $v }
    }
    return $null
}

function Test-MoveRound([string] $Name, [bool] $Kill) {
    $vhdx = Join-Path $WorkDir "opm-selftest-$([guid]::NewGuid().ToString('N').Substring(0, 8)).vhdx"
    $journal = "$vhdx.journal"
    try {
        $number = New-SelfTestDisk $vhdx
        $before = Get-TestVolume $number
        $partition = $null
        foreach ($p in Get-Partition -DiskNumber $number) {
            $v = $p | Get-Volume -ErrorAction SilentlyContinue
            if ($v -and $v.FileSystemLabel -eq 'OPMTEST') { $partition = $p }
        }
        if (-not $partition) { throw 'test partition not found after formatting' }
        $mount = Join-Path $WorkDir "mnt-$number"
        New-Item -ItemType Directory -Path $mount -Force | Out-Null
        $partition | Add-PartitionAccessPath -AccessPath $mount
        $bytes = [long]($before.Size * 0.6)
        Write-TestData -Root $mount -TargetBytes $bytes -Seed 42
        $partition | Remove-PartitionAccessPath -AccessPath $mount
        Remove-Item $mount -Force

        $from = $partition.Offset
        # Targets must be on a 1 MiB boundary. The source may not be: Server 2016 places a new
        # partition directly after the reserved partition (e.g. at 32 MiB + 17 KiB).
        $to = [long]([Math]::Ceiling(($from + 128MB) / 1MB) * 1MB)
        $moveArgs = @('move', '--disk', $number, '--from', $from, '--to', $to, '--journal', $journal)
        $start = Get-Date
        if ($Kill) {
            $log = "$journal.log"
            $p = Start-Process -FilePath $engine -ArgumentList ($moveArgs + @('--chunk', 16MB, '--delay-ms', 200)) -PassThru -NoNewWindow -RedirectStandardOutput $log
            # Wait until data is actually being copied (the source is hashed first, which can
            # take a while on slower cloud disks), then kill part way through.
            $deadline = (Get-Date).AddMinutes(5)
            while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
                if (@(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -like 'progress*' }).Count) { break }
                Start-Sleep -Milliseconds 200
            }
            Start-Sleep -Milliseconds 1500
            if ($p.HasExited) { throw "Move finished before it could be killed: $(Get-Content $log -Raw)" }
            Stop-Process -Id $p.Id -Force
            $p.WaitForExit()
            $chunks = @(Get-Content $log | Where-Object { $_ -like 'progress*' }).Count

            # The half-moved partition's entry should be out of the table now, with no volume
            # left for it. Saved for the report.
            $diag = Join-Path $reportDir 'after-kill-diagnostics.txt'
            "Killed after $chunks chunk(s). Partition table and volumes before recovery:" | Out-File $diag -Encoding utf8
            Get-Partition -DiskNumber $number | Format-Table PartitionNumber, Offset, Size, Type, GptType, Guid -AutoSize |
                Out-String -Width 250 | Out-File $diag -Append -Encoding utf8
            Get-Partition -DiskNumber $number | ForEach-Object {
                $v = $_ | Get-Volume -ErrorAction SilentlyContinue
                if ($v) { "partition $($_.PartitionNumber): volume $($v.Path) fs='$($v.FileSystem)' label='$($v.FileSystemLabel)' health=$($v.HealthStatus)" }
                else { "partition $($_.PartitionNumber): no volume" }
            } | Out-File $diag -Append -Encoding utf8
            (Invoke-Native $opm @('show', $number)).Output | Out-File $diag -Append -Encoding utf8
            $r = Invoke-Native $engine @('recover', '--disk', $number, '--journal', $journal)
            $r.Output | Out-File (Join-Path $reportDir 'recover-output.txt') -Encoding utf8
            if ($r.ExitCode -ne 0) { throw "recover failed: $($r.Output)" }
            if ($r.Output -match 'result=nothing-to-do') { throw "killed before the journal was written; re-run the script" }
            $onSource = ($r.Output -split "`n" | Where-Object { $_ -like 'entry_in_table=*' -or $_ -like 'locked=*' }) -join '; '
            $detail = "killed after $chunks chunk(s), recovered ($onSource)"
        }
        else {
            $r = Invoke-Native $engine $moveArgs
            if ($r.ExitCode -ne 0) { throw "move failed: $(($r.Output -split "`n" | Where-Object { $_ -notlike 'progress*' }) -join ' ')" }
            $detail = "moved $([Math]::Round(($to - $from) / 1MB, 2)) MiB right (from offset $from)"
        }
        $seconds = [Math]::Round(((Get-Date) - $start).TotalSeconds, 1)

        $after = Get-Partition -DiskNumber $number | Where-Object { $_.Guid -eq $partition.Guid }
        if (-not $after -or $after.Offset -ne $to) { throw "partition is not at the target offset" }
        if ($after.GptType -ne $partition.GptType) { throw "partition type was not restored ($($after.GptType))" }
        $volume = Get-TestVolume $number
        if (-not $volume -or $volume.Path -ne $before.Path) { throw "volume GUID changed or volume missing" }
        $bad = Test-TestData -Root $volume.Path -TargetBytes $bytes -Seed 42
        if ($bad) { throw "$bad file(s) differ after the move" }
        $scan = Repair-Volume -Path $volume.Path -Scan
        if ("$scan" -ne 'NoErrorsFound') { throw "file system scan: $scan" }
        Add-Result $Name 'PASS' "$detail in $seconds s; files identical; scan clean; volume GUID kept"
    }
    catch {
        Add-Result $Name 'FAIL' $_.Exception.Message
    }
    finally {
        Remove-SelfTestDisk $vhdx
        Remove-Item $journal, "$journal.log" -ErrorAction SilentlyContinue
    }
}

if ($SkipEngineTest) {
    Add-Result 'Engine self-test' 'SKIP' '-SkipEngineTest'
}
else {
    Write-Host "`n== Engine self-test on a throwaway VHDX in $WorkDir ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    Test-MoveRound 'Move a partition (VHDX)' $false
    Test-MoveRound 'Kill a move and recover (VHDX)' $true
    Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- Summary
Write-Host "`n== Summary ==" -ForegroundColor Cyan
$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Tee-Object -FilePath (Join-Path $reportDir 'summary.txt')
Stop-Transcript | Out-Null
$zip = "$reportDir.zip"
Compress-Archive -Path "$reportDir\*" -DestinationPath $zip -Force
Write-Host "Report: $zip  (send this back)" -ForegroundColor Green
