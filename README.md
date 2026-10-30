# Open Partition Manager

A free, open-source tool for Windows Server administrators to view, resize and move partitions,
including on Azure VMs.

> **Status: read-only preview (Phase 1).** `opm list` / `opm show` and the GUI show disks,
> partitions, free space and everything the tool would refuse or warn about. Nothing here
> changes disks yet. See
> [`.spec/plan.md`](.spec/plan.md) for the plan and [`.spec/handover.md`](.spec/handover.md) for
> the original brief.

**No warranty.** This software is provided "as is" (see [LICENSE](LICENSE)). Partition changes
can destroy data. Always take a backup or snapshot first.

## Layout

| Path | What |
|------|------|
| `src/engine/` | Rust engine. `opm-core` is `no_std`: block device trait, crash-safety journal and the journaled block mover. `opm-platform-sim` is the simulated power-loss disk and crash campaign runner. `opm-platform-win32` and `opm-ffi` are placeholders for now. |
| `src/dotnet/` | .NET 10: model, read-only discovery, operations (empty), Windows service (stub), `opm` CLI, WPF GUI (`OpenPartition.exe`). |
| `tests/dotnet/` | xUnit tests. |
| `tests/harness/` | PowerShell + Hyper-V harness: disk layout catalogue, VHDX builder with deterministic data and raw SHA-256 manifests, guest control, hard-stop fault injection. |

## Building

Requirements: PowerShell 7.2+, .NET 10 SDK, Rust stable (MSVC) with clippy.

```powershell
./build.ps1                 # build everything, run all fast tests
./build.ps1 -Integration    # also build real VHDX disks (elevated session with Hyper-V)
```

## Using the preview

```powershell
opm list            # every disk, partition and free extent
opm show 2          # one disk in detail: GUIDs, attributes, BitLocker, every refusal
opm list --json     # machine-readable
```

Run from an elevated prompt; without elevation BitLocker status is unknown and moves would be refused.

## Supported targets

Windows Server 2016–2025 and Windows 10/11, on physical machines, Hyper-V and Azure. The GUI
needs the Desktop Experience; on Server Core use the `opm` CLI.
