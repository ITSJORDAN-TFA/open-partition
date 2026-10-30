# Open Partition Manager — Implementation Plan

Based on `.spec/handover.md`. This plan keeps the handover's phases but changes four things:

1. It adds an early **research spike phase**, because three of the open questions change the architecture.
2. It adds a **simulated-disk fault-injection layer**, so crash-safety can be tested thousands of times per run instead of only through ~100 slow VM hard-stops.
3. It **builds the engine alongside Phases 1–2**, because the engine is the critical path.
4. It fixes several **technical gaps** in the handover (section 2) that would otherwise surface in Phase 3 or 6.

---

## 1. Decisions to make now

| # | Decision | Recommendation | Why |
|---|----------|----------------|-----|
> **Status (2026-10-30):** D1 WPF/.NET 10, D2 Rust, D3 MIT, D6 CLI as a first-class option — all **confirmed**. D4 pending spike S1.

| D1 | GUI framework | **WPF on .NET 10 (LTS)** | WinUI 3 / Windows App SDK needs Windows 10 1809+, which rules out Server 2016 (1607). WPF runs on every target OS. .NET 8 support ends Nov 2026, so start on .NET 10. |
| D2 | Engine language | **Rust**: a `no_std + alloc` core crate plus platform crates | Memory safety in the component that writes raw sectors. The core is pure logic (journal, chunk scheduler, layout model), so it stays portable whichever boot-time route D4 picks. |
| D3 | Licence | **GPLv3**, or MIT if adoption by companies and vendors matters more | GPLv3 keeps forks of a data-safety tool open. MIT is easier for companies to bundle. Both work with the no-warranty disclaimer. This one is your call. |
| D4 | Boot-time route for C: | **Decide after spike S1.** Leaning towards WinPE/WinRE RAM-disk boot rather than BootExecute for C: | See gap G4. This decides whether the engine must run under the NT native subsystem at all. |
| D5 | Service ↔ GUI IPC | Named pipe with a **DACL limited to Administrators and SYSTEM**, JSON messages | The service runs as LocalSystem, so it's a privilege boundary. The pipe ACL is a security requirement. |
| D6 | Server Core | CLI is a first-class interface, not an afterthought | Many Azure Windows Server VMs run Server Core, where there's no GUI. |

---

## 2. Technical gaps in the handover

These need to be designed in, not found during testing.

**G1. MBR disks: the drive letter and volume GUID do *not* survive a move automatically.**
On GPT, MountMgr identifies a volume by its partition GUID, so keeping the GUID is enough. On MBR, `HKLM\SYSTEM\MountedDevices` stores **disk signature + partition byte offset** (12 bytes). Changing the offset orphans the drive letter and the `Volume{GUID}`. Phase 3 has to rewrite the matching `MountedDevices` values to the new offset before the layout change. If that isn't reliable, it must at least reassign the letter and mount points afterwards. Spike S2 confirms the behaviour.

**G2. "Never interprets NTFS" needs one narrow exception.**
The NTFS boot sector holds a *hidden sectors* field (BPB offset 0x1C) equal to the partition's starting LBA. Windows ignores it for data volumes, but **BIOS/MBR boot code uses it** to find `bootmgr`. Moving the active system partition on Gen 1 or BIOS machines means patching this field in both the primary boot sector and the backup boot sector at the end of the volume. Keep this as a separate, audited routine that only runs for that case.

**G3. Overlapping moves: avoid the source-backup step entirely.**
If the copy runs in the correct direction and **each chunk is no larger than the shift distance**, a chunk never overlaps its own source. Re-copying the last chunk after a crash is then idempotent, because its source is still intact. Since partitions are 1 MiB aligned, the shift is always at least 1 MiB. Rule: `chunk = clamp(shift, 1 MiB, 64 MiB)`. This removes the backup-to-journal path (the trickiest code in section 5) at the cost of slower tiny moves.

**G4. Moving C: from BootExecute is probably not feasible.**
When BootExecute runs, the kernel already holds C: open: ntoskrnl, boot drivers, and the SYSTEM registry hive are all loaded from it. FSCTL_LOCK_VOLUME will fail, and the runner's own image is mapped from C:. The likely workable route is to **boot a separate environment**: build a WinPE image from the machine's own `winre.wim` at install time (this avoids WinPE redistribution licensing), inject the runner and a `winpeshl.ini`, add a one-time BCD RAM-disk entry (`bcdedit /bootsequence`), then reboot. In WinPE, C: is just another unlocked disk, and the engine can be a normal Win32 program. If this works on Hyper-V Gen 1/2 and Azure (spike S1), **Phases 5 and 6 can both use it, and the NT-native build target can be dropped.** That is a big simplification.

**G5. Journal placement when no free space is left over.**
On a typical Azure data disk with one partition and exactly enough free space for the move, *no* sectors lie outside both ranges. The **primary journal therefore goes in a file on a volume that isn't involved** (normally `C:\ProgramData\OpenPartition\journal\`, or a different disk for C: moves). A second on-disk copy is added only when free sectors are available. If neither location is available, refuse.

