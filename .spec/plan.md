# Open Partition Manager — Implementation Plan

Based on `.spec/handover.md`. This plan keeps the handover's phases but changes four things:

1. It adds an early **research spike phase**, because three of the open questions change the architecture.
2. It adds a **simulated-disk fault-injection layer**, so crash-safety can be tested thousands of times per run instead of only through ~100 slow VM hard-stops.
3. It **builds the engine alongside Phases 1–2**, because the engine is the critical path.
4. It fixes several **technical gaps** in the handover (section 2) that would otherwise surface in Phase 3 or 6.

## Progress (2026-10-30)

| Item | State |
|------|-------|
| Phase 0 — foundations, harness, sim disk | **Done.** `./build.ps1` builds and tests everything. |
| Engine track — journal + block mover (`opm-core`) | **Done on the simulator.** Two-slot CRC journal, chunk ≤ shift rule (G3), per-chunk flush + read-back (G7), table-vs-journal reconciliation (G6), journal on a separate disk (G5). Crash campaigns cover left/right/overlapping/partial-chunk/4Kn moves, including crashes during recovery. No Win32 disk code yet. |
| Engine on Windows (`opm-platform-win32`, `opm-engine`) | **Working on VHDX test disks.** Raw unbuffered write-through I/O, volume lock/dismount, layout edits via `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`, file journal. New gap **G9** handled: the partition's table entry is removed for the whole move (a snapshot of the raw entry is journaled), so Windows can't mount a half-moved volume after a crash; the final table write puts the entry back at the target. Source SHA-256 is journaled and the target must match before the entry goes back. Recovery refuses to write if another partition has been created over the source or target meanwhile. Verified on GPT right/left-overlapping/boxed-in/4Kn/tight and MBR moves, plus process kills mid-move with recovery: files byte-identical, `Repair-Volume -Scan` clean, GPT volume GUID preserved. **Write guard:** only file-backed virtual disks can be opened for writing. |
| Phase 1 — read-only discovery | **Done, pending Azure validation.** `opm list` / `opm show`, WPF disk map, refusal/warning classifier, read-only enforcement tests. Matches `Get-Partition`/`Get-Volume`/`Get-Disk` on all 7 harness layouts. Azure detection (SMBIOS asset tag + IMDS) and temp-disk heuristics are untested on a real Azure VM. |
| Spike S2 (early evidence) | MBR volume GUIDs are built from disk signature + partition offset (e.g. `{6facef11-0000-0000-0000-100000000000}` = signature 0x6FACEF11, offset 0x100000). Confirms G1: an MBR move changes the volume GUID unless MountedDevices is rewritten. |
| Spikes S1–S4 | **Waiting on test VMs.** |
| Azure field test #1 (2026-10-30) | Real Azure VM: Windows Server **2016** (14393), Gen 1, MBR, ATA, Standard_B4ms, australiasoutheast. Self-contained .NET 10 runs on 2016. Azure detection, IMDS and temp-disk labelling (D:, with a page file) all correct; engine refused both real disks. Bugs found and fixed: MBR EBR chain link entries were shown as extra extended partitions; the self-test assumed a 16 MiB MSR (Server 2016 creates 128 MiB). |

### G10 (resolved): resuming a move on Server 2016

Azure field tests #3–#4 (Server 2016): a move killed part way could not be resumed. Recovery's
raw writes succeed in free space but get ERROR_ACCESS_DENIED at the first write into the hidden
partition's own range, although Windows shows no volume on it. The uninterrupted move works
because the volume lock taken before hiding is held throughout. Windows 11 doesn't block these
writes. Tried locking the partition device (`\Device\HarddiskN\PartitionM`) during recovery:
it gets past the denial, but on Windows 11 recovery then produced data that failed the final
digest check in 3 of 3 rounds (0 of 3 without the lock). The digest check caught it and the
partition stayed hidden, but the lock is unusable. Removed.

