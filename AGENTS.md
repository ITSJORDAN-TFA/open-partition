# Working on Open Partition Manager as an agent

This file tells coding agents (and people working the same way) how to pick up roadmap work and
finish it. Read [CONTRIBUTING.md](CONTRIBUTING.md) too: its safety rules apply in full.

## The loop

1. **Pick an issue.** Open the pinned **Roadmap** issue. Take the lowest-numbered open issue that
   - is labelled `agent-ready`,
   - has every issue listed under **Depends on** closed, and
   - isn't assigned to anyone and has no open PR linked.

   Skip issues labelled `needs-maintainer` (they need accounts, hardware or decisions only the
   maintainer has).
2. **Claim it.** Assign yourself, or comment "Working on this", so two agents don't collide.
3. **Branch** from the latest `main`: `feat/<roadmap-key>` (the key is at the bottom of the
   issue), or `fix/…`, `spike/…`.
4. **Do the work** the issue describes. Meet every acceptance criterion; if one turns out to be
   wrong or impossible, say so in the PR rather than quietly dropping it.
5. **Test.**
   - Always: `./build.ps1` (Rust fmt and clippy, all unit tests and crash campaigns, .NET tests,
     harness self-tests).
   - If you change anything that writes to disks: `./build.ps1 -Integration` in an elevated
     session with Hyper-V. If you can't run it, say so in the PR and label it
     `needs-hyperv-vm`.
   - Issues labelled `needs-azure` need a run on an Azure VM before they close. Deliver the
     change, plus a self-test round in `tools/package/Test-OpmOnThisMachine.ps1` that proves it,
     and ask the maintainer to run it.
6. **Open a PR** using the template, with `Fixes #<n>` in the description. One issue per PR.
7. **Unblock the next work.** When your issue closes, check its **Blocks** list. For each one
   whose dependencies are now all closed, swap the `blocked` label for `agent-ready`.
8. **Record decisions.** Spikes end with a write-up in `docs/research/` and a short note in
   `.spec/plan.md`. Anything else you learn that future work needs (a Windows quirk, a gap) goes
   in `.spec/plan.md` too.

Then go back to step 1.

## Rules that never bend

- **Never write to a real disk.** Not on your machine, not on a CI runner. Use the harness's
  throwaway VHDX disks or disposable VMs. The engine's write gate (VHDX only, or an Azure VM with
  a named snapshot) must not be weakened, bypassed or "temporarily" disabled.
- **Crash safety first.** Any change to how data is moved keeps the journal guarantees. Add
  crash-campaign coverage (`src/engine/opm-platform-sim/tests`) for new write paths, and bump
  `FORMAT_VERSION` if the journal record changes.
- **Refuse rather than guess.** When safety can't be established, refuse with a reason a server
  admin understands, and say what to do next.
- **No AI attribution** in commits or PRs (no `Co-Authored-By` trailers or "generated with" lines).
  Commit messages use the `(type): summary` style from CONTRIBUTING.md.
- Don't merge your own PR. The maintainer reviews and merges.

## Where things are

| Area | Path |
|---|---|
| Plan, decisions, known gaps | `.spec/plan.md` |
| Roadmap seed (issue text) | `docs/roadmap/issues.md`, applied with `tools/roadmap/New-RoadmapIssues.ps1` |
| Mover core and crash tests | `src/engine/opm-core`, `src/engine/opm-platform-sim` |
| Windows disk layer | `src/engine/opm-platform-win32` |
| Engine CLI the apps drive | `src/engine/opm-engine-cli` |
| Discovery and safety rules | `src/dotnet/OpenPartition.Discovery` (`Classifier.cs`) |
| Planner, executor, engine runner | `src/dotnet/OpenPartition.Operations` |
| CLI / GUI | `src/dotnet/OpenPartition.Cli`, `src/dotnet/OpenPartition.Gui` |
| VHDX layouts and suites | `tests/harness` |
| On-machine self-test (Azure) | `tools/package/Test-OpmOnThisMachine.ps1` |

## Adding roadmap items

Add a section to `docs/roadmap/issues.md` (after anything it depends on) and run
`tools/roadmap/New-RoadmapIssues.ps1`. It creates only the new issues and refreshes links and the
Roadmap list. Or create the issue on GitHub directly and add it to the Roadmap issue by hand.