**G6. A crash between "table updated" and "journal says table updated".**
Resume must not trust the journal phase alone. It should **read the live partition table** and decide whether the entry is at the old or new offset. Every phase transition has to be idempotent.

**G7. Verify each chunk as it is copied, not only at the end.**
For each chunk: read the source, hash it, write the destination (`FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH`), flush, read it back, compare, then save the chunk hash and checkpoint to the journal. This finds bad writes right away, lets a resume re-verify finished chunks, and meets the rule "never change the table before the copy is verified" without hashing the whole range a second time.

**G8. Locked-volume handle lifetime.**
Keep the volume handle (locked and dismounted) open for the whole copy, so raw disk-handle writes into its sectors are allowed. `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` makes partmgr tear down and recreate the volume device. Expect the handle to go stale, and wait for the new volume to arrive (check its GUID) before verifying.

---

## 3. Repository layout

```
/                       LICENSE, README, build.ps1, Directory.Build.props
/src/engine/            Rust workspace
    opm-core/           no_std+alloc: journal format, chunk scheduler, layout model, CRC/SHA
    opm-platform-win32/ disk I/O, lock/dismount, layout IOCTLs
    opm-platform-sim/   file-backed disk with fault injection (see section 4)
    opm-ffi/            C ABI the .NET service calls
    opm-runner/         boot-time runner (WinPE or native; after S1)
/src/dotnet/
    OpenPartition.Model/      disk/partition/volume model, refusal reasons
    OpenPartition.Discovery/  IOCTL + Storage Management WMI readers (read-only)
    OpenPartition.Operations/ Phase 2 wrappers, operation queue, planner
    OpenPartition.Service/    elevated Windows service, named-pipe host
    OpenPartition.Cli/        `opm` command line (talks to the service, or runs in-process)
    OpenPartition.Gui/        WPF
/tests/
    (engine)            Rust tests live in each crate; crash campaigns in opm-platform-sim/tests
    dotnet/             xUnit
    harness/            PowerShell + Hyper-V: layouts, VM control, fault injection, hashing
/docs/                  admin guide, design notes, test matrix
```

---

## 4. Test strategy (built in Phase 0, used by every later phase)

Three layers, from fastest to most realistic:

1. **Simulated disk (`opm-platform-sim`)**, which runs in milliseconds. It's a file-backed disk that models a volatile write cache: writes since the last flush can be **dropped, reordered or torn** at a simulated crash point. A campaign runs every move scenario with a crash after write #1, #2, … #N, then resumes and checks the result is byte-exact. This gives thousands of crash points per CI run and covers the journal logic thoroughly.
2. **VHDX on the host.** Mount VHDX files on the build/test host, run real Win32 operations against them, then check hashes, `Get-Partition` output, volume GUIDs and `chkdsk /scan`. This covers the real IOCTL and partmgr behaviour.
3. **Hyper-V VM fault injection.** The engine runs inside a guest, driven through PowerShell Direct, and the host calls `Stop-VM -TurnOff` at random times. This covers real power loss, including cache behaviour, BootExecute/WinPE and Gen 1/Gen 2 boot. It's slow, so it runs nightly. Azure runs happen per release, using snapshots.

The harness layout catalogue covers GPT and MBR, 512n/512e/4Kn sectors, partitions at the start/middle/end, single-partition data disks, and the exact boxed-in case that started the project.

---

## 5. Phases

### Phase 0 — Foundations
- `git init`, licence (D3), `build.ps1` (cargo + dotnet, one command), CI on GitHub Actions (build and unit tests; Hyper-V tests on a self-hosted runner).
- Coding standards: Rust `#![forbid(unsafe_code)]` everywhere except the platform crates. `.editorconfig`. Analyzers set to treat warnings as errors.
- Harness skeleton: `New-TestDisk -Layout <name>`, `Get-VolumeHashes`, `Invoke-InGuest`, `Stop-GuestAtRandom`.
- `opm-platform-sim` with crash simulation, plus an empty campaign runner.
- **Acceptance:** as in the handover, plus the sim campaign runner runs in CI.

### Phase 0.5 — Research spikes (time-boxed, about 1–2 weeks total)
Each spike ends with a short write-up in `/docs/research/`.

