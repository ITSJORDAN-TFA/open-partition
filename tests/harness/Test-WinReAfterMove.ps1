#Requires -Version 7.2
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    VM test for issue #11: move a guest's Windows Recovery partition, re-register WinRE, and
    boot into WinRE. Run on a Hyper-V host against a disposable Gen 1 or Gen 2 test VM.

.DESCRIPTION
    The engine's write gate refuses real disks inside a Hyper-V guest, and must not be
    weakened. So the move itself runs on the host, offline, with the guest's VHDX mounted as a
    file-backed virtual disk (which the gate allows), through `opm move` (planner, classifier,
    engine). reagentc only ever runs inside the guest. The WinRE steps are the ones WinReHook
    runs around a move in the product:

      1. Guest: `reagentc /info` must show WinRE Enabled on the Recovery partition right after
         C: (the boxed-in shape). Then `reagentc /disable`; Winre.wim must be back in
         C:\Windows\System32\Recovery.
      2. Shut the VM down. Host: grow the VHDX by -GrowMB, mount it, `opm move <disk>
         @<recovery offset> --to end --yes`, check type/GUID/attributes, optionally `opm extend`
         C: (-ExtendC), dismount.
      3. Guest: `reagentc /enable`; `reagentc /info` must show Enabled on the partition that is
         now at the new offset.
      4. Guest: `reagentc /boottore`, restart. The VM must stay out of Windows (no PowerShell
         Direct, heartbeat lost) and a console screenshot is saved: it should show WinRE's
         "Choose an option" screen. The VM is then reset; the one-time WinRE boot entry means it
         boots Windows again, and `reagentc /info` must still show Enabled at the new location.

    A copy of the VHDX is kept in -WorkDir before anything changes (restore it by copying it
    back with the VM off). The VM must have no checkpoints (the VHDX is resized). MBR (Gen 1):
    the guest's own MountedDevices entry for the recovery volume is not rewritten, because the
    move runs on the host; WinRE doesn't need it (the volume has no drive letter, and reagentc
    /enable finds the partition itself).

    Not run by build.ps1. Needs: an elevated pwsh 7 on a Hyper-V host, the solution built
    (opm.exe, opm-engine.exe), a guest with PowerShell Direct (Windows Server 2016+ / Windows
    10+) and an administrator credential for it.

.EXAMPLE
    $cred = Get-Credential
    ./tests/harness/Test-WinReAfterMove.ps1 -VMName opm-gen2 -Credential $cred -IUnderstandThisChangesTheVm -ExtendC
