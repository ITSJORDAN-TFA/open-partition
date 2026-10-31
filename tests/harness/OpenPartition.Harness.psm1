#Requires -Version 7.2
<#
    Test harness for Open Partition Manager.

    Builds VHDX disks from Layouts.psd1, fills them with deterministic data, records a manifest
    (layout + SHA-256 of every partition's raw sectors), and drives Hyper-V guests for fault
    injection. Disk-touching functions need an elevated session with the Hyper-V module.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:MiB = 1MB
$script:SectorFormats = @{
    '512n' = @{ Logical = 512;  Physical = 512 }
    '512e' = @{ Logical = 512;  Physical = 4096 }
    '4Kn'  = @{ Logical = 4096; Physical = 4096 }
}

class SkipTestException : System.Exception {
    SkipTestException([string] $reason) : base($reason) {}
}

function Skip-Test {
    param([Parameter(Mandatory)] [string] $Reason)
    throw [SkipTestException]::new($Reason)
}

function Assert-Elevated {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This harness function needs an elevated (Administrator) session.'
    }
}

function Test-HarnessHostReady {
    <# True when this session can create and mount VHDX disks. #>
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    return $elevated -and [bool](Get-Command New-VHD -ErrorAction SilentlyContinue)
}

function Get-TestLayout {
    <# Returns layouts from the catalogue, each with its Name added. #>
    param([string] $Name = '*')
    $catalogue = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Layouts.psd1')
    foreach ($key in ($catalogue.Keys | Sort-Object)) {
        if ($key -like $Name) {
            $layout = $catalogue[$key].Clone()
            $layout.Name = $key
            [pscustomobject] $layout
        }
    }
}

function Test-LayoutDefinition {
    <# Static checks on a layout; returns a list of problems (empty when valid). #>
    param([Parameter(Mandatory)] $Layout)
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($Layout.Style -notin 'GPT', 'MBR') { $problems.Add("unknown Style '$($Layout.Style)'") }
    if (-not $script:SectorFormats.ContainsKey($Layout.SectorFormat)) {
        $problems.Add("unknown SectorFormat '$($Layout.SectorFormat)'")
    }
    $kinds = @($Layout.Partitions | ForEach-Object { if ($_.ContainsKey('Kind')) { $_.Kind } else { 'Primary' } })
    if ($Layout.Style -eq 'MBR' -and @($kinds | Where-Object { $_ -ne 'Logical' }).Count -gt 4) {
        $problems.Add('MBR layouts are limited to 4 primary partitions')
    }
    if (@($kinds | Where-Object { $_ -notin 'Primary', 'Recovery', 'Extended', 'Logical' }).Count) {
        $problems.Add("unknown Kind (use Primary, Recovery, Extended or Logical)")
    }
    if ($Layout.Style -ne 'MBR' -and @($kinds | Where-Object { $_ -notin 'Primary', 'Recovery' }).Count) {
        $problems.Add('extended and logical partitions need an MBR layout')
    }
    $extendedEnd = $null
    # Initialize-Disk adds a 16 MiB Microsoft Reserved partition to GPT disks.
    $cursor = if ($Layout.Style -eq 'GPT') { 16 } else { 1 }
    # Leave room for the backup GPT (or nothing for MBR) at the end of the disk.
    $usable = $Layout.DiskSizeMB - 1
    for ($i = 0; $i -lt $Layout.Partitions.Count; $i++) {
        $p = $Layout.Partitions[$i]
        if ($kinds[$i] -eq 'Logical') {
            # Inside the extended partition: 1 MiB covers the chain overhead and alignment.
            if ($null -eq $extendedEnd) { $problems.Add("logical partition $i has no extended partition before it"); continue }
            $cursor += 1 + $p.SizeMB
            if ($cursor -gt $extendedEnd) { $problems.Add("logical partition $i does not fit in its extended partition") }
            if ($p.FillPercent -lt 0 -or $p.FillPercent -gt 95) { $problems.Add("partition $i FillPercent must be 0-95") }
            continue
        }
        if ($null -ne $extendedEnd) { $cursor = $extendedEnd; $extendedEnd = $null }
        $start = if ($p.ContainsKey('OffsetMB')) { $p.OffsetMB } else { $cursor }
        if ($start -lt $cursor) { $problems.Add("partition $i overlaps the one before it") }
        $size = if ($p.SizeMB -eq 0) { $usable - $start } else { $p.SizeMB }
        if ($size -le 0 -or $start + $size -gt $usable) { $problems.Add("partition $i does not fit on the disk") }
        if ($p.SizeMB -eq 0 -and $i -ne $Layout.Partitions.Count - 1) { $problems.Add('only the last partition may use SizeMB = 0') }
        if ($p.ContainsKey('FillPercent') -and ($p.FillPercent -lt 0 -or $p.FillPercent -gt 95)) { $problems.Add("partition $i FillPercent must be 0-95") }
        $cursor = $start + $size
        if ($kinds[$i] -eq 'Extended') { $extendedEnd = $cursor; $cursor = $start }
    }
    return $problems
}

