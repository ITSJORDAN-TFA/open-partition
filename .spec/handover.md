# Open Partition Manager — Agent Handover

Working title. A free, open-source GUI tool that lets Windows Server administrators view, resize and move partitions, including on Azure VMs, without paying for enterprise partition software.

## 1. Goals and non-goals

**Goals**

- Simple GUI for admins: see disks and partitions, then extend, shrink, move, create, delete and change drive letters.
- Data volumes can be changed online, with no reboot.
- System (C:) volume moves are supported through a boot-time component, behind clear warnings.
- Every operation that moves data is crash-safe: it can resume after a power loss, VM reboot or process kill.
- Works on Windows Server 2016 to 2025 and Windows 10/11, on physical machines, Hyper-V and Azure VMs.
- Licensed openly (MIT or GPLv3, to be decided) with an explicit no-warranty disclaimer.

**Non-goals for v1**

- Writing our own NTFS/ReFS resize engine. Resizing the file system is always left to Windows APIs.
- Dynamic disks, Storage Spaces, software RAID or ReFS moves. Detect them and refuse.
- Linux file systems and dual boot.
- Cloning or migrating disks.

## 2. Core design principle

Split the work so that each risky thing is done by the safest component available:

- **Resizing the file system** (shrink or extend NTFS): done by Windows, using FSCTL\_SHRINK\_VOLUME and FSCTL\_EXTEND\_VOLUME, or the Storage Management API (MSFT\_Partition.Resize, which is what Resize-Partition uses).
- **Moving a partition**: done by our engine as a raw, block-level sector copy, followed by a partition table update. The engine never interprets NTFS structures. A volume's contents don't depend on where it sits on the disk, so moving it as a block is file-system-agnostic.
- **Locked volumes, including C:**: the same engine code runs in a boot-time native executable, before anything has the volume open.

## 3. Proposed architecture

| Component | Tech (suggested) | Role |
| --- | --- | --- |
| GUI | C# .NET 8, WPF or WinUI 3 | Disk map view, operation queue, preview, progress, warnings |
| Service / CLI | C# or C++, runs elevated | Takes queued operations, calls the engine, reports progress over named pipes; a CLI too, for scripting |
| Engine core | C or C++ (or Rust with no\_std-compatible core) | Block mover, journal, partition table read/write. **Must compile both for Win32 and for the NT native subsystem** |
| Boot-time runner | Native NT app (ntdll only), registered in BootExecute | Runs the engine on locked or system volumes before Win32 starts |
| Test harness | PowerShell + Hyper-V + VHDX | Creates disk layouts, runs operations, injects faults, verifies hashes |

The engine core must not depend on the Win32 API, the CRT heap or C++ exceptions, so the same code works inside the native boot app. Abstract disk I/O, memory, logging and time behind a small platform layer with two implementations (Win32 and NT native).

## 4. Phased plan, lowest risk to highest

Each phase must meet its acceptance criteria and pass the test harness before the next one starts.

### Phase 0 — Foundations and test harness (no risk)

- Repo layout, build pipeline, coding standards, licence file.
- Test harness: PowerShell scripts that create VHDX disks with set layouts (GPT and MBR, 512 and 4K sector sizes, various partition orders), fill them with known data, and record SHA-256 hashes for each volume.
- Fault-injection support: hard-stop the Hyper-V VM at random points during an operation.
- **Acceptance:** one command builds everything and runs an empty test suite on a fresh Hyper-V host.

### Phase 1 — Read-only discovery and GUI (no risk)

- Enumerate disks, partitions and volumes using IOCTL\_DISK\_GET\_DRIVE\_GEOMETRY\_EX, IOCTL\_DISK\_GET\_DRIVE\_LAYOUT\_EX, IOCTL\_VOLUME\_GET\_VOLUME\_DISK\_EXTENTS and the Storage Management WMI classes.
- Show a visual disk map: partition type, file system, size, used space, drive letter, mount points, GPT attributes, and the gaps of free space.
- Detect and label anything the tool must refuse or warn about: BitLocker (via Win32\_EncryptableVolume), dynamic disks, Storage Spaces, ReFS, the EFI System, MSR and Recovery partitions, the active system/boot volume, page file and hibernation files, and VSS shadow storage.
- **Azure detection:** identify the temporary resource disk and label it clearly, since it's wiped on redeploy. Detect Azure through IMDS or SMBIOS and show a banner recommending a managed disk snapshot.
- **Acceptance:** accurate maps for every harness layout, matching diskpart and Get-Partition output. No write handles opened anywhere in this phase (enforce in code review and with a test).

