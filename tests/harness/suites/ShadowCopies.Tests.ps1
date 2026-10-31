# Run only inside a disposable Windows guest with Hyper-V management tools.
function Test-ShadowCopyRefusalRemediationAndMove {
    if (-not $env:OPM_HARNESS_INTEGRATION) { Skip-Test 'integration tests not requested' }
    if ($env:OPM_DISPOSABLE_VSS_GUEST -ne '1') { Skip-Test 'VSS creation requires an explicitly disposable guest (OPM_DISPOSABLE_VSS_GUEST=1); never run on the host' }
    if (-not (Test-HarnessHostReady)) { Skip-Test 'requires elevation and Hyper-V tools' }
    $cli = Join-Path $PSScriptRoot '../../../src/dotnet/OpenPartition.Cli/bin/Debug/net10.0-windows10.0.17763.0/opm.exe'
    $path = Join-Path $env:OPM_HARNESS_OUT "shadows-$([guid]::NewGuid().ToString('N')).vhdx"
    try {
        New-TestDisk -Layout gpt-512n-single -Path $path -Seed 31 | Out-Null
        $vhd = Mount-VHD -Path $path -NoDriveLetter -Passthru
        $disk = $vhd.DiskNumber
        $part = Get-Partition -DiskNumber $disk | Where-Object Type -eq 'Basic' | Select-Object -First 1
        $volume = $part | Get-Volume
        $file = $volume.Path + 'shadow-move-proof.txt'
        Set-Content -LiteralPath $file -Value 'shadow remediation move proof'
        $hash = (Get-FileHash -LiteralPath $file).Hash
        $created = Invoke-CimMethod -Namespace root/cimv2 -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{ Volume = $volume.Path; Context = 'ClientAccessible' }
        if ($created.ReturnValue -ne 0) { throw "VSS creation failed: $($created.ReturnValue)" }
        $state = (& $cli show $disk --json | Out-String | ConvertFrom-Json)
        $p = $state.Partitions | Where-Object Offset -eq $part.Offset
        if (-not $p.Volume.HasShadowCopies) { throw 'Shadow copies not discovered' }
        & $cli move $disk "@$($part.Offset)" --to end
        if ($LASTEXITCODE -eq 0) { throw 'Move was not refused' }
        & $cli shadows $disk "@$($part.Offset)" --delete --yes --confirm-volume yes
        if ($LASTEXITCODE -eq 0) { throw 'Unnamed confirmation accepted' }
        if (-not (Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq $created.ShadowID)) { throw 'Unconfirmed deletion removed snapshot' }
        & $cli shadows $disk "@$($part.Offset)" --delete --yes --confirm-volume $p.Volume.Path
        if ($LASTEXITCODE) { throw 'Confirmed remediation failed' }
        & $cli move $disk "@$($part.Offset)" --to end --yes
        if ($LASTEXITCODE) { throw 'Move still refused or failed after deletion; inspect remaining storage associations/provider restrictions' }
        if ((Get-FileHash -LiteralPath $file).Hash -ne $hash) { throw 'Data changed after move' }
    }
    finally {
        # Detach only our own throwaway VHDX, including any residual guest snapshots.
        if (Test-Path $path) { Dismount-VHD -Path $path -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "$path.manifest.json" -ErrorAction SilentlyContinue
    }
}
