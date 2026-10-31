#Requires -Version 5.1
<#
.SYNOPSIS
    Drives the GUI with UI Automation through the whole "free the space after a boxed-in
    partition, then extend it" flow on a throwaway VHDX, then checks the result.

.DESCRIPTION
    Builds the mbr-512n-logical-boxed-in layout (SYS, PRIM, an extended partition with three
    logical drives, free space at the end), starts OpenPartition.exe, and clicks: extended
    partition -> Move -> End of the free space; PRIM -> Move -> End; SYS -> Extend; Apply.
    Then checks partition positions, every file, and every volume GUID. A screenshot of the
    planned preview is saved next to the VHDX.

    Run elevated from Windows PowerShell 5.1 or PowerShell 7 with the engine and GUI built.
#>
param(
    [string] $WorkDir = $(if ($env:OPM_HARNESS_WORKDIR) { $env:OPM_HARNESS_WORKDIR } else { Join-Path $PSScriptRoot 'out' }),
    [string] $Screenshot = (Join-Path $WorkDir 'gui-plan-preview.png')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Import-Module (Join-Path $PSScriptRoot 'OpenPartition.Harness.psm1') -Force

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$gui = Get-ChildItem (Join-Path $root 'src\dotnet\OpenPartition.Gui\bin') -Recurse -Filter OpenPartition.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$engine = Get-ChildItem (Join-Path $root 'src\engine\target') -Recurse -Filter opm-engine.exe | Where-Object { $_.Directory.Name -in 'debug', 'release' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $gui -or -not $engine) { throw 'Build the GUI and engine first.' }
$env:OPM_ENGINE_EXE = $engine.FullName
$env:OPM_HARNESS_OUT = $WorkDir
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
function Find-Named($Parent, [string] $Name, [int] $Seconds = 20) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $found = $Parent.FindFirst($Tree::Descendants, $condition)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 250
    }
    throw "UI element '$Name' not found."
}
function Find-Window([string] $Title, [int] $Seconds = 20) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Title)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $w = $AE::RootElement.FindFirst($Tree::Descendants, $condition)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 250
    }
    throw "Window '$Title' not found."
}
function Invoke-Element($Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 300
}
function Click([object] $Parent, [string] $Name) { Invoke-Element (Find-Named $Parent $Name) }

$path = Join-Path $WorkDir "gui-$([guid]::NewGuid().ToString('N').Substring(0, 8)).vhdx"
$process = $null
try {
    $manifest = New-TestDisk -Layout 'mbr-512n-logical-boxed-in' -Path $path -Seed 19
    $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
    $disk = $vhd.DiskNumber
    $labels = @($manifest.Data | ForEach-Object Label)
    $paths = @{}
    foreach ($p in Get-Partition -DiskNumber $disk) {
        $v = $p | Get-Volume -ErrorAction SilentlyContinue
        if ($v) { $paths[$v.FileSystemLabel] = $v.Path }
    }
    $part = @{}
    foreach ($p in Get-Partition -DiskNumber $disk) {
        $v = $p | Get-Volume -ErrorAction SilentlyContinue
        if ($v -and $v.FileSystemLabel) { $part[$v.FileSystemLabel] = $p }
    }

    $process = Start-Process -FilePath $gui.FullName -PassThru
    $main = Find-Window 'Open Partition Manager 0.1.0 — preview' 60
    $extended = Find-Named $main "Disk $disk, extended partition" 60

    Invoke-Element $extended
    Click $main 'Move…'
    $dialog = Find-Window 'Move the extended partition'
    Click $dialog 'End of the free space'
    Click $dialog 'Add to plan'

    Invoke-Element (Find-Named $main "Disk $disk, partition $($part['PRIM'].PartitionNumber) — PRIM")
    Click $main 'Move…'
    $dialog = Find-Window "Move 'PRIM'"
    Click $dialog 'End of the free space'
    Click $dialog 'Add to plan'

    Invoke-Element (Find-Named $main "Disk $disk, partition $($part['SYS'].PartitionNumber) — SYS")
    Click $main 'Extend…'
    $dialog = Find-Window "Extend 'SYS'"
    Click $dialog 'Use all the free space'
    Click $dialog 'Add to plan'

    # Screenshot of the planned preview for the record.
    Start-Sleep -Milliseconds 500
    $r = $main.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($Screenshot, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()

    Click $main 'Apply…'
    $apply = Find-Window 'Apply planned changes'
    Click $apply 'Apply'
    Find-Named $apply 'All 3 change(s) done.' 600 | Out-Null
    Click $apply 'Close'

    $sys = Get-Partition -DiskNumber $disk | Where-Object Offset -eq $part['SYS'].Offset
    $prim = $null
    foreach ($p in Get-Partition -DiskNumber $disk) { $v = $p | Get-Volume -ErrorAction SilentlyContinue; if ($v -and $v.FileSystemLabel -eq 'PRIM') { $prim = $p } }
    if ($sys.Offset + $sys.Size -ne $prim.Offset) { throw "SYS ends at $($sys.Offset + $sys.Size) but PRIM starts at $($prim.Offset)." }
    foreach ($p in Get-Partition -DiskNumber $disk) {
        $v = $p | Get-Volume -ErrorAction SilentlyContinue
        if ($v -and $v.FileSystemLabel -and $v.Path -ne $paths[$v.FileSystemLabel]) { throw "$($v.FileSystemLabel) volume GUID changed." }
    }
    $process | Stop-Process -Force
    $process = $null
    Dismount-VHD -Path $path
    $vhd = Mount-VHD -Path $path -ReadOnly -NoDriveLetter -Passthru
    foreach ($data in $manifest.Data) {
        foreach ($p in Get-Partition -DiskNumber $vhd.DiskNumber) {
            $v = $p | Get-Volume -ErrorAction SilentlyContinue
            if ($v -and $v.FileSystemLabel -eq $data.Label) {
                $problems = @(Test-DeterministicData -Root $v.Path -TargetBytes $data.Bytes -Seed $data.Seed)
                if ($problems.Count) { throw "$($data.Label): $($problems -join '; ')" }
            }
        }
    }
    "PASS: GUI moved the extended partition and PRIM, extended SYS; files identical, volume GUIDs kept. Preview screenshot: $Screenshot"
}
finally {
    if ($process) { $process | Stop-Process -Force -ErrorAction SilentlyContinue }
    Dismount-VHD -Path $path -ErrorAction SilentlyContinue
    Remove-Item $path, "$path.manifest.json" -ErrorAction SilentlyContinue
}