### Phase 2 — Wrapping built-in Windows operations (low risk)

- Extend into adjacent free space, shrink from the end, create, delete, format, and assign or remove drive letters and mount points.
- Implement through the Storage Management API rather than raw IOCTLs, wherever possible.
- Operation queue: the user builds a list of changes, sees a before-and-after preview, then applies them.
- **Acceptance:** each operation gives the same result as the equivalent PowerShell cmdlet. Failed operations leave the disk unchanged and show the Windows error clearly.

### Phase 3 — Online move of data partitions (medium risk; the core feature)

Move a non-system data partition left or right into adjacent free space while Windows is running.

**Procedure**

1. Pre-flight checks: the partition isn't system, boot, page file, BitLocker, dynamic or Storage Spaces; the target range is truly free; enough room for the journal; the user has confirmed a backup or snapshot.
2. Stop dependents: list processes with open handles on the volume, then ask the user to stop the services involved, or offer to.
3. Take exclusive access: FSCTL\_LOCK\_VOLUME, then FSCTL\_DISMOUNT\_VOLUME. If the lock fails, abort cleanly. Don't force it in v1.
4. Write the journal (see section 5).
5. Copy the sectors in chunks (for example 16 to 64 MB). **The direction of the copy matters when the old and new ranges overlap:** moving right, copy from the end backwards; moving left, copy from the start forwards.
6. After each chunk: flush (FlushFileBuffers on the disk handle), then update the journal checkpoint.
7. When the copy is done, update the partition entry's starting offset using IOCTL\_DISK\_SET\_DRIVE\_LAYOUT\_EX, then IOCTL\_DISK\_UPDATE\_PROPERTIES. Keep the partition GUID and type the same, so the volume GUID, drive letter and mount points survive.
8. Unlock, let the volume remount, then verify (see section 6).
9. Mark the journal complete and remove it.

**Acceptance:** for every harness layout, moved volumes hash identically, keep the same drive letter and volume GUID, and pass chkdsk /scan. Fault-injection tests (100+ random hard-stops) all resume to a correct result.

### Phase 4 — Planner and composite operations (medium risk)

- "Make room" wizard: the user picks a partition and a target size, and the planner works out a sequence (shrink a neighbour, move partitions, extend) to get there.
- This is the exact case that started the project: extend a partition in the middle of the disk when the free space is at the end. Plan: move each partition after it rightwards, starting with the last, then extend the target.
- Each step journaled separately; the whole plan can resume.
- Show the total data to be moved and an estimated time based on measured disk throughput (important on Azure, where disk tiers limit throughput).
- **Acceptance:** the planner finds the minimum-data-moved plan for all harness scenarios, and a multi-step plan survives fault injection at any step.

### Phase 5 — Boot-time runner for locked data volumes (high risk)

For data volumes that can't be locked online (for example ones holding a page file, or used by services that can't be stopped).

- Build a native NT application (subsystem native, links only ntdll) that wraps the engine core. Use the NT API: NtCreateFile, NtReadFile, NtWriteFile, NtDeviceIoControlFile, NtDisplayString for output.
- Install: copy to System32, add an entry to HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\BootExecute after autocheck, write the job file, then reboot.
- The runner reads the job file, runs or resumes the journal, then **always removes its own BootExecute entry before doing any disk writes**, so a crash can never cause a boot loop. A crash during the operation is handled by a GUI-triggered resume, or by re-arming only when the journal says it's in progress.
- Progress output goes to the console and to a log file, readable through the Azure serial console and boot diagnostics.
- No kernel driver is needed. This avoids driver signing requirements.
- **Acceptance:** works on Hyper-V Gen 1 and Gen 2, and on an Azure VM. A deliberately broken job file results in a clean, normal boot. Fault-injection tests pass.

