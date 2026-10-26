# Contributing to PLCSIM.UnitTest

Bug reports, documentation improvements, and focused pull requests are welcome.
The [architecture overview](docs/architecture/000-overview.md) introduces the
components and their responsibilities.

## Questions and Bug Reports

Use [Discussions](https://github.com/Lorenz-Software/PLCSIM.UnitTest/discussions)
for questions and ideas, and
[Issues](https://github.com/Lorenz-Software/PLCSIM.UnitTest/issues) for reproducible
problems. Check existing reports before opening a new one.

Include the following where relevant:

- PLCSIM.UnitTest application and installer versions or source commit.
- Access profile and binding versions, and whether the run used WPF or CLI.
- Windows version, vendor engineering/runtime versions, runtime type, and target architecture.
- A minimal configuration or steps to reproduce, including the starting conditions.
- Expected behavior and actual behavior.
- Assertion names/results and relevant logs or event captures, if available.
- When using a PLC testing framework: its name and version or source commit.

Report problems specific to PLC-side tests or framework behavior in the matching
[PLC framework repository](README.md#current-plc-targets).
For PLC/PC integration problems, include both sides' versions and observations.
Remove credentials and confidential project information from attachments.

## Pull Requests

- Keep each change focused on one problem or improvement.
- Explain the problem, resulting behavior, and checks performed. Link an existing report when available.
- Add or update relevant regression tests when application, profile, or tooling behavior changes.
- Update affected documentation and examples alongside the implementation.

Discuss changes to public APIs, lifecycle semantics, result handling, or PLC/PC
contracts before implementation. Follow the
[architecture](docs/architecture/000-overview.md) and
[access-profile runtime contract](docs/contracts/access-profile-runtime-contract.md).

Use English for code comments and documentation, and preserve the existing
formatting and line-ending rules in `.gitattributes`. Keep generated packages,
help archives, build output, and local engineering state out of commits.
Canonical contract binaries are committed inputs; follow the
[build foundation](docs/development/solution-and-build-foundation.md) when they
need to change.

## Validation

Run checks appropriate to the change:

- **Documentation:** verify Markdown formatting and affected links; run `git diff --check`.
- **Application and shared code:** follow the [build foundation](docs/development/solution-and-build-foundation.md) and run the relevant tests.
- **Access profiles and bindings:** follow the owning component's build and verification instructions under [access-profiles](access-profiles/README.md).
- **Tooling:** run the affected tool's documented offline checks.

Native PLC validation requires the environment documented by the selected
profile and explicit approval for a disposable target before changing runtime
state. Report offline builds, consumer checks, and runtime tests separately,
including their environment and results. State which checks you could not
perform.

## License

PLCSIM.UnitTest is provided under the [MIT license](LICENSE).
