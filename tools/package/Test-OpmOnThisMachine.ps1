<#
.SYNOPSIS
    Open Partition Manager preview - self-test for a server or Azure VM.

.DESCRIPTION
    1. Read-only: records what `opm list` / `opm show` see on this machine (all disks).
    2. Engine self-test: creates small throwaway VHDX disks with diskpart (no Hyper-V needed),
       moves a partition, kills a second move part way and recovers it, and checks every file
       byte for byte plus a file system scan. A third round builds this VM's own shape on MBR
       (a boxed-in system partition followed by a primary and an extended partition with
       logical drives), moves the extended partition and the primary to the end, extends the
       first partition with Resize-Partition, and checks files, volume GUIDs and drive letters.
       The VHDX files are deleted afterwards.

    Nothing here writes to this machine's real disks. opm-engine refuses to open any disk
    that is not a VHD/VHDX, and this script only points it at the VHDX it created.

    Runs on Windows PowerShell 5.1 or PowerShell 7. Run it elevated.

.PARAMETER WorkDir
    Folder for the throwaway VHDX (about 1 GB free space needed). Defaults to a folder next to
    this script. Do not use the Azure temporary disk (D:) if you want to keep the report.

.PARAMETER SkipEngineTest
    Only run the read-only discovery part.

.PARAMETER RollbackOnly
    Only run the two rollback rounds: an extended partition moved far right whose finished
    copy fails the final hash check must stay out of the table, `compare` must find the bad
    sector, and `rollback` must put every logical drive back exactly as it was; and a move
    during which something writes into the source must stop and undo itself.
#>
param(
    [string] $WorkDir = (Join-Path $PSScriptRoot 'selftest-work'),
    [switch] $SkipEngineTest,
    [switch] $RollbackOnly
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
            $copyBytes = if ($r.Output -match 'copy_bytes=(\d+)') { [long]$Matches[1] } else { 0 }
            # The volume is 60% full: its free space must be skipped, not copied.
            if ($copyBytes -ge $partition.Size) { throw "free space was not skipped: copied $copyBytes of $($partition.Size) bytes ($(($r.Output -split "`n" | Where-Object { $_ -like 'locked=*' }) -join ' '))" }
            $detail = "moved $([Math]::Round(($to - $from) / 1MB, 2)) MiB right (from offset $from), copying $([Math]::Round($copyBytes / 1MB)) MiB of $([Math]::Round($partition.Size / 1MB)) MiB"
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
        Remove-Item $journal, "$journal.log", "$journal.map" -ErrorAction SilentlyContinue
    }
}

function Get-FreeDriveLetter {
    $used = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object Name) + @(Get-Volume | Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter })
    foreach ($c in 'PQRSTUVWXYZ'.ToCharArray()) { if ([string]$c -notin $used) { return [string]$c } }
    throw 'No free drive letter for the test.'
}

function Find-Labelled([int] $DiskNumber, [string] $Label) {
    foreach ($p in Get-Partition -DiskNumber $DiskNumber) {
        $v = $p | Get-Volume -ErrorAction SilentlyContinue
        if ($v -and $v.FileSystemLabel -eq $Label) { return [pscustomobject] @{ Partition = $p; Volume = $v } }
    }
    throw "No volume labelled $Label on disk $DiskNumber."
}

# A throwaway MBR VHDX shaped like this VM's disk: OPMSYS, OPMPRIM, then an extended partition
# (at 225 MiB, 320 MiB long) with three logical drives, each holding test files.
function New-MbrShapeDisk([string] $vhdx) {
        $existing = @(Get-Disk | ForEach-Object Number)
        Invoke-DiskPart @("create vdisk file=`"$vhdx`" maximum=1024 type=expandable", "select vdisk file=`"$vhdx`"", 'attach vdisk') | Out-Null
        $disk = $null
        foreach ($i in 1..20) {
            Start-Sleep -Milliseconds 500
            $disk = Get-Disk | Where-Object { $_.Number -notin $existing -and $_.BusType -eq 'File Backed Virtual' } | Select-Object -First 1
            if ($disk) { break }
        }
        if (-not $disk) { throw 'Attached VHDX did not appear as a disk.' }
        $number = $disk.Number
        Initialize-Disk -Number $number -PartitionStyle MBR
        $volumes = @(
            @{ Label = 'OPMSYS'; Size = 128MB }, @{ Label = 'OPMPRIM'; Size = 96MB },
            @{ Label = 'OPMLOG1'; Size = 96MB }, @{ Label = 'OPMLOG2'; Size = 96MB }, @{ Label = 'OPMLOG3'; Size = 96MB }
        )
        New-Partition -DiskNumber $number -Offset 1MB -Size 128MB | Format-Volume -FileSystem NTFS -NewFileSystemLabel OPMSYS -Confirm:$false | Out-Null
        New-Partition -DiskNumber $number -Offset 129MB -Size 96MB | Format-Volume -FileSystem NTFS -NewFileSystemLabel OPMPRIM -Confirm:$false | Out-Null
        New-Partition -DiskNumber $number -Offset 225MB -Size 320MB -MbrType Extended | Out-Null
        foreach ($label in 'OPMLOG1', 'OPMLOG2', 'OPMLOG3') {
            New-Partition -DiskNumber $number -Size 96MB | Format-Volume -FileSystem NTFS -NewFileSystemLabel $label -Confirm:$false | Out-Null
        }
        $seed = 100
        foreach ($v in $volumes) {
            $v.Seed = $seed; $seed++
            $v.Bytes = [long]($v.Size * 0.35)
            $found = Find-Labelled $number $v.Label
            $mount = Join-Path $WorkDir "mnt-$($v.Label)"
            New-Item -ItemType Directory -Path $mount -Force | Out-Null
            $found.Partition | Add-PartitionAccessPath -AccessPath $mount
            Write-TestData -Root $mount -TargetBytes $v.Bytes -Seed $v.Seed
            $found.Partition | Remove-PartitionAccessPath -AccessPath $mount
            Remove-Item $mount -Force
            $v.Path = $found.Volume.Path
        }
        return [pscustomobject] @{ Number = $number; Volumes = $volumes }
}

