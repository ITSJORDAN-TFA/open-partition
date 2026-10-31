## What and why

<!-- What does this change, and why? Link the issue: "Fixes #123". -->

## How it was tested

<!-- Tick what applies and say on which Windows versions. -->

- [ ] `./build.ps1` passes (Rust + .NET + harness self-tests)
- [ ] `./build.ps1 -Integration` passes (needed for anything that writes to disks)
- [ ] New or changed tests cover this (unit, crash campaign, VHDX scenario, or xUnit)
- [ ] Tried by hand on a throwaway VHDX or VM, never a real disk
- [ ] Windows versions tried:

## Safety checklist (changes to the engine, journal, write gate or volume locking)

- [ ] Crash campaigns still pass, and new moving logic has new campaign coverage
- [ ] Journal layout unchanged, or `FORMAT_VERSION` bumped
- [ ] The write gate is not weakened (real disks only on Azure with a snapshot name)
- [ ] Failure messages say what happened, whether anything changed, and what to do next

## Screenshots

<!-- GUI changes: before / after. -->
