Open Partition Manager - preview build for testing
====================================================

NO WARRANTY. Partition changes can destroy data. Take a snapshot first.

What this build may change
--------------------------
  - VHD/VHDX test disks: always.
  - Real disks: ONLY on an Azure VM, and only after you name a snapshot of the disk you took
    first. The engine checks for Azure itself; on any other machine real disks are refused.

What's in the folder
--------------------
  OpenPartition.exe         GUI: disk map, Move / Extend, planned changes, Apply, Resume
  opm.exe                   CLI: opm list | show | move | extend | resume   (opm help)
  opm-engine.exe            the move engine (journaled; survives a crash or restart)
  Test-OpmOnThisMachine.ps1 self-test on throwaway VHDX disks; produces a report

No .NET or Visual C++ runtime needs to be installed. Run everything as Administrator.

1. Self-test first (about 5 minutes, changes nothing real)
----------------------------------------------------------
Copy this folder to the VM, e.g. C:\opm-preview (not the D: temporary disk), then in an
elevated PowerShell:

    cd C:\opm-preview
    Unblock-File *.exe, *.ps1
    powershell -ExecutionPolicy Bypass -File .\Test-OpmOnThisMachine.ps1

Send back the report-<machine>-<time>.zip it prints at the end (disk layouts and volume
GUIDs, no file contents).

2. Making room to extend a partition (e.g. C:)
-----------------------------------------------
Windows can only extend a volume into free space directly after it. If other partitions sit
between it and the free space, move them towards the end of the disk first, starting with
the one nearest the free space.

  a. In the Azure portal, take a snapshot of the VM's disk. Note its name.
  b. Stop the programs and services that use the partitions that will move, and close any
     Explorer windows on them. The tool never forces a volume closed: if something still has
     it open, the move stops before writing anything and tells you which volume.
  c. Start OpenPartition.exe. For each partition between the volume and the free space,
     starting with the one nearest the free space:
        select it -> Move... -> "End of the free space" -> Add to plan
     An MBR extended partition moves with all its logical drives; select the "Extended" box.
  d. Select the volume to grow -> Extend... -> "Use all the free space" -> Add to plan.
  e. The disk map now shows how the disk will look (changed partitions have a green border).
     Nothing has been changed yet. Click Apply..., enter the snapshot name, tick the box,
     and Apply.

Moving copies the data of each partition (free NTFS space is skipped) and verifies every
chunk against a fingerprint taken first, so it takes a while, and longer on VM sizes with
low disk throughput. Drive letters and volume GUIDs are kept. Moving the same partition
twice in the plan is merged into one move.
If something writes to the data underneath a move, the move stops at that point and, when
the move never overwrote its original location, undoes itself: the partition is put back.
If the machine restarts or the program is closed part way, nothing is lost: start
OpenPartition.exe again and click Resume on the red banner (or run: opm resume --yes), or
Undo to put the partition back where it was (opm undo --yes).
