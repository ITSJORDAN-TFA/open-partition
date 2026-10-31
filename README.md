# Open Partition Manager

<img src="assets/brand/open-partition-128.png" alt="" width="96" align="right" />

A free, open-source tool for Windows Server administrators to view, resize and move partitions,
including on Azure VMs.

> **Status: preview.** The GUI and `opm` show disks, partitions, free space and everything the
> tool would refuse or warn about, and can move partitions (crash-safely, journaled) and extend
> volumes. Writes are limited to test virtual disks (VHD/VHDX) and, after a snapshot, real disks
> on Azure VMs; see the [compatibility matrix](#compatibility-matrix). See [`.spec/plan.md`](.spec/plan.md) for the
> plan and [`.spec/handover.md`](.spec/handover.md) for the original brief.

## Download

Everything is published from this repository; there is nothing else to install from.

- **Releases:** [Releases page](../../releases). Each version has
  `openpartition-<version>-win-x64.zip` and its `.sha256`. Unzip anywhere and run
  `OpenPartition.exe` (GUI) or `opm.exe` (CLI) from an elevated prompt. They're self-contained,
  so no .NET runtime or Visual C++ redistributable is needed.
- **Latest `main` build:** the rolling [`preview`](../../releases/tag/preview) pre-release,
  rebuilt by CI on every push to `main`. For testing only.

Check the download before running it:

```powershell
(Get-FileHash .\openpartition-0.1.0-win-x64.zip).Hash   # compare with the .sha256 file
```

The binaries are not code-signed yet, so Windows SmartScreen may warn the first time.

**No warranty.** This software is provided "as is" (see [LICENSE](LICENSE)). Partition changes
can destroy data. Always take a backup or snapshot first.

## Compatibility matrix

Last updated 2026-10-31. Only what has actually been run is marked as tested.

| Mark | Meaning |
|------|---------|
| ✅ | Tested and working |
| 🟡 | Expected to work, not tested yet |
| 🚧 | In progress |
| ⛔ | Refused by design: the tool will not do this, and says why |
| ❌ | Not supported yet |

### Where it can write

Reading is allowed everywhere. Writing is limited to the environments below, and is enforced by
the engine itself, not just the GUI.

| Environment | View disks | Change disks |
|-------------|:----------:|:------------:|
| Test virtual disks (VHD/VHDX attached on any machine) | ✅ | ✅ |
| Azure VM, after you name a snapshot of the disk | ✅ | 🟡 ready, first real-disk run pending |
| Hyper-V guest | 🟡 | ⛔ for now |
| Physical machine | ✅ | ⛔ for now |
| Other hypervisors (VMware, KVM, ...) | 🟡 | ⛔ for now |

### Windows versions

| Windows | View disks | Move partitions (tested on VHDX) | Notes |
|---------|:----------:|:--------------------------------:|-------|
| Windows Server 2016 (1607) | ✅ | ✅ | Azure field tests, including kill-and-recover and the MBR extended-partition round |
| Windows Server 2019 | 🟡 | 🟡 | |
| Windows Server 2022 | 🟡 | 🟡 | |
| Windows Server 2025 | 🟡 | 🟡 | |
| Windows 10 | 🟡 | 🟡 | |
| Windows 11 | ✅ | ✅ | Development machine; full harness |
| Server Core | 🟡 CLI only | 🟡 CLI only | The GUI needs the Desktop Experience |

### Disks and partitions

| Disk or partition | Move | Extend | Notes |
|-------------------|:----:|:------:|-------|
| GPT basic disk | ✅ | 🟡 | Volume GUID and drive letter kept |
| MBR basic disk, primary partition | ✅ | ✅ | Drive letter and volume GUID kept (MountedDevices updated) |
| MBR extended partition, with its logical drives | ✅ | ✅ | Moves as one block; every logical drive keeps its letter and GUID. Extend grows the container into free space after it (no data moves) |
| MBR logical drive on its own | ⛔ | ✅ last one only | Move the extended partition that holds it. Only the last logical drive can grow (into its container); grow the container first |
| 512-byte and 4K-native sectors (512n, 512e, 4Kn) | ✅ | 🟡 | |
| Partition followed by free space (e.g. C:) | | ✅ | Uses Windows' own resize, which is safe on a running system |
| Windows (boot) partition, C: | ⛔ | 🟡 | Can't be moved while Windows runs; it can be extended |
| System / EFI / MSR / recovery partitions | ⛔ | ⛔ | Never changed |
| Volume with a page file or hibernation file | ⛔ | 🟡 | Move the page file elsewhere first |
| BitLocker-encrypted volume | ⛔ | 🟡 | Decrypt before moving |
| Volume in use (open files, services) | ⛔ until released | ✅ | Never forced closed; stop whatever uses it. Remote Desktop drive redirection on the machine running the tool holds new drive letters open |
| Dynamic disks, Storage Spaces, Storage Replica, clustered disks | ⛔ | ⛔ | Use the native tools |

### Operations

| Operation | CLI | GUI |
|-----------|:---:|:---:|
| View disks, partitions, free space and refusal reasons | ✅ `opm` | ✅ |
| Move a partition (journaled, survives a crash or kill) | ✅ `opm move` | ✅ |
| Move an MBR extended partition with its logical drives | ✅ `opm move <disk> extended` | ✅ |
| Extend a volume into free space after it | ✅ `opm extend` | ✅ |
| Queue several operations, preview, then apply | one at a time | ✅ |
| Resume an interrupted move | ✅ `opm resume` | 🟡 |
| Undo an interrupted move (if it never overwrote its original location) | ✅ `opm undo` | 🟡 |
| Stop and undo by itself if the data changes under a move | ✅ | ✅ |
| Copy only the space NTFS uses (free clusters are skipped) | ✅ | ✅ |
| Merge repeated moves or extends of the same partition in the plan | — | ✅ |
| Shrink, create, delete, format | ❌ | ❌ |
| Move the Windows partition (boot-time mode) | ❌ | ❌ |

## Layout

| Path | What |
|------|------|
| `src/engine/` | Rust engine. `opm-core` is `no_std`: block device trait, crash-safety journal and the journaled block mover. `opm-platform-sim` is the simulated power-loss disk and crash campaign runner. `opm-platform-win32` does raw disk I/O, volume locking, partition table updates and MountedDevices fix-ups. `opm-engine-cli` is the `opm-engine` command. |
| `src/dotnet/` | .NET 10: model, read-only discovery, operations, Windows service (stub), `opm` CLI, WPF GUI (`OpenPartition.exe`). |
| `tests/dotnet/` | xUnit tests. |
| `tests/harness/` | PowerShell + Hyper-V harness: disk layout catalogue, VHDX builder with deterministic data and raw SHA-256 manifests, engine move scenarios, guest control, hard-stop fault injection. |
| `tools/package/` | Preview package and the on-machine self-test (`Test-OpmOnThisMachine.ps1`). |

## Building

Requirements: PowerShell 7.2+, .NET 10 SDK, Rust stable (MSVC) with clippy.

```powershell
./build.ps1                 # build everything, run all fast tests
./build.ps1 -Integration    # also build real VHDX disks (elevated session with Hyper-V)
```

## Releasing

CI (`.github/workflows/ci.yml`) builds, tests and packages every push and pull request. Pushes to
`main` also refresh the `preview` pre-release. To publish a version, bump `Version` in
`src/dotnet/OpenPartition.Model/ProductInfo.cs` (and `src/engine/Cargo.toml`), commit, then tag:

```powershell
git tag v0.1.0
git push origin v0.1.0     # CI checks the tag matches ProductInfo and publishes the release
```

## Using the preview

```powershell
opm list            # every disk, partition and free extent
opm show 2          # one disk in detail: GUIDs, attributes, BitLocker, every refusal
opm list --json     # machine-readable
opm move 0 extended --to end --snapshot my-snap      # dry run: shows the plan, changes nothing
opm move 0 extended --to end --snapshot my-snap --yes
opm extend 0 C: --snapshot my-snap --yes
opm resume --yes    # finish a move that was interrupted
opm undo --yes      # or put it back where it was (when the move never overwrote its source)
```

Run from an elevated prompt; without elevation BitLocker status is unknown and moves would be refused.

To try the engine safely on any machine, run `Test-OpmOnThisMachine.ps1` from the preview
package. It builds throwaway VHDX disks, moves partitions on them, and deletes them afterwards.
