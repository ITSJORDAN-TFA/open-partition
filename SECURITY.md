# Security policy

Open Partition Manager runs elevated and writes raw disk sectors, so we treat these as
security issues:

- any way to make it write to a disk the write gate should refuse;
- anything that corrupts data, or makes a move unrecoverable, outside a documented limitation;
- code execution or privilege escalation through the tool, its journal or copy-map files, or
  the package;
- tampering with releases or the build pipeline.

## Reporting

Please **don't open a public issue.** Report it privately through GitHub:
**Security → Report a vulnerability** on this repository. Include the version, the Windows
version, what you did, and what happened. A VHDX or a report from `Test-OpmOnThisMachine.ps1`
that reproduces it helps a lot.

We aim to acknowledge reports within a few days, fix confirmed issues in a new release, and
credit you if you'd like.

## Supported versions

Only the latest release, and the `preview` build from `main`, get fixes while the project is
at 0.x.

## Verifying downloads

Each release asset has a `.sha256` file. Compare it with `Get-FileHash <zip>` before running
anything. Builds are made only by GitHub Actions in this repository.
