#Requires -Version 7.2
<#
.SYNOPSIS
    Builds the self-contained preview package: artifacts/opm-preview-<version>-win-x64.zip.

.DESCRIPTION
    - opm-engine.exe: Rust release build with the C runtime linked statically, so it runs
      on a clean Windows Server without the Visual C++ redistributable.
    - opm.exe and OpenPartition.exe: .NET 10 self-contained single-file publishes, so no
      .NET runtime is needed on the target.
    - README.txt and Test-OpmOnThisMachine.ps1 from this folder.
#>
param(
    [string] $Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$version = ([regex]::Match((Get-Content (Join-Path $root 'src/dotnet/OpenPartition.Model/ProductInfo.cs') -Raw), 'Version = "([^"]+)"')).Groups[1].Value
$name = "opm-preview-$version-$Runtime"
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts $name
$zip = "$stage.zip"

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
