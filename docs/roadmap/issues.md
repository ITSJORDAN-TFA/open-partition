# Roadmap issues

The seed for the GitHub issues that make up the to-do list. `tools/roadmap/New-RoadmapIssues.ps1`
creates one issue per `##` section, in this order, so every issue comes after the ones it depends
on. In bodies, `{{key}}` becomes a link to that issue. Once created, **GitHub is the source of
truth**; this file is only re-run to add new sections.

Section format:

```
## key: Title
milestone: <milestone title>
labels: comma, separated
depends: key-a, key-b        (or "none")

Body (markdown)…
```

---

## free-space-map: Skip free NTFS clusters when moving a partition (fix G11)
milestone: v0.2 — data partition moves, hardened
labels: type: feature, area: engine, P1, needs-azure
depends: none

**Goal:** moves copy only clusters NTFS uses, so a half-empty 65 GB volume copies half the data.

**Current state:**
- The copy map is implemented end to end: `opm-core::CopyMap`, `opm-platform-win32/src/copy_map.rs`, map file next to the journal, crash campaigns for mapped moves.
- But on Server 2016 the free-space read fails, so whole volumes are copied (safe fallback, logged as `locked=… free_bytes=unknown reason=not_NTFS_or_bitmap_unreadable_(OS_error_0x00000015)`).
- `LockedVolume::lock` (`volume.rs`) calls `FSCTL_GET_NTFS_VOLUME_DATA` / `FSCTL_GET_VOLUME_BITMAP` after `FSCTL_LOCK_VOLUME`, and that returns ERROR_NOT_READY.

**Constraint:** the bitmap must reflect the volume *while it is locked*. Reading it before the lock is unsafe: clusters allocated in between would be treated as free and never copied (silent data loss).

**Suggested approach** (prototype on a VHDX first, record what works in `.spec/plan.md`):
1. Check whether the FSCTLs work on the lock handle if issued *before* any other request, or on a second handle opened before the lock (the lock may fail then).
2. If not: read `$Bitmap` through the locked volume handle with raw reads. Parse the NTFS boot sector → MFT record 6 → `$DATA` runlist → bitmap bytes. That is a narrow, read-only NTFS parser; keep it small, fuzz it, and fall back to a full copy on anything unexpected.

**Acceptance criteria:**
- [ ] Moving a 60%-full test volume copies less than its size: the self-test move round asserts this, so it must pass on Server 2016 (Azure VM) and Windows 11.
- [ ] Any parse or IOCTL problem falls back to a full copy, never a partial one.
- [ ] Crash campaigns and VHDX suites still pass; a mapped move survives a kill (`Test-KilledMovesRecover`).
- [ ] The log shows `free_bytes=<n>` per volume.

**Files:** `src/engine/opm-platform-win32/src/volume.rs`, `copy_map.rs`, `tools/package/Test-OpmOnThisMachine.ps1`.

## azure-source-change-rca: Find what changed E: during the first Azure move (verification failure)
milestone: v0.2 — data partition moves, hardened
labels: type: spike, area: engine, P1, needs-azure, needs-maintainer
depends: none

**Goal:** know what wrote to a locked, dismounted logical drive (E:) during the 2026-10-31 Azure move, so it can be prevented or detected up front rather than after copying.

**Background:** the 65 GB extended-partition move finished copying, but the final source-digest check failed, so the move was rolled back with `rollback`. Since then the source is hashed after the entry leaves the table, and each chunk is checked as it is copied. A recurrence now stops at the exact chunk with `SourceChanged { offset }` and undoes itself. Candidates:
- Windows writing to the volumes as the partitions leave the table;
- a filter or agent writing below the file system (backup, replication, AV);
- an application using E: that holds no file handle.

**Tasks:**
- [ ] On the Azure VM (fresh snapshot first), repeat a move of the extended partition with the current build. If it stops with `SourceChanged`, map the offset to a file: offset → volume → cluster, then `fsutil volume querycluster`.
- [ ] Record filter drivers (`fltmc`, `fltmc instances`) and services touching E:.
- [ ] Write up the cause and the mitigation in `.spec/plan.md`. If the writer can be detected before a move starts, open a follow-up to refuse or warn in the classifier.

**Acceptance:** cause identified (or ruled down to a short list with evidence) and documented; follow-up issues opened.