### Phase 6 — System (C:) volume moves (highest risk)

- Reuse the Phase 5 runner. C: isn't in use yet when BootExecute runs, so it can be moved the same way, though the runner itself is loaded from C:; this needs careful research and testing.
- After moving C: or the boot partition: update the BCD store so the device and osdevice entries point at the right partition (BCD usually references partitions by disk signature or GUID and offset, so this must be verified for both MBR and GPT); check the Windows Recovery Environment (reagentc) still works.
- Moving the EFI System partition or Recovery partition: out of scope for v1 unless needed. Prefer moving the partitions around them.
- Mandatory safeguards: a confirmation to type, a snapshot check on Azure (warn if no recent snapshot found), and a pre-flight check that all of the boot configuration can be read and parsed.
- **Acceptance:** a VM with C: moved left and right boots normally, Windows Update and recovery still work, and fault injection at any point leaves a VM that can either resume or be recovered from a snapshot.

### Phase 7 — Release and hardening

- Code signing for the installer and binaries (an Authenticode certificate; consider Azure Trusted Signing for cost).
- Installer, auto-update, crash reporting that can be switched off, and full documentation.
- An admin guide: always snapshot, Azure temp disk warning, what's supported and what's refused.
- Public test matrix showing which OS versions and layouts are verified.

## 5. Journal design (crash safety)

The journal is what makes this tool safe. Requirements:

- Stored **outside** the ranges being moved: in free space on the same disk, on a different volume, or both. Two copies with sequence numbers and CRC32 or SHA-256 checksums; the newest valid copy wins.
- Contents: operation ID, disk identity (signature or GUID plus size), source and target offsets, chunk size, copy direction, last completed chunk, and phase (copying, updating the table, verifying, done).
- Rule: a chunk is only marked complete after its data has been flushed to disk.
- **Overlapping moves:** where the chunk being copied overlaps its own destination, back up the source chunk to the journal area first, so a crash midway through writing that chunk can be undone.
- On start-up, the service and the boot runner both check for incomplete journals, and offer to (or automatically) resume before allowing anything else.

## 6. Verification

- Before a move: optionally hash the source range (slow but strongly recommended; on by default for volumes under a set size).
- After the move: hash the destination range and compare.
- After remounting: run chkdsk /scan (read-only) and check that the drive letter, volume GUID, shares and mount points are unchanged.
- Log everything to a readable file that admins can attach to bug reports.

## 7. Hard safety rules

- Refuse to run on unsupported layouts. Never "try anyway".
- Never write to a disk without an exclusive lock, or without being in boot-time mode.
- Never change the partition table before the data copy has been verified.
- Never remove the old partition entry until the new one is written and flushed.
- BitLocker: v1 requires the volume to be decrypted, or refuses. Research whether a raw move of a BitLocker volume with protection suspended is safe, and only allow it after it's proven in testing.
- Always show a backup or snapshot warning before any operation that moves data.

## 8. Open questions for the agent to research

1. Exact BCD fields that need updating after a C: move, for both MBR and GPT, and whether Windows fixes them on its own.
2. Whether a BitLocker volume can be moved block-for-block with protection suspended.
3. How the boot runner can best handle C: being moved while it was loaded from C: (run fully from memory, or load the image first).
4. Behaviour with Azure disk caching settings (ReadOnly or ReadWrite host caching) during large raw writes, and whether flushes are honoured through them.
5. VSS shadow copies: delete them, or refuse, before moving a volume that has them?
6. Handling 4Kn and 512e disks, and making sure alignment (1 MB boundaries) is kept.
7. The best licence for this project (MIT or GPLv3).

## 9. Suggested order of work for the agent

Complete Phases 0 to 2, release a read-only and basic-operations preview, then build Phase 3 with a heavy focus on testing. Phase 3 plus Phase 4 already solves the most common real-world problem (a data partition boxed in on an Azure VM). Phases 5 and 6 come only after Phase 3 has passed extensive fault-injection testing.
