# Contributing to Open Partition Manager

Thanks for helping. This tool moves partitions on servers, so a bug here can destroy data.
Correctness and safety come before features and speed.

## Ground rules

- **Never point disk-writing code at a real disk while developing.** Use throwaway VHD/VHDX
  disks (the test harness creates them) or a disposable VM. The engine refuses real disks
  unless it runs on an Azure VM *and* is given a snapshot name; don't weaken that gate in a PR.
- **Every change to the mover must keep the crash guarantees.** The simulation crash campaigns
  (`opm-platform-sim`) cut power at every write and must keep passing. New moving logic needs
  new campaign coverage.
- **Refuse rather than guess.** If the tool can't be sure something is safe (BitLocker state
  unknown, a volume can't be locked, an unexpected layout), it refuses and says why. Keep it
  that way.

## Getting started

Requirements: Windows 10/11 or Server 2016+, PowerShell 7.2+, .NET 10 SDK, Rust stable (MSVC
toolchain) with clippy. Integration tests also need an elevated session with Hyper-V enabled.

```powershell
git clone https://github.com/ITSJORDAN-TFA/open-partition.git
cd open-partition
./build.ps1                       # build + all fast tests (Rust, .NET, harness self-tests)
./build.ps1 -Integration          # also the VHDX tests (elevated, Hyper-V)
./tools/package/Build-Package.ps1 # the self-contained zip in artifacts/
```

Layout:

| Path | What |
|---|---|
| `src/engine/opm-core` | `no_std` journaled mover and recovery state machine |
| `src/engine/opm-platform-sim` | simulated disks with power loss, crash campaigns |
| `src/engine/opm-platform-win32` | raw disk I/O, partition tables, volume locks, write gate |
| `src/engine/opm-engine-cli` | `opm-engine.exe`, driven by the GUI and CLI |
| `src/dotnet/OpenPartition.Discovery` | read-only discovery and the safety classifier |
| `src/dotnet/OpenPartition.Operations` | planner, executor, engine runner |
| `src/dotnet/OpenPartition.Cli` / `.Gui` | `opm.exe` and `OpenPartition.exe` (WPF) |
| `tests/harness` | VHDX layouts and integration suites |
| `tools/package` | packaging and the on-machine self-test |

## Making a change

1. **Open an issue first** for anything bigger than a small fix, so we can agree on the
   approach. This matters most for changes to the engine, journal format or write gate.
2. Branch from `main`. Keep each PR to one concern.
3. Add tests:
   - engine logic: unit tests plus a crash campaign in `opm-platform-sim`;
   - Windows behaviour: a VHDX scenario in `tests/harness/suites`;
   - planner and GUI logic: xUnit tests in `tests/dotnet`.
4. Run `./build.ps1` (and `-Integration` if you touched disk-writing code) before opening the PR.
5. Fill in the PR template, including how you tested and on which Windows versions.

### Style

- Rust: `cargo fmt`, `cargo clippy -- -D warnings`. `unsafe` only in the platform crates, each
  block with a `// SAFETY:` comment.
- .NET: the analyzers run with warnings as errors; follow the existing code style.
- Messages a user sees say what happened, whether anything changed, and what to do next.
  Avoid jargon in the GUI.
- Changing the journal record layout means bumping `FORMAT_VERSION`.

### Commit messages

Use a type prefix in parentheses, then a short summary in the imperative:

```
(feat): undo an interrupted move from the GUI
(fix): keep drive letters when a logical drive moves
(test): crash campaign for copy maps
(docs): compatibility matrix for Server 2025
(build): publish the preview release from main
(refactor): split the planner's gap logic
```

## Reporting bugs and compatibility results

Use the issue templates. For anything disk-related, attach the report zip from
`Test-OpmOnThisMachine.ps1` (it only creates throwaway VHDX disks) and the output of
`opm list`. **Never post snapshot names, credentials or customer data.**

Security problems: see [SECURITY.md](SECURITY.md). Please don't open a public issue.

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