## volume-users-restart-manager: Show what holds a volume open and offer to stop services
milestone: v0.2 — data partition moves, hardened
labels: type: feature, area: operations, area: gui, P2
depends: none

**Goal:** when a volume can't be locked, the Apply dialog names the processes and services using it and offers to stop the services, never kill processes.

**Current state:** `OpenPartition.Operations/VolumeUsers.cs` guesses from services and processes whose image is on the drive, and points to Resource Monitor. It doesn't see processes that merely have files open there.

**Approach:** use the Restart Manager API (`RmStartSession`, `RmRegisterResources` with the files that are open, `RmGetList`). Enumerate open handles per volume where Restart Manager can't, e.g. with `NtQuerySystemInformation(SystemHandleInformation)` or another documented route. Map processes to services (`Win32_Service.ProcessId`) and dependent services.

**Acceptance criteria:**
- [ ] With a test program holding a file open on a VHDX volume, the failure message names that program.
- [ ] The dialog offers "Stop service X and retry" for services; it never stops processes itself.
- [ ] xUnit tests for the message formatting; a VHDX harness test for detection.

## move-throughput-eta: Measure disk speed and show an ETA before and during a move
milestone: v0.2 — data partition moves, hardened
labels: type: feature, area: gui, area: engine, P2
depends: none

**Goal:** before Apply, the user sees roughly how long each step will take; during Apply, current MB/s and time remaining.

**Approach:**
- Engine: add `opm-engine bench --disk N --offset X --length L` (read-only, a few seconds of 8 MiB parallel reads, the same I/O path as moves). Progress lines already carry `stage` and `bytes`; add a timestamp or compute rates in .NET.
- GUI: Apply dialog shows the per-step estimate (bytes to copy × passes ÷ measured rate) and live MB/s and ETA. Explain that Azure disk tiers and VM sizes cap throughput.

**Acceptance criteria:**
- [ ] `bench` is read-only (unit test: the engine opens the disk for reading only).
- [ ] The estimate is within 2× of the actual time on a VHDX test move.
- [ ] Live MB/s and ETA update at least every few seconds during a move.

## move-recovery-partition: Move the Windows Recovery partition (keep WinRE working)
milestone: v0.2 — data partition moves, hardened
labels: type: feature, area: engine, area: discovery, P1, needs-hyperv-vm
depends: none

**Goal:** a recovery partition right after C: (very common on GPT and Gen2 installs) can be moved to the end of the disk so C: can be extended. WinRE must still boot afterwards.

**Current state:** the classifier refuses Recovery partitions ("Changing it is not supported in this version"). The engine can move any partition; the work is keeping WinRE registered.

**Approach:**
- Before: `reagentc /info` to record status and location. Move with the engine, as for a data partition (GPT: the partition GUID and type stay the same).
- After: `reagentc /disable` + `/enable`, or point the BCD `recoverysequence` / ramdisk device at the new partition. Verify `reagentc /info` shows Enabled at the new location.
- MBR recovery partitions: also the MountedDevices remap (as for data).

**Acceptance criteria:**
- [ ] Hyper-V Gen2 and Gen1 VM test: move the recovery partition, then `reagentc /info` shows Enabled, and booting into WinRE (`reagentc /boottore` + reboot) works.
- [ ] Classifier allows Move for Recovery partitions only when WinRE can be re-registered; otherwise it explains why not.
- [ ] Planner tests for the "C: boxed in by recovery" layout (new harness layout `gpt-512e-recovery-after-c`).

## bitlocker-suspended-moves: Allow moving BitLocker volumes with protection suspended
milestone: v0.2 — data partition moves, hardened
labels: type: spike, type: feature, area: engine, area: discovery, P2, needs-hyperv-vm
depends: none

**Goal:** prove (spike S3) whether a block-level move of an encrypted data volume is safe once protection is suspended. If yes, allow it; if no, keep refusing with a clear reason.

**Current state:** the classifier refuses Move for BitLocker volumes, and for volumes whose status is unknown.

**Tasks:**
- [ ] VHDX test: encrypt a volume, suspend protection (`Suspend-BitLocker -RebootCount 0`), move it with the engine, then unlock and verify files. The FVE metadata is expected to be volume-relative.
- [ ] Repeat with protection on (expected: works too, since data is ciphertext; but the keys/unlock path must survive).
- [ ] Decide the rule: e.g. "allowed when fully encrypted or fully decrypted, never mid-conversion". Document it in the compatibility matrix.