| Spike | Question (handover section 8) | Method |
|-------|-------------------------------|--------|
| **S1** | C: move route (#3) | Build a WinRE-derived RAM-disk WinPE with a test exe. One-time boot it on Gen 1, Gen 2 and Azure. Check console and serial output. Also try locking C: from BootExecute, to confirm G4. |
| **S2** | MBR MountedDevices (G1) and BCD fields (#1) | On test VMs, change an offset by hand and record what MountMgr and BCD do. Dump `bcdedit /enum all /v` before and after for MBR and GPT. |
| **S3** | BitLocker raw move (#2) | Suspended protection, block move of a data volume, then unlock. Expected result: works, since the FVE metadata is volume-relative. Prove it or refuse it. |
| **S4** | Azure caching and flush (#4) | Data disks set to None, ReadOnly and ReadWrite caching. Write, flush, then force-deallocate the VM and check for lost writes. |
| S5 | VSS (#5), 4Kn/512e (#6) | Lower risk, can be researched during Phases 1–2. |

**Exit:** D4 is decided, and the engine's platform targets are fixed.

### Phase 1 — Read-only discovery and GUI
- `OpenPartition.Discovery`: geometry, layout, extents, MSFT_Disk/Partition/Volume, Win32_EncryptableVolume, page file and hiberfil locations, VSS shadow storage, dynamic disks and Storage Spaces.
- A **refusal and warning model**: every partition gets a list of `Capability` values (CanExtend, CanShrink, CanMove, …), each with a reason when not allowed. The GUI and CLI both display this. Later phases add capabilities but never skip this check.
- Azure: IMDS (`169.254.169.254`, 1 s timeout), with SMBIOS as a fallback. Temp disk identified by its controller/LUN and label (on v6+ sizes it's NVMe and may be raw). Banner recommending a snapshot.
- WPF disk map and the `opm list` / `opm show` CLI commands.
- **Read-only enforcement:** all handle opens go through one `DeviceHandle.OpenRead()`. A unit test plus a source scan fail the build if `GENERIC_WRITE` or `FILE_SHARE`-less opens show up in Discovery.
- **Acceptance:** as in the handover. Compared automatically against `Get-Partition` and `diskpart` output for every harness layout.

### Phase 2 — Built-in Windows operations
- Extend, shrink, create, delete, format, drive letters and mount points, all through MSFT_Partition and MSFT_Volume methods.
- An operation queue with a preview that shows the before and after layouts, computed from the model.
- Service plus named pipe (D5), and the `opm apply` CLI command with `--dry-run`.
- **Acceptance:** as in the handover. **Ship preview release 0.1** (read-only plus basic operations).

### Engine track (runs in parallel with Phases 1–2)
- `opm-core`: journal format (versioned, two slots, sequence number and CRC32 per slot, plus a SHA-256 for each chunk), the chunk scheduler (direction and the G3 chunk rule), and a phase state machine with idempotent transitions (G6).
- `opm-platform-win32`: lock, dismount, unbuffered I/O, flush, layout IOCTLs.
- Sim campaigns at 100% of crash points for every scenario before Phase 3 starts on real disks.

### Phase 3 — Online move of data partitions
- The handover procedure, with changes from G1 (MBR MountedDevices), G5 (journal placement), G7 (per-chunk verify) and G8 (handle lifetime).
- Finding open handles: the Restart Manager API (`RmGetList`) for processes, plus the services that depend on them. Offer to stop services, never kill processes.
- Resume runs when the service starts. When an incomplete journal is found, every other operation is blocked until the user resumes or abandons it. Abandoning is only allowed while the old layout is still intact.
- **Acceptance:** as in the handover, run on both MBR and GPT, plus a 100% pass rate on the sim campaign. **Release 0.2.**

### Phase 4 — Planner
- Partition order is fixed (no reordering), so the search space is small. List every combination of "shift this run of partitions left or right" plus "shrink this neighbour", score by bytes moved, and break ties by the number of steps.
- Throughput is measured with a short read benchmark on the disk (Azure disk tiers cap MB/s), and the ETA is shown before the user confirms.
- A plan journal records each step's own journal ID, so the whole plan can resume.
- **Acceptance:** as in the handover. **Release 0.3**, which covers the main real-world case.

### Phase 5 — Boot-time runner for locked data volumes
- Uses the route chosen in S1. If it's WinPE, the same runner is reused for Phase 6. If it's BootExecute, follow the handover (it removes its own entry before any disk write).
- In both routes, the one-time boot (`/bootsequence`, or self-removal) means a crash brings back a normal boot.
- **Acceptance:** as in the handover.

### Phase 6 — C: moves
- Steps depend on S1 and S2: BCD `device` / `osdevice` updates (MBR mainly), the G2 hidden-sectors patch for BIOS/MBR, and checks with `reagentc /info`.
- Safeguards as in the handover. The Azure snapshot check uses IMDS plus an optional `az` / managed identity query. If it can't be verified, show a stronger warning and don't block.
- **Acceptance:** as in the handover.

### Phase 7 — Release and hardening
- As in the handover. Azure Trusted Signing is the cheapest real option for open-source code signing. Use MSIX or WiX for the installer (WiX if the service install needs to be scriptable on Server Core).

---

## 6. Milestones

| Release | Contents | Risk |
|---------|----------|------|
| 0.1 preview | Discovery, GUI, CLI, built-in operations | Low |
| 0.2 | Online data partition move | Medium |
| 0.3 | Make-room planner. **Solves the boxed-in Azure data disk case** | Medium |
| 0.4 | Boot-time runner for locked data volumes | High |
| 1.0 | C: moves, signed installer, documentation, public test matrix | Highest |

## 7. Immediate next steps

1. ~~Confirm D1–D3 (and D6).~~ Done.
2. Phase 0: repo scaffold, `build.ps1`, CI, harness skeleton, sim disk.
3. Start spikes S1 and S2 straight away. They decide the rest of the architecture.
