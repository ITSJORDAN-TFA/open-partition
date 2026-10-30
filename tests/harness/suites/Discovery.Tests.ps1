# Phase 1 acceptance: `opm show --json` must match Get-Partition / Get-Volume / Get-Disk for
# every layout in the catalogue.

function Get-OpmPath {
    if ($env:OPM_EXE -and (Test-Path $env:OPM_EXE)) { return $env:OPM_EXE }
    $root = Join-Path $PSScriptRoot '..\..\..\src\dotnet\OpenPartition.Cli\bin'
    $exe = Get-ChildItem $root -Recurse -Filter opm.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'opm.exe not found; build the solution first or set OPM_EXE.' }
    return $exe.FullName
}

function Compare-DiscoveryWithWindows {
    param([Parameter(Mandatory)] [int] $DiskNumber, [Parameter(Mandatory)] [string] $LayoutName)

    $json = & (Get-OpmPath) show $DiskNumber --json
    if ($LASTEXITCODE) { throw "opm show $DiskNumber failed: $json" }
    $opm = ($json -join "`n") | ConvertFrom-Json
    $disk = Get-Disk -Number $DiskNumber
    $layout = Get-TestLayout -Name $LayoutName
    $problems = [System.Collections.Generic.List[string]]::new()

    if ($opm.Size -ne $disk.Size) { $problems.Add("disk size $($opm.Size) <> $($disk.Size)") }
    if ($opm.LogicalSectorSize -ne $disk.LogicalSectorSize) { $problems.Add("logical sector $($opm.LogicalSectorSize) <> $($disk.LogicalSectorSize)") }
    if ($opm.PhysicalSectorSize -ne $disk.PhysicalSectorSize) { $problems.Add("physical sector $($opm.PhysicalSectorSize) <> $($disk.PhysicalSectorSize)") }
    if ($opm.Style -ne $layout.Style) { $problems.Add("style $($opm.Style) <> $($layout.Style)") }
    if ($layout.Style -eq 'GPT' -and [guid]$opm.GptDiskId -ne [guid]$disk.Guid) { $problems.Add("GPT disk id $($opm.GptDiskId) <> $($disk.Guid)") }
    if ($layout.Style -eq 'MBR' -and $opm.MbrSignature -ne $disk.Signature) { $problems.Add("MBR signature $($opm.MbrSignature) <> $($disk.Signature)") }
    if (-not ($opm.Issues | Where-Object Code -eq 'disk.read-only')) { $problems.Add('read-only disk was not flagged') }

    $expected = @(Get-Partition -DiskNumber $DiskNumber | Sort-Object Offset)
    $actual = @($opm.Partitions | Sort-Object Offset)
    if ($actual.Count -ne $expected.Count) {
        $problems.Add("partition count $($actual.Count) <> $($expected.Count)")
    }
    else {
        for ($i = 0; $i -lt $expected.Count; $i++) {
            $e = $expected[$i]; $a = $actual[$i]; $tag = "partition $($e.PartitionNumber)"
            if ($a.Offset -ne $e.Offset) { $problems.Add("$tag offset $($a.Offset) <> $($e.Offset)") }
            if ($a.Size -ne $e.Size) { $problems.Add("$tag size $($a.Size) <> $($e.Size)") }
            if ($layout.Style -eq 'GPT') {
                if ([guid]$a.PartitionGuid -ne [guid]$e.Guid) { $problems.Add("$tag GUID $($a.PartitionGuid) <> $($e.Guid)") }
                if ([guid]$a.GptType -ne [guid]$e.GptType) { $problems.Add("$tag type $($a.GptType) <> $($e.GptType)") }
            }
            else {
                if ($a.MbrType -ne $e.MbrType) { $problems.Add("$tag MBR type $($a.MbrType) <> $($e.MbrType)") }
            }
            $volume = $e | Get-Volume -ErrorAction SilentlyContinue
            if ($volume) {
                if (-not $a.Volume) { $problems.Add("$tag has a volume but opm found none") }
                else {
                    if ($a.Volume.FileSystem -ne $volume.FileSystem) { $problems.Add("$tag FS $($a.Volume.FileSystem) <> $($volume.FileSystem)") }
                    if ($a.Volume.Label -ne $volume.FileSystemLabel) { $problems.Add("$tag label '$($a.Volume.Label)' <> '$($volume.FileSystemLabel)'") }
                    if ($a.Volume.Path -ne $volume.Path.ToLowerInvariant()) { $problems.Add("$tag volume path $($a.Volume.Path) <> $($volume.Path)") }
                }
            }
        }
    }

    # Free space: the largest extent must match what Windows reports, within the 1 MiB alignment trim.
    $largest = ($opm.FreeExtents | Measure-Object -Property Size -Maximum).Maximum
    if ($null -eq $largest) { $largest = 0 }
    if ([Math]::Abs($largest - $disk.LargestFreeExtent) -gt 1MB) {
        $problems.Add("largest free extent $largest <> Windows $($disk.LargestFreeExtent)")
    }

    if ($problems.Count) { throw "${LayoutName}: $($problems -join '; ')" }
}

function Test-DiscoveryMatchesWindowsForEveryLayout {
    if (-not $env:OPM_HARNESS_INTEGRATION) { Skip-Test 'integration tests not requested (-Integration)' }
    if (-not (Test-HarnessHostReady)) { Skip-Test 'needs an elevated session with the Hyper-V module' }

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($layout in Get-TestLayout) {
        $path = Join-Path $env:OPM_HARNESS_OUT "discovery-$($layout.Name)-$([guid]::NewGuid().ToString('N').Substring(0, 8)).vhdx"
        try {
            New-TestDisk -Layout $layout.Name -Path $path -Seed 7 | Out-Null
            $vhd = Mount-VHD -Path $path -ReadOnly -NoDriveLetter -Passthru
            try {
                Compare-DiscoveryWithWindows -DiskNumber $vhd.DiskNumber -LayoutName $layout.Name
            }
            finally {
                Dismount-VHD -Path $path
            }
        }
        catch {
            $failures.Add($_.Exception.Message)
        }
        finally {
            Remove-Item $path, "$path.manifest.json" -ErrorAction SilentlyContinue
        }
    }
    if ($failures.Count) { throw ($failures -join "`n") }
}

function Test-ListRunsOnThisMachine {
    $output = & (Get-OpmPath) list --json
    if ($LASTEXITCODE) { throw "opm list failed: $output" }
    $snapshot = ($output -join "`n") | ConvertFrom-Json
    if (-not $snapshot.Disks.Count) { throw 'opm list found no disks.' }
}