**Acceptance:** documented result, classifier updated, harness test added.

## shadow-copy-handling: Offer to remove or relocate shadow copies blocking a move
milestone: v0.2 — data partition moves, hardened
labels: type: feature, area: operations, area: gui, P3
depends: none

**Goal:** volumes with shadow copies, or hosting shadow storage, are refused today. Offer a guided fix instead of a dead end.

**Approach:** show which volumes are affected (`Win32_ShadowStorage`). Offer "Delete shadow copies of X" (`vssadmin delete shadows /for=X /quiet`, with explicit confirmation) or "Move shadow storage to Y" (`vssadmin resize/add shadowstorage`). Re-run discovery after.

**Acceptance criteria:**
- [ ] VHDX test: create a shadow copy, see the refusal, apply the fix, and the move is then allowed and succeeds.
- [ ] Nothing is deleted without an explicit confirmation naming the volume.

## move-logical-drive: Move a single logical drive inside an MBR extended partition
milestone: v0.2 — data partition moves, hardened
labels: type: feature, area: engine, P3
depends: none

**Goal:** move one logical drive within its extended partition, e.g. to close a gap, instead of only moving the whole container.

**Current state:** `layout.rs` treats the container as one block; `opm-engine move` refuses logical drives ("move its extended partition").

**Approach:** rewrite the EBR chain for the moved logical (its EBR location and the previous EBR's link). Detach and attach must remove and re-insert just that logical's records. Journal snapshot format: record the chain neighbours.

**Acceptance criteria:**
- [ ] Unit tests on synthetic MBR layouts (first, middle, last logical; left and right moves).
- [ ] Crash campaign coverage for the new table writes.
- [ ] VHDX test: files, volume GUIDs and drive letters kept; `diskpart list partition` consistent.

## hyperv-ci-runner: Self-hosted Hyper-V runner for integration tests in CI
milestone: v0.2 — data partition moves, hardened
labels: type: infra, area: ci, P2, needs-maintainer
depends: none

**Goal:** VHDX integration suites (`./build.ps1 -Integration`) run automatically, nightly and on demand, not just on a developer machine.

**Tasks:**
- [ ] Register a Windows runner with Hyper-V enabled, running elevated, labelled `self-hosted, windows, hyperv` (runner ≥ 2.327.1 for Node 24 actions).
- [ ] Add a nightly `schedule:` trigger for the `integration` job and upload `tests/harness/out/results.json`.
- [ ] Document the runner setup in `docs/`.

**Acceptance:** a nightly integration run is green, and a failing VHDX test fails the workflow.

## cli-plan-files: Multi-step plans from the CLI (Server Core) with JSON output
milestone: v0.2 — data partition moves, hardened
labels: type: feature, area: cli, P2
depends: none

**Goal:** everything the GUI queue does works from `opm` on Server Core: build a plan, preview it, apply it, follow progress as JSON.

**Approach:**
- `opm plan add <move|extend …>` to build a plan file (JSON) using `Planner.Append`, which merges steps.
- `opm plan show` for a dry run with the before/after layout.
- `opm apply <plan.json> --snapshot X --yes [--json]`, with JSON lines for progress.

**Acceptance criteria:**
- [ ] xUnit tests for plan files (round trip, merging, validation against a changed disk).
- [ ] Harness test: plan with two moves and an extend applied via the CLI on a VHDX.
- [ ] `--json` progress documented in the README.

## shrink-volume: Shrink NTFS volumes
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, area: gui, area: cli, P1
depends: none

**Goal:** shrink a volume from its end (Windows' own resize, like Disk Management), from the GUI drag bar and `opm shrink`.

**Approach:** `MSFT_Partition.GetSupportedSize` gives the minimum. `Resizer.cs` already wraps Resize. Planner: `ShrinkStep`, projection, and merging with consecutive shrinks/extends of the same partition. GUI: Shrink dialog with `PlacementBar` (drag the right edge left; dashed outline at the current size).

**Acceptance criteria:**
- [ ] Planner unit tests: minimum size, merging, projection.
- [ ] VHDX test: shrink, files intact, file system scan clean.
- [ ] Refused with a reason when the minimum is above the requested size (e.g. unmovable files).

## create-format-partition: Create and format partitions in free space
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, area: gui, area: cli, P2
depends: none

**Goal:** create a partition in unallocated space (size and position, NTFS or ReFS, label, drive letter) from the GUI and `opm create`.

**Acceptance criteria:**
- [ ] Planner step plus projection; MBR primary/extended/logical rules (max 4 primaries) enforced with clear refusals.
- [ ] VHDX tests for GPT and MBR.
- [ ] Never creates inside a range a pending (journaled) move owns.

## delete-partition: Delete a partition (with strong confirmation)
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, area: gui, area: cli, P2
depends: none

**Goal:** delete data partitions to make room. Refused for system, boot, recovery, MSR, EFI and pagefile volumes, and for partitions a journaled move owns.

**Acceptance criteria:**
- [ ] The confirmation names the volume, its label and size, and requires typing the drive letter or label.
- [ ] The CLI requires `--yes` plus `--confirm <label>`.
- [ ] Tests: classifier refusals and a VHDX delete.

## drive-letters-mount-points: Change drive letters and mount points
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, area: gui, area: cli, P3
depends: none

**Goal:** assign, change or remove drive letters and folder mount points from the GUI and `opm letter`.

**Acceptance criteria:**
- [ ] C: and the system partition are refused.
- [ ] A warning when services or shares reference the old letter (best effort: `Win32_Share`, service image paths).
- [ ] VHDX tests.

## plan-journal: Resume a whole multi-step plan after a crash or reboot
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, area: engine, P1
depends: none

**Goal:** if a 5-step plan is interrupted at step 3, the app offers to continue from step 3, not just finish the interrupted move.

**Current state:** each move has its own journal and can be resumed or undone, but the rest of the plan is lost.

**Approach:** a plan journal under `%ProgramData%\OpenPartition\plans\`, written durably before step 1. It holds the steps, the per-step engine journal paths and the completed count. The GUI banner and `opm resume` continue the plan, re-validating each remaining step against the live layout before running it.

**Acceptance criteria:**
- [ ] Kill the app mid-plan (harness), restart, resume, and the final layout equals the planned one.
- [ ] If the disk changed outside the tool, remaining steps are refused with an explanation.
- [ ] xUnit tests for plan-journal round trips and re-validation.

## service-named-pipe: Elevated Windows service with a secured named pipe (D5)
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, P2
depends: none

**Goal:** the GUI and CLI can run unelevated. A LocalSystem service does discovery and disk writes, reachable through a named pipe whose DACL allows only Administrators and SYSTEM.

**Acceptance criteria:**
- [ ] `OpenPartition.Service` project; JSON messages; versioned protocol.
- [ ] Security test: a non-admin client is refused by the pipe ACL.
- [ ] GUI and CLI fall back to in-process mode when no service is installed (today's behaviour).

## auto-resume-on-boot: Resume interrupted moves and plans automatically at startup
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, P2
depends: service-named-pipe, plan-journal

**Goal:** after a reboot or power loss mid-move, the service finds the journal and, as configured, finishes the move or holds every other operation until the admin decides. Volumes are never left offline unnoticed.

**Acceptance criteria:**
- [ ] Hyper-V VM test: hard power-off mid-move, boot, and the service completes the move; the event log records it.
- [ ] Policy setting: auto-finish (default) or wait for the admin.
- [ ] Windows Event Log entries for start, progress milestones, completion and failure.

## make-room-planner: One-click "extend C: (or any volume) to the maximum"
milestone: v0.3 — make-room planner
labels: type: feature, area: operations, area: gui, area: cli, P1
depends: shrink-volume, move-recovery-partition, plan-journal

**Goal:** the user picks a volume and "Make room and extend"; the planner finds the cheapest sequence of moves (and optional neighbour shrinks) that puts free space right after it, then extends it. This is the boxed-in Azure case in one click.

**Approach** (plan.md Phase 4): partition order is fixed. Enumerate "shift this run of partitions left or right" and "shrink this neighbour" combinations. Score by bytes moved (use copy maps when available), then by number of steps. Prefer non-overlapping moves (they can be undone).

**Acceptance criteria:**
- [ ] Planner unit tests on every harness layout, including the Azure field case (extended partition plus logicals after C:) and recovery-after-C.
- [ ] The GUI shows the generated plan in the queue for review before Apply; nothing runs automatically.
- [ ] `opm make-room <disk> <volume> [--size]`.

## gui-map-drag: Move and resize directly on the disk map
milestone: v0.3 — make-room planner
labels: type: feature, area: gui, P3
depends: none

**Goal:** drag tiles on the main disk map to queue moves, and drag their edges to queue extends or shrinks, reusing `PlacementBar` logic. The dialogs stay available for exact values.

**Acceptance criteria:**
- [ ] Keyboard-accessible alternative (the dialogs) unchanged; UIA names stable for `Test-GuiFreeAndExtend.ps1`.
- [ ] Dragging shows a live preview and only queues a step on release; Esc cancels.

## spike-winpe-boot: Spike S1 — one-time boot into a WinRE-derived WinPE on Hyper-V and Azure
milestone: v0.4 — boot-time runner
labels: type: spike, area: engine, P1, needs-hyperv-vm, needs-azure
depends: none

**Goal:** decide D4, the route for moving volumes that can't be locked online (including C:).

**Tasks:**
- [ ] Build a RAM-disk WinPE from the machine's own `winre.wim` (no WinPE redistribution), inject a test exe and `winpeshl.ini`, and add a one-time BCD entry (`bcdedit /bootsequence`).
- [ ] Boot it on Hyper-V Gen1, Gen2 (Secure Boot on) and an Azure VM. Capture output through the serial console / boot diagnostics.
- [ ] Confirm that BootExecute can't lock C: (G4).
- [ ] Write up `docs/research/s1-boot-route.md`: steps, what failed, and the decision.

**Acceptance:** D4 decided and recorded in `.spec/plan.md`.

## spike-bcd-mounteddevices: Spike S2 — what BCD and MountedDevices need when system or boot partitions move
milestone: v0.4 — boot-time runner
labels: type: spike, area: engine, P1, needs-hyperv-vm
depends: none

**Goal:** know exactly which BCD elements and registry values must change when the System Reserved/EFI partition or C: moves, on MBR and GPT.

**Tasks:**
- [ ] On test VMs, move these partitions offline (from another OS or WinPE) and record `bcdedit /enum all /v` and MountedDevices before and after, and what breaks at boot.
- [ ] MBR: confirm the G2 hidden-sectors (BPB 0x1C) patch is needed for the active partition, in both the primary and backup boot sectors.
- [ ] Write up `docs/research/s2-bcd.md`.

**Acceptance:** a documented, tested list of required changes for each case.

## boot-time-runner: Boot-time runner that performs moves outside the running Windows
milestone: v0.4 — boot-time runner
labels: type: feature, area: engine, area: operations, P1, needs-hyperv-vm, needs-azure
depends: spike-winpe-boot

**Goal:** queue a plan, reboot into the route chosen in S1, run the engine there with the same journal format, and boot back into Windows automatically. A crash at any point brings back a normal boot and a resumable journal.

**Acceptance criteria:**
- [ ] Hyper-V Gen1 and Gen2 and Azure: a move that can't run online (page file on the volume) completes via the runner.
- [ ] Power-off at random points during the runner (harness `Stop-GuestAtRandom`): the machine always boots normally afterwards, and the move resumes or is undone.
- [ ] Progress visible on the Azure serial console.

## locked-data-volume-moves: Move data volumes that can't be locked online
milestone: v0.4 — boot-time runner
labels: type: feature, area: operations, area: gui, P2
depends: boot-time-runner

**Goal:** when a data volume can't be locked (page file, a service that can't stop), offer "Do it at next restart" instead of refusing.

**Acceptance criteria:**
- [ ] GUI and CLI show the restart requirement and the estimated downtime before Apply.
- [ ] Harness VM test with a page file on the moved volume.

## move-system-partition: Move the System Reserved / EFI partition
milestone: v1.0 — C: moves and signed release
labels: type: feature, area: engine, P2, needs-hyperv-vm
depends: boot-time-runner, spike-bcd-mounteddevices

**Goal:** move the system partition (MBR active System Reserved, or the GPT EFI partition) so space can be rearranged at the start of the disk. Boot must keep working.

**Acceptance criteria:**
- [ ] MBR: hidden-sectors patch (G2) in both boot sectors, as a separate audited routine with unit tests.
- [ ] BCD updates per S2; VM tests on Gen1 and Gen2 boot normally afterwards.
- [ ] Power-loss tests through the runner.

## move-boot-volume: Move C: (the Windows volume)
milestone: v1.0 — C: moves and signed release
labels: type: feature, area: engine, area: operations, P1, needs-hyperv-vm, needs-azure
depends: boot-time-runner, spike-bcd-mounteddevices

**Goal:** move the Windows partition itself via the boot-time runner, updating BCD `device`/`osdevice` (and MountedDevices on MBR), so layouts where C: must shift become possible.

**Acceptance criteria:**
- [ ] VM tests (Gen1, Gen2, Azure): move C: left and right; Windows boots; drive letters, page file and WinRE are intact.
- [ ] Power-loss campaign through the runner: always bootable afterwards, or recoverable from the runner automatically.
- [ ] Extra safeguards: snapshot confirmation (see {{azure-snapshot-verification}} once done), free-space checks, an explicit typed confirmation.

## azure-snapshot-verification: Check that an Azure snapshot really exists before writing to a real disk
milestone: v1.0 — C: moves and signed release
labels: type: feature, area: discovery, area: operations, P2, needs-azure
depends: none

**Goal:** today the user types a snapshot name and we trust it. Where possible, verify it: IMDS gives the VM's resource ID and disk names. With a managed identity or `az` login, list snapshots of the OS or data disk created recently.

**Acceptance criteria:**
- [ ] Verified: green check with the snapshot's name and time. Can't verify: a stronger warning, still not blocking, as today.
- [ ] No credentials stored by the tool; uses the VM's managed identity or the user's existing `az` session only.

## code-signing: Code-sign release binaries (Azure Trusted Signing) in CI
milestone: v1.0 — C: moves and signed release
labels: type: infra, area: ci, P2, needs-maintainer
depends: none

**Goal:** `opm.exe`, `opm-engine.exe` and `OpenPartition.exe` in releases are signed, so SmartScreen stops warning.

**Tasks:**
- [ ] Maintainer: set up an Azure Trusted Signing account and certificate profile, and add the federated credential for GitHub Actions (OIDC), with no secrets in the repo.
- [ ] CI: sign in the `build` job for tags (and optionally main), then verify signatures before packaging.

**Acceptance:** release assets show a valid signature (`Get-AuthenticodeSignature`).

## installer: Installer (WiX) with service registration, Server Core friendly
milestone: v1.0 — C: moves and signed release
labels: type: feature, area: ci, area: operations, P2
depends: service-named-pipe, code-signing

**Goal:** an MSI that installs to Program Files, registers the service, adds Start-menu entries and `opm` to PATH, and supports silent install (`msiexec /qn`) on Server Core. The zip stays available.

**Acceptance criteria:**
- [ ] Install, upgrade and uninstall tested in CI on a Windows runner.
- [ ] Signed MSI attached to releases.

## admin-guide-test-matrix: Admin guide and public test matrix
milestone: v1.0 — C: moves and signed release
labels: type: docs, P2
depends: none

**Goal:** `docs/` explains when and how to use each operation safely (snapshots, what is locked, expected downtime, recovery after failure). A public matrix records which Windows versions, platforms and layouts were tested for each release, fed by compatibility reports.

**Acceptance criteria:**
- [ ] Guide covers every operation in the README's operations table, including resume and undo.
- [ ] Matrix updated from at least the harness and the Azure Server 2016 results.

## convert-mbr-gpt: Convert a data disk between MBR and GPT
milestone: Later
labels: type: feature, area: engine, P3
depends: none

**Goal:** convert *data* disks between MBR and GPT without data loss (system disks: point to `mbr2gpt`). Check space for the GPT headers at the start and end, and keep volume GUIDs and letters.

**Acceptance:** VHDX tests both ways; refuses when headers don't fit, with a fix suggested (move or shrink).

## clone-partition: Copy a partition to another disk
milestone: Later
labels: type: feature, area: engine, P3
depends: none

**Goal:** copy a data partition (used clusters only, using the copy map) to free space on another disk, e.g. to migrate to a new Azure managed disk.

**Acceptance:** VHDX tests across disks with different sector sizes (512e to 4Kn); the copy gets new identifiers so both can be online.