#>
param(
    [Parameter(Mandatory)] [string] $VMName,
    [Parameter(Mandatory)] [pscredential] $Credential,
    # The script changes the VM's disk; say you know it is a disposable test VM.
    [Parameter(Mandatory)] [switch] $IUnderstandThisChangesTheVm,
    [int] $GrowMB = 1024,
    [switch] $ExtendC,
    [string] $WorkDir = (Join-Path $PSScriptRoot 'out'),
    [int] $WinReWaitSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OpenPartition.Harness.psm1') -Force
. (Join-Path $PSScriptRoot 'suites\Engine.Tests.ps1')

$recoveryGpt = 'de94bba4-06d1-4d40-a16a-bfd50179d6ac'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
$report = [ordered] @{ VMName = $VMName; Started = (Get-Date).ToString('o'); Steps = [System.Collections.Generic.List[object]]::new() }
function Step([string] $Name, $Detail) {
    $report.Steps.Add([ordered] @{ Step = $Name; Detail = $Detail })
    Write-Host "[ok] $Name  $($Detail | ConvertTo-Json -Compress -Depth 4)" -ForegroundColor Green
}

# Runs in the guest (Windows PowerShell 5.1 on Server 2016): reagentc /info, parsed like WinReProbe.
$guestInfo = {
    $out = (& reagentc.exe /info) -join "`n"
    $status = if ($out -match '(?m)^\s*Windows RE status:\s*(\S+)') { $Matches[1] } else { 'Unknown' }
    $disk = $null; $partition = $null
    if ($out -match '(?mi)^\s*Windows RE location:\s*\\\\\?\\GLOBALROOT\\device\\harddisk(\d+)\\partition(\d+)') {
        $disk = [int]$Matches[1]; $partition = [int]$Matches[2]
    }
    $c = Get-Partition -DriveLetter C
    [pscustomobject] @{
        Status = $status; Disk = $disk; Partition = $partition; Raw = $out
        OsDisk = $c.DiskNumber; COffset = $c.Offset; CSize = $c.Size
        Partitions = @(Get-Partition -DiskNumber $c.DiskNumber | Select-Object PartitionNumber, Offset, Size, GptType, MbrType)
        StagedImage = Test-Path -LiteralPath "$env:SystemRoot\System32\Recovery\Winre.wim"
    }
}

function Get-GuestWinRe { Invoke-InGuest -VMName $VMName -Credential $Credential -ScriptBlock $guestInfo }

function Save-Screenshot([string] $Path) {
    $ns = 'root\virtualization\v2'
    $vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$VMName'"
    $settings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData |
        Where-Object VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized'
    $service = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
    $r = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $settings; WidthPixels = [uint16]800; HeightPixels = [uint16]600 }
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new(800, 600, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
    $data = $bitmap.LockBits([System.Drawing.Rectangle]::new(0, 0, 800, 600), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
    [System.Runtime.InteropServices.Marshal]::Copy([byte[]]$r.ImageData, 0, $data.Scan0, $r.ImageData.Length)
    $bitmap.UnlockBits($data)
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# --- 0. Preconditions -----------------------------------------------------------------------
$vm = Get-VM -Name $VMName
if (Get-VMSnapshot -VMName $VMName) { throw "VM '$VMName' has checkpoints; its disk can't be resized. Use a VM without checkpoints." }
$drive = Get-VMHardDiskDrive -VMName $VMName | Sort-Object ControllerType, ControllerNumber, ControllerLocation | Select-Object -First 1
$vhdx = $drive.Path
if ($vm.State -ne 'Running') { Start-VM -Name $VMName }
Wait-GuestReady -VMName $VMName -Credential $Credential

# --- 1. Before: WinRE enabled on the recovery partition right after C:, then turn it off ----
$before = Get-GuestWinRe
if ($before.Status -ne 'Enabled') { throw "WinRE is '$($before.Status)' in the guest; it must be Enabled.`n$($before.Raw)" }
$recovery = $before.Partitions | Where-Object PartitionNumber -eq $before.Partition
if ($before.Disk -ne $before.OsDisk -or -not $recovery) { throw "WinRE is not on a partition of the OS disk: $($before.Raw)" }
$isRecovery = ("$($recovery.GptType)".Trim('{}') -eq $recoveryGpt) -or ($recovery.MbrType -eq 0x27)
if (-not $isRecovery) { throw "WinRE is on partition $($recovery.PartitionNumber), which is not a Recovery partition." }
if ($recovery.Offset -ne $before.COffset + $before.CSize) {
    Write-Warning "The recovery partition is not directly after C: (C: ends at $($before.COffset + $before.CSize), recovery starts at $($recovery.Offset)). Testing anyway."
}
Step 'WinRE enabled before the move' @{ Partition = $recovery.PartitionNumber; Offset = $recovery.Offset; Size = $recovery.Size }

Invoke-InGuest -VMName $VMName -Credential $Credential -ScriptBlock { & reagentc.exe /disable | Out-Null } | Out-Null
$disabled = Get-GuestWinRe
if ($disabled.Status -ne 'Disabled' -or -not $disabled.StagedImage) { throw "reagentc /disable did not leave WinRE disabled with Winre.wim staged on C:.`n$($disabled.Raw)" }
Step 'reagentc /disable' @{ StagedImage = $disabled.StagedImage }

# --- 2. Host: move the recovery partition to the end of the grown disk, offline --------------
Stop-VM -Name $VMName
$backup = Join-Path $WorkDir "$VMName-$stamp-before.vhdx"
Copy-Item -LiteralPath $vhdx -Destination $backup
Step 'Backup of the VM disk' @{ Path = $backup }
$size = (Get-VHD -Path $vhdx).Size
Resize-VHD -Path $vhdx -SizeBytes ($size + [long]$GrowMB * 1MB)
$mounted = Mount-VHD -Path $vhdx -NoDriveLetter -Passthru
try {
    $hostDisk = $mounted.DiskNumber
    if ((Get-Disk -Number $hostDisk).BusType -ne 'File Backed Virtual') { throw "Mounted disk $hostDisk is not a file-backed virtual disk." }
    Update-Disk -Number $hostDisk
    $hostRecovery = Get-Partition -DiskNumber $hostDisk | Where-Object Offset -eq $recovery.Offset
    $show = (Invoke-Opm @('show', $hostDisk, '--json')).Output -join "`n" | ConvertFrom-Json
    $opmRecovery = $show.Partitions | Where-Object Offset -eq $recovery.Offset
    $r = Invoke-Opm @('move', $hostDisk, "@$($recovery.Offset)", '--to', 'end', '--yes')
    if ($r.ExitCode -ne 0) { throw "opm move failed: $($r.Output -join ' | ')" }
    if ($r.Output -match 'reagentc') { throw "opm ran the host's WinRE hook for the guest's disk: $($r.Output -join ' | ')" }
    $moved = Get-Partition -DiskNumber $hostDisk | Where-Object { $_.Guid -eq $hostRecovery.Guid -and $_.Size -eq $hostRecovery.Size } | Select-Object -First 1
    if ($before.Partitions[0].GptType) {
        $after = (Invoke-Opm @('show', $hostDisk, '--json')).Output -join "`n" | ConvertFrom-Json
        $movedOpm = $after.Partitions | Where-Object { [guid]$_.PartitionGuid -eq [guid]$hostRecovery.Guid }
        if ([guid]$movedOpm.GptType -ne [guid]$recoveryGpt) { throw "Recovery type lost: $($movedOpm.GptType)" }
        if ($movedOpm.GptAttributes -ne $opmRecovery.GptAttributes) { throw "Recovery attributes changed: $($movedOpm.GptAttributes) (were $($opmRecovery.GptAttributes))" }
    }
    else {
        $moved = Get-Partition -DiskNumber $hostDisk | Where-Object { $_.MbrType -eq 0x27 -and $_.Size -eq $hostRecovery.Size } | Select-Object -First 1
    }
    if (-not $moved -or $moved.Offset -le $recovery.Offset) { throw 'The recovery partition did not move right.' }
    $newOffset = $moved.Offset
    Step 'opm move (host, offline)' @{ From = $recovery.Offset; To = $newOffset; Output = @($r.Output | Where-Object { $_ -notmatch '%' }) }
    if ($ExtendC) {
        $r = Invoke-Opm @('extend', $hostDisk, "@$($before.COffset)", '--yes')
        if ($r.ExitCode -ne 0) { throw "opm extend C: failed: $($r.Output -join ' | ')" }
        Step 'opm extend C: (host, offline)' @{ Output = @($r.Output) }
    }
}
finally {
    Dismount-VHD -Path $vhdx
}

# --- 3. Guest: turn WinRE back on, check it is on the moved partition -----------------------
Start-VM -Name $VMName
Wait-GuestReady -VMName $VMName -Credential $Credential
$enableOutput = Invoke-InGuest -VMName $VMName -Credential $Credential -ScriptBlock { (& reagentc.exe /enable) -join ' ' }
$enabled = Get-GuestWinRe
$expected = $enabled.Partitions | Where-Object Offset -eq $newOffset
if ($enabled.Status -ne 'Enabled' -or $enabled.Disk -ne $enabled.OsDisk -or $enabled.Partition -ne $expected.PartitionNumber) {
    throw "After reagentc /enable ($enableOutput), WinRE is not Enabled on the moved partition $($expected.PartitionNumber):`n$($enabled.Raw)"
}
Step 'reagentc /enable: Enabled at the new location' @{ Partition = $enabled.Partition; Offset = $newOffset; Info = $enabled.Raw }

# --- 4. Boot into WinRE ---------------------------------------------------------------------
Invoke-InGuest -VMName $VMName -Credential $Credential -ScriptBlock { & reagentc.exe /boottore | Out-Null; Restart-Computer -Force } -ErrorAction SilentlyContinue | Out-Null
Start-Sleep -Seconds 20
$deadline = (Get-Date).AddSeconds($WinReWaitSeconds)
while ((Get-Date) -lt $deadline) {
    $reachable = $true
    try { Invoke-Command -VMName $VMName -Credential $Credential -ScriptBlock { $true } -ErrorAction Stop | Out-Null } catch { $reachable = $false }
    if ($reachable) { throw 'The guest booted back into Windows instead of WinRE after reagentc /boottore.' }
    Start-Sleep -Seconds 10
}
$heartbeat = (Get-VMIntegrationService -VMName $VMName -Name Heartbeat).PrimaryStatusDescription
$screenshot = Join-Path $WorkDir "$VMName-$stamp-winre.png"
Save-Screenshot $screenshot
Step 'Booted into WinRE (Windows stayed unreachable)' @{ Seconds = $WinReWaitSeconds; Heartbeat = $heartbeat; Screenshot = $screenshot }

Stop-VM -Name $VMName -TurnOff
Start-VM -Name $VMName
Wait-GuestReady -VMName $VMName -Credential $Credential
$final = Get-GuestWinRe
if ($final.Status -ne 'Enabled' -or $final.Partition -ne $expected.PartitionNumber) { throw "After leaving WinRE, reagentc /info shows:`n$($final.Raw)" }
Step 'Back in Windows; WinRE still enabled at the new location' @{ Partition = $final.Partition }

$report.Finished = (Get-Date).ToString('o')
$file = Join-Path $WorkDir "$VMName-$stamp-winre-report.json"
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $file -Encoding utf8
Write-Host "PASSED. Check the screenshot shows WinRE ('Choose an option'): $screenshot" -ForegroundColor Green
Write-Host "Report: $file. The pre-test disk copy is $backup (delete it when done)."