function Test-ExtendedRound([string] $Name) {
    $vhdx = Join-Path $WorkDir "opm-selftest-$([guid]::NewGuid().ToString('N').Substring(0, 8)).vhdx"
    $journal = "$vhdx.journal"
    $letters = @{}
    try {
        $shape = New-MbrShapeDisk $vhdx
        $number = $shape.Number
        $volumes = $shape.Volumes
        # A Remote Desktop client on this machine redirects new drive letters into its sessions
        # and holds them open, so lettered test volumes could not be locked. Skip letters then.
        $rdp = [bool](Get-Process msrdc, mstsc -ErrorAction SilentlyContinue)
        foreach ($label in @(if (-not $rdp) { 'OPMPRIM', 'OPMLOG2' })) {
            $letter = Get-FreeDriveLetter
            (Find-Labelled $number $label).Partition | Set-Partition -NewDriveLetter $letter
            $letters[$label] = $letter
        }

        $start = Get-Date
        $shift = 256MB
        $extended = Get-Partition -DiskNumber $number | Where-Object Type -eq 'Extended'
        $primary = (Find-Labelled $number 'OPMPRIM').Partition
        foreach ($m in @(@{ What = 'extended partition'; From = $extended.Offset; Size = $extended.Size }, @{ What = 'primary'; From = $primary.Offset; Size = $primary.Size })) {
            $to = $m.From + $shift
            $r = Invoke-Native $engine @('move', '--disk', $number, '--from', $m.From, '--to', $to, '--journal', $journal)
            $r.Output | Out-File (Join-Path $reportDir "extended-round-move-$($m.What -replace ' ', '-').txt") -Encoding utf8
            if ($r.ExitCode -ne 0) { throw "moving the $($m.What) failed: $(($r.Output -split "`n" | Where-Object { $_ -notlike 'progress*' }) -join ' ')" }
            if (-not (Get-Partition -DiskNumber $number | Where-Object { $_.Offset -eq $to -and $_.Size -eq $m.Size })) { throw "the $($m.What) is not at $to after the move" }
        }
        $sys = (Find-Labelled $number 'OPMSYS').Partition
        $max = (Get-PartitionSupportedSize -DiskNumber $number -PartitionNumber $sys.PartitionNumber).SizeMax
        if ($max -lt $sys.Size + $shift - 1MB) { throw "the first partition can only grow to $max bytes" }
        $sys | Resize-Partition -Size $max
        $seconds = [Math]::Round(((Get-Date) - $start).TotalSeconds, 1)

        $problems = New-Object System.Collections.Generic.List[string]
        foreach ($label in $letters.Keys) {
            $now = [string](Find-Labelled $number $label).Partition.DriveLetter
            if ($now -ne $letters[$label]) { $problems.Add("$label drive letter $($letters[$label]) became '$now'") }
        }
        foreach ($v in $volumes) {
            $found = Find-Labelled $number $v.Label
            if ($found.Volume.Path -ne $v.Path) { $problems.Add("$($v.Label) volume GUID changed") }
            $bad = Test-TestData -Root $found.Volume.Path -TargetBytes $v.Bytes -Seed $v.Seed
            if ($bad) { $problems.Add("$($v.Label): $bad file(s) differ") }
            $scan = Repair-Volume -Path $found.Volume.Path -Scan
            if ("$scan" -ne 'NoErrorsFound') { $problems.Add("$($v.Label) scan: $scan") }
        }
        if ($problems.Count) { throw ($problems -join '; ') }
        Add-Result $Name 'PASS' "extended partition (3 logical drives) and primary moved 256 MiB, first partition extended, in $seconds s; files identical; scans clean; volume GUIDs kept; $(if ($rdp) { 'drive letters NOT tested (Remote Desktop client running)' } else { 'drive letters kept' })"
    }
    catch {
        Add-Result $Name 'FAIL' $_.Exception.Message
    }
    finally {
        Remove-SelfTestDisk $vhdx
        Remove-Item $journal -ErrorAction SilentlyContinue
    }
}

# Raw sector write for the rollback round. Windows PowerShell's FileStream refuses device paths.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class OpmRawDisk {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    public static void WriteSector(int disk, long offset, byte value) {
        using (var handle = CreateFile(@"\\.\PhysicalDrive" + disk, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new IOException("open failed: " + Marshal.GetLastWin32Error());
            using (var stream = new FileStream(handle, FileAccess.ReadWrite, 512)) {
                var data = new byte[512];
                for (int i = 0; i < data.Length; i++) data[i] = value;
                stream.Seek(offset, SeekOrigin.Begin);
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }
    }
}
'@

function Get-LayoutText([int] $DiskNumber) {
    (@(Get-Partition -DiskNumber $DiskNumber | Sort-Object Offset | ForEach-Object { "$($_.Offset)/$($_.Size)/$($_.Type)" })) -join ', '
}

function Test-RollbackRound([string] $Name) {
    $vhdx = Join-Path $WorkDir "opm-selftest-$([guid]::NewGuid().ToString('N').Substring(0, 8)).vhdx"
    $journal = "$vhdx.journal"
    try {
        $shape = New-MbrShapeDisk $vhdx
        $number = $shape.Number
        $rdp = [bool](Get-Process msrdc, mstsc -ErrorAction SilentlyContinue)
        $letters = @{}
        foreach ($label in @(if (-not $rdp) { 'OPMLOG1', 'OPMLOG3' })) {
            $letter = Get-FreeDriveLetter
            (Find-Labelled $number $label).Partition | Set-Partition -NewDriveLetter $letter
            $letters[$label] = $letter
        }
        $before = Get-LayoutText $number
        $extended = Get-Partition -DiskNumber $number | Where-Object Type -eq 'Extended'
        # Far right, so source and target don't overlap (like 62 GB -> 191 GB on the real disk).
        $to = 640MB
        if ($to -lt $extended.Offset + $extended.Size) { throw 'test layout: target overlaps the source' }

        # Kill the move once the first chunk (the last 16 MiB of the partition) is copied.
        $log = "$journal.log"
        $p = Start-Process -FilePath $engine -ArgumentList @('move', '--disk', $number, '--from', $extended.Offset, '--to', $to, '--journal', $journal, '--chunk', 16MB, '--delay-ms', 300, '--full-copy') -PassThru -NoNewWindow -RedirectStandardOutput $log
        $deadline = (Get-Date).AddMinutes(5)
        while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
            if (@(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -like 'progress stage=copying*' }).Count) { break }
            Start-Sleep -Milliseconds 50
        }
        if ($p.HasExited) { throw "move finished before it could be killed: $(Get-Content $log -Raw)" }
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit()

        # Damage one sector of the part already copied (unpartitioned space while the entry is out).
        $bad = $extended.Offset + $extended.Size - 8MB
        [OpmRawDisk]::WriteSector($number, $to + $extended.Size - 8MB, 0xEE)

        $r = Invoke-Native $engine @('recover', '--disk', $number, '--journal', $journal)
        $r.Output | Out-File (Join-Path $reportDir 'rollback-round-recover.txt') -Encoding utf8
        if ($r.ExitCode -eq 0 -or $r.Output -notmatch 'verification failed') { throw "recovery did not catch the bad copy: $($r.Output)" }
        if (Get-Partition -DiskNumber $number | Where-Object Type -eq 'Extended') { throw 'a copy that failed verification was put back in the table' }

        $r = Invoke-Native $engine @('compare', '--disk', $number, '--journal', $journal)
        $r.Output | Out-File (Join-Path $reportDir 'rollback-round-compare.txt') -Encoding utf8
        $diffs = @($r.Output -split "`n" | Where-Object { $_ -like 'diff *' })
        if ($r.ExitCode -ne 0 -or $diffs.Count -ne 1 -or $diffs[0] -notmatch "source_byte=$bad length=512 ") { throw "compare did not find exactly the damaged sector: $($r.Output)" }

        $r = Invoke-Native $engine @('rollback', '--disk', $number, '--journal', $journal)
        $r.Output | Out-File (Join-Path $reportDir 'rollback-round-rollback.txt') -Encoding utf8
        if ($r.ExitCode -ne 0 -or $r.Output -notmatch 'result=rolled-back') { throw "rollback failed: $($r.Output)" }
        $after = Get-LayoutText $number
        if ($after -ne $before) { throw "layout after rollback: $after; before: $before" }
        $r = Invoke-Native $engine @('recover', '--disk', $number, '--journal', $journal)
        if ($r.Output -notmatch 'result=nothing-to-do') { throw "journal still unfinished after rollback: $($r.Output)" }

        $problems = New-Object System.Collections.Generic.List[string]
        foreach ($label in $letters.Keys) {
            $now = [string](Find-Labelled $number $label).Partition.DriveLetter
            if ($now -ne $letters[$label]) { $problems.Add("$label drive letter $($letters[$label]) became '$now'") }
        }
        foreach ($v in $shape.Volumes) {
            $found = Find-Labelled $number $v.Label
            if ($found.Volume.Path -ne $v.Path) { $problems.Add("$($v.Label) volume GUID changed") }
            $badFiles = Test-TestData -Root $found.Volume.Path -TargetBytes $v.Bytes -Seed $v.Seed
            if ($badFiles) { $problems.Add("$($v.Label): $badFiles file(s) differ") }
            $scan = Repair-Volume -Path $found.Volume.Path -Scan
            if ("$scan" -ne 'NoErrorsFound') { $problems.Add("$($v.Label) scan: $scan") }
        }
        if ($problems.Count) { throw ($problems -join '; ') }
        Add-Result $Name 'PASS' "bad copy refused, compare found the damaged sector, rollback restored the layout; files identical; scans clean; volume GUIDs kept; $(if ($rdp) { 'drive letters NOT tested (Remote Desktop client running)' } else { 'drive letters kept' })"
    }
    catch {
        Add-Result $Name 'FAIL' $_.Exception.Message
    }
    finally {
        Remove-SelfTestDisk $vhdx
        Remove-Item $journal, "$journal.log" -ErrorAction SilentlyContinue
    }
}

function Test-BusyDiskRound([string] $Name) {
    $vhdx = Join-Path $WorkDir "opm-selftest-$([guid]::NewGuid().ToString('N').Substring(0, 8)).vhdx"
    $journal = "$vhdx.journal"
    try {
        $shape = New-MbrShapeDisk $vhdx
        $number = $shape.Number
        $rdp = [bool](Get-Process msrdc, mstsc -ErrorAction SilentlyContinue)
        $letters = @{}
        foreach ($label in @(if (-not $rdp) { 'OPMLOG1', 'OPMLOG3' })) {
            $letter = Get-FreeDriveLetter
            (Find-Labelled $number $label).Partition | Set-Partition -NewDriveLetter $letter
            $letters[$label] = $letter
        }
        $before = Get-LayoutText $number
        $extended = Get-Partition -DiskNumber $number | Where-Object Type -eq 'Extended'
        $to = 640MB
        $firstLogical = Get-Partition -DiskNumber $number | Where-Object { $_.Type -ne 'Extended' -and $_.Offset -gt $extended.Offset } | Sort-Object Offset | Select-Object -First 1
        # Windows leaves a gap between the extended partition's first sector (its boot record)
        # and the first logical drive: 64 KiB on Server 2016, 1 MiB on newer versions.
        $gap = $firstLogical.Offset - $extended.Offset
        if ($gap -lt 8KB) { throw "test layout: no gap before the first logical drive ($gap bytes)" }
        $junkAt = $extended.Offset + [long]([Math]::Floor($gap / 2 / 4KB) * 4KB)

        $log = "$journal.log"
        $p = Start-Process -FilePath $engine -ArgumentList @('move', '--disk', $number, '--from', $extended.Offset, '--to', $to, '--journal', $journal, '--chunk', 16MB, '--delay-ms', 300) -PassThru -NoNewWindow -RedirectStandardOutput $log
        $deadline = (Get-Date).AddMinutes(5)
        while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
            if (@(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -like 'progress stage=copying*' }).Count) { break }
            Start-Sleep -Milliseconds 50
        }
        if ($p.HasExited) { throw "move finished before the source could be changed: $(Get-Content $log -Raw)" }
        # Something else writes into the source while it is being copied: halfway into the gap
        # before the first logical drive (inside the extended partition, outside any volume, so
        # no file is damaged). That chunk is copied last when moving right.
        [OpmRawDisk]::WriteSector($number, $junkAt, 0xEE)
        if (-not $p.WaitForExit(300000)) { Stop-Process -Id $p.Id -Force; throw 'move did not stop' }
        $output = Get-Content $log -Raw
        Copy-Item $log (Join-Path $reportDir 'busy-round-move.txt')
        if ($p.ExitCode -eq 0) { throw "the move finished although its source changed: $output" }
        if ($output -notmatch 'rolled_back=yes' -or $output -notmatch 'changed after it was hashed') { throw "the move did not stop and undo itself: $output" }
        $after = Get-LayoutText $number
        if ($after -ne $before) { throw "layout after the automatic undo: $after; before: $before" }

        $problems = New-Object System.Collections.Generic.List[string]
        foreach ($label in $letters.Keys) {
            $now = [string](Find-Labelled $number $label).Partition.DriveLetter
            if ($now -ne $letters[$label]) { $problems.Add("$label drive letter $($letters[$label]) became '$now'") }
        }
        foreach ($v in $shape.Volumes) {
            $found = Find-Labelled $number $v.Label
            if ($found.Volume.Path -ne $v.Path) { $problems.Add("$($v.Label) volume GUID changed") }
            $badFiles = Test-TestData -Root $found.Volume.Path -TargetBytes $v.Bytes -Seed $v.Seed
            if ($badFiles) { $problems.Add("$($v.Label): $badFiles file(s) differ") }
            $scan = Repair-Volume -Path $found.Volume.Path -Scan
            if ("$scan" -ne 'NoErrorsFound') { $problems.Add("$($v.Label) scan: $scan") }
        }
        if ($problems.Count) { throw ($problems -join '; ') }
        $copied = @($output -split "`n" | Where-Object { $_ -like 'progress stage=copying*' }).Count
        Add-Result $Name 'PASS' "change detected after $copied chunk(s) copied, move undone automatically; files identical; scans clean; volume GUIDs kept; $(if ($rdp) { 'drive letters NOT tested (Remote Desktop client running)' } else { 'drive letters kept' })"
    }
    catch {
        Add-Result $Name 'FAIL' $_.Exception.Message
    }
    finally {
        Remove-SelfTestDisk $vhdx
        Remove-Item $journal, "$journal.log", "$journal.map" -ErrorAction SilentlyContinue
    }
}

if ($SkipEngineTest) {
    Add-Result 'Engine self-test' 'SKIP' '-SkipEngineTest'
}
else {
    Write-Host "`n== Engine self-test on a throwaway VHDX in $WorkDir ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    if (-not $RollbackOnly) {
        Test-MoveRound 'Move a partition (VHDX)' $false
        Test-MoveRound 'Kill a move and recover (VHDX)' $true
        Test-ExtendedRound 'Free space after a boxed-in MBR partition (VHDX)'
    }
    Test-RollbackRound 'Roll back a move whose copy fails verification (VHDX)'
    Test-BusyDiskRound 'Stop and undo a move whose source changes under it (VHDX)'
    Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- Summary
Write-Host "`n== Summary ==" -ForegroundColor Cyan
$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Tee-Object -FilePath (Join-Path $reportDir 'summary.txt')
Stop-Transcript | Out-Null
$zip = "$reportDir.zip"
Compress-Archive -Path "$reportDir\*" -DestinationPath $zip -Force
Write-Host "Report: $zip  (send this back)" -ForegroundColor Green
