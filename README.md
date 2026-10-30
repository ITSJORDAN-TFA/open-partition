# Open Partition Manager

A free, open-source tool for Windows Server administrators to view, resize and move partitions,
including on Azure VMs.

> **Status: Phase 0 (foundations).** Nothing here changes disks yet. See
> [`.spec/plan.md`](.spec/plan.md) for the plan and [`.spec/handover.md`](.spec/handover.md) for
> the original brief.

**No warranty.** This software is provided "as is" (see [LICENSE](LICENSE)). Partition changes
can destroy data. Always take a backup or snapshot first.

## Layout

| Path | What |
|------|------|
| `src/engine/` | Rust engine. `opm-core` is `no_std` (block device trait, later the journal and chunk scheduler); `opm-platform-sim` is the simulated power-loss disk and crash campaign runner; `opm-platform-win32` and `opm-ffi` are placeholders for now. |
| `src/dotnet/` | .NET 10: model, discovery, operations, Windows service, `opm` CLI, WPF GUI. |
| `tests/dotnet/` | xUnit tests. |
| `tests/harness/` | PowerShell + Hyper-V harness: disk layout catalogue, VHDX builder with deterministic data and raw SHA-256 manifests, guest control, hard-stop fault injection. |

## Building

Requirements: PowerShell 7.2+, .NET 10 SDK, Rust stable (MSVC) with clippy.

```powershell
./build.ps1                 # build everything, run all fast tests
./build.ps1 -Integration    # also build real VHDX disks (elevated session with Hyper-V)
```

## Supported targets

Windows Server 2016–2025 and Windows 10/11, on physical machines, Hyper-V and Azure. The GUI
needs the Desktop Experience; on Server Core use the `opm` CLI.