function Get-RawRangeHash {
    <# SHA-256 of a byte range read straight from \\.\PhysicalDriveN. The range must be sector aligned. #>
    param(
        [Parameter(Mandatory)] [int] $DiskNumber,
        [Parameter(Mandatory)] [long] $Offset,
        [Parameter(Mandatory)] [long] $Length
    )
    Assert-Elevated
    $chunk = 4MB
    $stream = [System.IO.FileStream]::new("\\.\PhysicalDrive$DiskNumber",
        [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite, 0)
    $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $buffer = [byte[]]::new($chunk)
        $stream.Seek($Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $remaining = $Length
        while ($remaining -gt 0) {
            $want = [int][Math]::Min($chunk, $remaining)
            $read = 0
            while ($read -lt $want) {
                $n = $stream.Read($buffer, $read, $want - $read)
                if ($n -le 0) { throw "Unexpected end of disk $DiskNumber at offset $($Offset + $Length - $remaining + $read)" }
                $read += $n
            }
            $sha.AppendData($buffer, 0, $want)
            $remaining -= $want
        }
        return [Convert]::ToHexString($sha.GetHashAndReset())
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-TestDiskManifest {
    <#
        Snapshot of a disk: partition table and raw SHA-256 per partition. Take it while the
        disk is mounted read-only (or offline) so NTFS can't change sectors under the hash.
    #>
    param([Parameter(Mandatory)] [int] $DiskNumber)
    Assert-Elevated
    $disk = Get-Disk -Number $DiskNumber
    $partitions = foreach ($p in Get-Partition -DiskNumber $DiskNumber | Sort-Object Offset) {
        $volume = $p | Get-Volume -ErrorAction SilentlyContinue
        [ordered] @{
            PartitionNumber = $p.PartitionNumber
            Offset          = $p.Offset
            Size            = $p.Size
            Type            = $p.Type
            GptType         = $p.GptType
            Guid            = $p.Guid
            MbrType         = $p.MbrType
            DriveLetter     = if ($p.DriveLetter -and $p.DriveLetter -ne [char]0) { [string]$p.DriveLetter } else { $null }
            VolumePath      = if ($volume) { $volume.Path } else { $null }
            FileSystem      = if ($volume) { $volume.FileSystem } else { $null }
            Label           = if ($volume) { $volume.FileSystemLabel } else { $null }
            Sha256          = Get-RawRangeHash -DiskNumber $DiskNumber -Offset $p.Offset -Length $p.Size
        }
    }
    [ordered] @{
        PartitionStyle    = [string]$disk.PartitionStyle
        Signature         = $disk.Signature
        Guid              = $disk.Guid
        Size              = $disk.Size
        LogicalSectorSize = $disk.LogicalSectorSize
        PhysicalSectorSize = $disk.PhysicalSectorSize
        Partitions        = @($partitions)
    }
}

function Write-DeterministicData {
    <# Fills a mounted volume with files whose sizes and contents depend only on the seed. #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [long] $TargetBytes,
        [Parameter(Mandatory)] [int] $Seed
    )
    $random = [System.Random]::new($Seed)
    $written = 0L
    $index = 0
    while ($written -lt $TargetBytes) {
        $dir = Join-Path $Root ('d{0:D3}' -f ($index % 37))
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
        # Mix of small and large files: 4 KiB to 8 MiB.
        $size = [long][Math]::Min($TargetBytes - $written, 4KB * [Math]::Pow(2, $random.Next(0, 12)))
        $bytes = [byte[]]::new($size)
        $random.NextBytes($bytes)
        [System.IO.File]::WriteAllBytes((Join-Path $dir ('f{0:D6}.bin' -f $index)), $bytes)
        $written += $size
        $index++
    }
}

function Test-DeterministicData {
    <#
        Regenerates the files Write-DeterministicData wrote and compares them byte for byte.
        Returns a list of problems (empty when every file matches). $Root may be a volume
        GUID path such as \\?\Volume{...}\, so no drive letter is needed.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [long] $TargetBytes,
        [Parameter(Mandatory)] [int] $Seed
    )
    $problems = [System.Collections.Generic.List[string]]::new()
    $random = [System.Random]::new($Seed)
    $written = 0L
    $index = 0
    while ($written -lt $TargetBytes) {
        $file = Join-Path (Join-Path $Root ('d{0:D3}' -f ($index % 37))) ('f{0:D6}.bin' -f $index)
        $size = [long][Math]::Min($TargetBytes - $written, 4KB * [Math]::Pow(2, $random.Next(0, 12)))
        $expected = [byte[]]::new($size)
        $random.NextBytes($expected)
        try {
            $actual = [System.IO.File]::ReadAllBytes($file)
            if (-not [System.Linq.Enumerable]::SequenceEqual($expected, $actual)) { $problems.Add("$file differs") }
        }
        catch {
            $problems.Add("$file unreadable: $($_.Exception.Message)")
        }
        if ($problems.Count -ge 10) { $problems.Add('...stopping after 10 problems'); break }
        $written += $size
        $index++
    }
    return $problems
}

function Set-RecoveryPartitionType {
    <# Turns a partition on a harness VHDX into a Windows Recovery partition. #>
    param([int] $DiskNumber, [int] $PartitionNumber, [string] $Style)
    $disk = Get-Disk -Number $DiskNumber
    if ($disk.BusType -ne 'File Backed Virtual') { throw "Disk $DiskNumber is not a VHD/VHDX; refusing to change it." }
    $script = if ($Style -eq 'GPT') {
        "select disk $DiskNumber`nselect partition $PartitionNumber`nset id=de94bba4-06d1-4d40-a16a-bfd50179d6ac override`ngpt attributes=0x8000000000000001"
    }
    else {
        "select disk $DiskNumber`nselect partition $PartitionNumber`nset id=27 override"
    }
    $file = New-TemporaryFile
    try {
        Set-Content -Path $file -Value $script -Encoding ascii
        $output = diskpart.exe /s $file
        if ($LASTEXITCODE) { throw "diskpart could not set the recovery type: $($output -join ' ')" }
    }
    finally {
        Remove-Item $file -ErrorAction SilentlyContinue
    }
}

function New-TestDisk {
    <#
        Creates a VHDX from a catalogue layout, fills it with deterministic data and writes
        <Path>.manifest.json. Returns the manifest. The VHDX is left detached.
    #>
    param(
        [Parameter(Mandatory)] [string] $Layout,
        [Parameter(Mandatory)] [string] $Path,
        [int] $Seed = 1
    )
    Assert-Elevated
    $definition = Get-TestLayout -Name $Layout
    if (-not $definition) { throw "Unknown layout '$Layout'." }
    $problems = Test-LayoutDefinition $definition
    if ($problems) { throw "Layout '$Layout' is invalid: $($problems -join '; ')" }
    if (Test-Path $Path) { throw "'$Path' already exists; refusing to overwrite it." }

    $format = $script:SectorFormats[$definition.SectorFormat]
    New-VHD -Path $Path -SizeBytes ($definition.DiskSizeMB * $script:MiB) -Dynamic `
        -LogicalSectorSizeBytes $format.Logical -PhysicalSectorSizeBytes $format.Physical | Out-Null

    $mountRoot = Join-Path ([System.IO.Path]::GetTempPath()) "opm-harness-$([guid]::NewGuid())"
    $dataSets = [System.Collections.Generic.List[object]]::new()
    $recoveries = [System.Collections.Generic.List[int]]::new()
    $vhd = Mount-VHD -Path $Path -NoDriveLetter -Passthru
    try {
        $diskNumber = $vhd.DiskNumber
        Initialize-Disk -Number $diskNumber -PartitionStyle $definition.Style
        $index = 0
        foreach ($p in $definition.Partitions) {
            # Initialize-Disk may add an MSR on GPT; place each partition after whatever exists.
            $existingEnd = (Get-Partition -DiskNumber $diskNumber -ErrorAction SilentlyContinue |
                ForEach-Object { $_.Offset + $_.Size } | Measure-Object -Maximum).Maximum
            if (-not $existingEnd) { $existingEnd = 0 }
            $cursor = [Math]::Max([long]$existingEnd, $script:MiB)
            $cursor = [long]([Math]::Ceiling($cursor / $script:MiB) * $script:MiB)
            $offset = if ($p.ContainsKey('OffsetMB')) { [long]$p.OffsetMB * $script:MiB } else { $cursor }
            if ($offset -lt $cursor) { throw "Partition $index of '$Layout' would overlap an existing partition." }

            $kind = if ($p.ContainsKey('Kind')) { $p.Kind } else { 'Primary' }
            $newPartition = @{ DiskNumber = $diskNumber }
            if ($kind -ne 'Logical') { $newPartition.Offset = $offset }
            if ($p.SizeMB -eq 0) { $newPartition.UseMaximumSize = $true } else { $newPartition.Size = [long]$p.SizeMB * $script:MiB }
            if ($kind -eq 'Extended') {
                New-Partition @newPartition -MbrType Extended | Out-Null
                $index++
                continue
            }
            # With an extended partition on the disk, a partition created without an offset
            # becomes the next logical drive inside it.
            $partition = New-Partition @newPartition
            $partition | Format-Volume -FileSystem $p.FileSystem -NewFileSystemLabel $p.Label -Confirm:$false | Out-Null
            if ($kind -eq 'Recovery') { $recoveries.Add($partition.PartitionNumber) }

            if ($p.FillPercent -gt 0) {
                $mount = Join-Path $mountRoot $index
                New-Item -ItemType Directory -Path $mount -Force | Out-Null
                $partition | Add-PartitionAccessPath -AccessPath $mount
                try {
                    $volume = $partition | Get-Volume
                    $target = [long]($volume.Size * $p.FillPercent / 100)
                    Write-DeterministicData -Root $mount -TargetBytes $target -Seed ($Seed * 1000 + $index)
                    $dataSets.Add([ordered] @{ Label = $p.Label; Seed = $Seed * 1000 + $index; Bytes = $target })
                }
                finally {
                    $partition | Remove-PartitionAccessPath -AccessPath $mount
                }
            }
            $index++
        }
        # Recovery type last, after the data is written (as Windows Setup leaves it: GPT type
        # de94bba4 with "no drive letter" + "required"; MBR type 0x27).
        foreach ($number in $recoveries) {
            Set-RecoveryPartitionType -DiskNumber $diskNumber -PartitionNumber $number -Style $definition.Style
        }
    }
    finally {
        Dismount-VHD -Path $Path
        Remove-Item $mountRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Hash with the disk attached read-only so nothing writes to it while we read.
    $vhd = Mount-VHD -Path $Path -ReadOnly -NoDriveLetter -Passthru
    try {
        $manifest = Get-TestDiskManifest -DiskNumber $vhd.DiskNumber
    }
    finally {
        Dismount-VHD -Path $Path
    }
    $manifest.Layout = $Layout
    $manifest.Seed = $Seed
    $manifest.Data = @($dataSets)
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path "$Path.manifest.json" -Encoding utf8
    return $manifest
}

function Invoke-InGuest {
    <# Runs a script block inside a Hyper-V guest over PowerShell Direct. #>
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [Parameter(Mandatory)] [pscredential] $Credential,
        [Parameter(Mandatory)] [scriptblock] $ScriptBlock,
        [object[]] $ArgumentList = @()
    )
    Invoke-Command -VMName $VMName -Credential $Credential -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
}

function Wait-GuestReady {
    <# Waits until PowerShell Direct into the guest works again, e.g. after a hard stop and restart. #>
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [Parameter(Mandatory)] [pscredential] $Credential,
        [int] $TimeoutSeconds = 600
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            Invoke-Command -VMName $VMName -Credential $Credential -ScriptBlock { $true } -ErrorAction Stop | Out-Null
            return
        }
        catch {
            Start-Sleep -Seconds 5
        }
    }
    throw "Guest '$VMName' was not reachable within $TimeoutSeconds seconds."
}

function Start-RandomHardStop {
    <#
        Fault injection: after a random delay, turns the VM off without a shutdown (power
        pull). Returns the background job; its output records the delay used, for
        reproducing a failure.
    #>
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [Parameter(Mandatory)] [double] $MinSeconds,
        [Parameter(Mandatory)] [double] $MaxSeconds,
        [int] $Seed = [System.Environment]::TickCount
    )
    $delay = $MinSeconds + [System.Random]::new($Seed).NextDouble() * ($MaxSeconds - $MinSeconds)
    Start-Job -Name "opm-hardstop-$VMName" -ArgumentList $VMName, $delay, $Seed -ScriptBlock {
        param($vm, $delay, $seed)
        Start-Sleep -Milliseconds ([int]($delay * 1000))
        Stop-VM -Name $vm -TurnOff -Force
        [pscustomobject] @{ VMName = $vm; DelaySeconds = $delay; Seed = $seed; StoppedAt = [DateTime]::UtcNow }
    }
}

Export-ModuleMember -Function Skip-Test, Test-HarnessHostReady, Get-TestLayout, Test-LayoutDefinition, Test-DeterministicData,
    Get-RawRangeHash, Get-TestDiskManifest, New-TestDisk, Invoke-InGuest, Wait-GuestReady, Start-RandomHardStop
