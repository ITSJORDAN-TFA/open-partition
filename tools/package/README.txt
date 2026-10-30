Open Partition Manager - preview build for testing
====================================================

NO WARRANTY. Take a snapshot of the VM's disks before testing anything.

This build can only WRITE to VHD/VHDX virtual disk files. It refuses every real disk,
including Azure managed OS and data disks, so it cannot change this machine's partitions.

What's in the folder
--------------------
  OpenPartition.exe         GUI: disk map, details and refusals (read-only)
  opm.exe                   CLI: opm list | opm show <disk> | --json   (read-only)
  opm-engine.exe            move engine (VHD/VHDX only in this build)
  Test-OpmOnThisMachine.ps1 self-test that produces a report to send back

No .NET or Visual C++ runtime needs to be installed.

How to test (about 5 minutes)
-----------------------------
1. Copy this folder to the VM, e.g. C:\opm-preview  (not the D: temporary disk).
2. Open PowerShell as Administrator and run:

       cd C:\opm-preview
       Unblock-File *.exe, *.ps1
       powershell -ExecutionPolicy Bypass -File .\Test-OpmOnThisMachine.ps1

   It records what the tool sees on every disk (read-only), then creates a 1 GB
   throwaway VHDX next to the script, moves a partition on it twice (once killed
   part way and recovered), checks every file and deletes the VHDX.

3. Open OpenPartition.exe and have a look at the disk map.
4. Send back the report-<machine>-<time>.zip the script prints at the end. It contains
   disk layouts, sizes, labels and volume GUIDs, but no file contents.
