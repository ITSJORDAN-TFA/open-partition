#Requires -Version 7.2
<#
.SYNOPSIS
    Builds the self-contained package: artifacts/openpartition-<version>-win-x64.zip (and .sha256).

.DESCRIPTION
    - opm-engine.exe: Rust release build with the C runtime linked statically, so it runs
      on a clean Windows Server without the Visual C++ redistributable.
    - opm.exe and OpenPartition.exe: .NET 10 self-contained single-file publishes, so no
      .NET runtime is needed on the target.
    - README.txt and Test-OpmOnThisMachine.ps1 from this folder.
#>
param(
    [string] $Runtime = 'win-x64',
    # Defaults to the version in ProductInfo.cs; CI passes e.g. 0.1.0-preview.42 for main builds.
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$version = if ($Version) { $Version } else { ([regex]::Match((Get-Content (Join-Path $root 'src/dotnet/OpenPartition.Model/ProductInfo.cs') -Raw), 'Version = "([^"]+)"')).Groups[1].Value }
$name = "openpartition-$version-$Runtime"
$artifacts = Join-Path $root 'artifacts'
# Stage outside artifacts/, so the unzipped folder is never run in place by mistake.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) $name
$zip = Join-Path $artifacts "$name.zip"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host '==> opm-engine (Rust, static CRT)' -ForegroundColor Cyan
$targetDir = Join-Path $root 'src/engine/target/package'
$previous = $env:RUSTFLAGS
try {
    $env:RUSTFLAGS = '-C target-feature=+crt-static'
    cargo build --release -p opm-engine-cli --manifest-path (Join-Path $root 'src/engine/Cargo.toml') --target-dir $targetDir
    if ($LASTEXITCODE) { throw 'cargo build failed' }
}
finally {
    $env:RUSTFLAGS = $previous
}
Copy-Item (Join-Path $targetDir 'release/opm-engine.exe') $stage

foreach ($project in 'OpenPartition.Cli', 'OpenPartition.Gui') {
    Write-Host "==> $project (self-contained single file)" -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src/dotnet/$project") -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        -o $stage --nologo -v minimal
    if ($LASTEXITCODE) { throw "dotnet publish $project failed" }
}

Copy-Item (Join-Path $PSScriptRoot 'README.txt'), (Join-Path $PSScriptRoot 'Test-OpmOnThisMachine.ps1') $stage
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')

if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content "$zip.sha256" "$hash  $(Split-Path $zip -Leaf)"

Write-Host ''
Get-ChildItem $stage | Format-Table Name, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } } -AutoSize | Out-Host
Write-Host "Package: $zip ($([Math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "SHA-256: $hash"