Fix (2026-10-30, journal format 2): instead of changing the partition's type to hide it, the
mover **removes its table entry** for the duration of the move. The journal stores the raw
`PARTITION_INFORMATION_EX` (type, GUID, attributes, GPT name, MBR active flag) and the final
table write puts it back at the target; GPT entries leave the array, MBR primary slots are
emptied in place. Raw writes into unpartitioned space were allowed on Server 2016 in every
test, and nothing can mount a partition that isn't in the table. Recovery checks that no
partition now overlaps the source or target before writing (`MoveError::RangeInUse`).
Verified: simulator crash campaigns, all VHDX engine tests and the packaged self-test on
Windows 11 (GPT volume GUID survives the remove and re-add). **Confirmed on Server 2016**
(Azure field test #5): killed after 6 of 32 chunks, recovery resumed with the entry out of the
table, files identical, scan clean, volume GUID kept. Still to do: the GUI/CLI must read the journal and mark the range, because during an interrupted move
it shows as free space in Disk Management; README warning.

### Field finding: the boxed-in C: on MBR with logical drives (engine support done, VHDX)

The test VM is the real-world case: C: (51.5 GB, 71% used) is followed by a primary (I:) and an
**extended partition holding three logical drives** (E:, L:, K:), with 129 GB free at the end of
the disk. Extending C: means moving I: *and the whole extended container* right.

Done (2026-10-30, journal format 3):
- **Whole-container moves.** `opm-engine move --from <extended start>` moves the extended
  partition with every logical drive in it as one block. Logical offsets inside the container
  are relative, so the block copy keeps the chain valid. The journaled snapshot holds the
  extended entry plus every logical and link entry (up to 11 logical drives). They are removed
  from the table together and put back shifted; Windows rewrites the EBRs from the layout.
  Logical drives still can't be moved on their own. Every volume inside the range is locked
  first.
- **G1, MBR identity.** Windows names a basic MBR volume by disk signature + start. Drive
  letters live in `HKLM\SYSTEM\MountedDevices` and are rewritten to the new start before the
  entries go back. Volume GUIDs are usually *computed* from signature + start, with no stored
  entry (seen on Windows 11 and on the Azure VM, including C:), so a
  `\??\Volume{old computed GUID}` value pointing at the new start is added. The mount manager
  uses it and the volume keeps its GUID. The rewrite plan is saved under
  `HKLM\SOFTWARE\OpenPartition` first, so a crash part way through replays it exactly.
- Verified on VHDX: harness layout `mbr-512n-logical-boxed-in`. Move the extended partition,
  then the primary, then `Resize-Partition` SYS into the freed space; files identical, scans
  clean, all volume GUIDs kept, including a run killed mid-copy and recovered. The packaged
  self-test has the same round.
- **Confirmed on Server 2016** (Azure field test #6): the same round on a VHDX on the VM passed:
  the extended partition with 3 logical drives (all 3 volumes locked) and the primary moved
  256 MiB, the first partition extended, files identical, scans clean, volume GUIDs **and drive
  letters** kept, in 14.7 s.
- Drive letters are only checked where no Remote Desktop client runs: `msrdc.exe` (RDP drive
  redirection) opens every new lettered volume and never lets go, so the engine correctly
  refuses to lock it. Real servers will have similar holders (services, agents, open
  files): a live move of a busy volume needs the owner stopped, or the boot-time mode.

### Operations in the GUI and CLI (2026-10-31)

General operations, not a single-purpose wizard: **Move** (any movable partition, or an MBR
extended partition with its logical drives, anywhere in the free space around it) and
**Extend** (Windows' own `MSFT_Partition.Resize` into free space after a volume). The GUI
queues them, previews the resulting layout on the disk map, and applies them in order; `opm
move | extend | resume` do the same one step at a time (dry run unless `--yes`).
`OpenPartition.Operations` holds the planner (pure), the write gate, the engine runner (one
journal per move in %ProgramData%\OpenPartition\journals) and the resizer; GUI and CLI share it.

**Write gate (option A, user decision):** test VHD/VHDX disks always; real disks only on an
Azure VM after the user names a snapshot. The engine enforces it itself
(`--snapshot-confirmed` + SMBIOS asset tag read via `GetSystemFirmwareTable`); the .NET side
applies the same rule, including to extends. Physical machines, Hyper-V and other hypervisors
stay read-only for now. Compatibility matrix in the README.

Verified: planner unit tests on the Azure layout; `opm` end to end on VHDX (free space after
SYS by moving the extended partition and PRIM, extend SYS), including an engine kill and `opm
resume`; the GUI driven by UI Automation through the same flow
(`tests/harness/Test-GuiFreeAndExtend.ps1`). Next: first real-disk run on the Azure VM.

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

## v0.1.0 baseline (2026-10-31)

Shipped since the "Operations in the GUI and CLI" section above:

- **Field incident (Azure, Server 2016):** moving the 65 GB extended partition finished the
  copy but failed the final source-digest check, so the partition stayed out of the table
  (by design). `compare` and `rollback` were added; rollback put E:, K: and the other logical
  drives back with their letters. Root cause not proven (the source changed between hashing
  and copying).
- **Engine (journal format 4):**
  - The source is hashed only after the entry is out of the table.
  - Each chunk is checked against its own digest as it is copied, so a source change stops the
    move at that chunk (`SourceChanged`). A non-overlapping move that fails verification
    undoes itself (`rolled_back=yes`).
  - The final full re-read only runs after a restart.
  - `roll_back` (core), `rollback` / `compare` (CLI), `opm undo`, and Undo in the GUI.
- **Speed:**
  - Copy maps (only mapped parts are hashed and copied; the map file sits next to the journal,
    identified by SHA-256).
  - Parallel unbuffered I/O through 4 handles in 8 MiB pieces.
  - Multi-core tree SHA-256.
  - Chunks up to 128 MiB.
- **Known gap G11:** NTFS free-space skipping is not effective yet. `FSCTL_GET_NTFS_VOLUME_DATA`
  on the locked volume fails with ERROR_NOT_READY (0x15) on Server 2016, so whole volumes are
  copied (safe fallback, logged per volume as `free_bytes=unknown`). Reading the bitmap before
  the lock is unsafe (allocations in between would be skipped). Next: find a lock-safe way, e.g.
  parse `$Bitmap` through the locked handle.
  - **Fix (#7, pending Azure confirmation):** `opm-platform-win32/src/ntfs.rs` is a read-only
    NTFS parser (no `unsafe`): boot sector -> `$MFT` record 0 -> record 3 (`$Volume`, refuses a
    dirty volume) -> record 6 (`$Bitmap`) unnamed `$DATA` runlist -> bitmap bytes. It checks
    fixups, record numbers, attribute and runlist bounds, sizes against the boot sector's
    cluster count, and that the boot sector, `$MFT` and `$Bitmap` clusters are marked used. It
    refuses attribute lists, sparse/compressed/encrypted data, and MFT records outside
    1-4 KiB; any refusal means a full copy.
  - `LockedVolume::lock` still asks the FSCTLs under the lock (before the dismount), then reads
    `$Bitmap` raw through the locking handle *after* `FSCTL_DISMOUNT_VOLUME` (the log is
    checkpointed and nothing can allocate, so the on-disk bitmap is final). Both work: a
    cluster is free only if free in both. One works: that one. Neither, or dirty: full copy.
  - The log line is `locked=… free_bytes=<n> free_source=raw+fsctl|raw (…)|fsctl (…)`. VHDX
    move scenarios now require `raw+fsctl` on NTFS volumes (proves the parser on real NTFS);
    Server 2016 is expected to show `raw (FSCTL: OS error 0x00000015)`. Unverified until the
    self-test move round passes there: raw reads on a dismounted, locked volume handle.
  - Unit tests build synthetic NTFS images (512 B/4 KiB sectors, 512 B-128 KiB clusters,
    records straddling MFT runs) and mutate them: 20,000 corrupted variants never panic and
    never report a used cluster free.
- **GUI:**
  - New look (sidebar, summary cards, tile map, partition table, details pane, footer Apply).
  - Icons (`assets/brand`).
  - Drag-to-place Move/Extend dialogs with exact MiB fields and a whole-disk strip.
- **Releases:** CI publishes `openpartition-<version>-win-x64.zip` to GitHub Releases (tags
  `v*`), and a rolling `preview` pre-release from `main`.
