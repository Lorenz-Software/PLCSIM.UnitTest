# TwinCAT Licensing

This describes vendor prerequisites and verified access capabilities. The
profile does not provide automatic license activation or renewal.

## Runtime Requirements

Ordinary Usermode execution (TC1700) does not add a Usermode license requirement.
The executed program still needs its runtime licenses, notably TC1200 for PLC
execution and any additional functions it uses. TC1701 covers externally driven
execution ticks, not ordinary ADS communication; TC1702 covers Fast As Possible
execution. Beckhoff describes licensing as device-based, allowing real-time and
Usermode runtimes on the same device to use the same applicable licenses.
See [Usermode licensing](https://infosys.beckhoff.com/content/1033/tc170x_tc3_usermode_runtime/11319883275.html)
and [execution modes](https://infosys.beckhoff.com/content/1033/tc170x_tc3_usermode_runtime/11319881355.html).

A distinct AMS Net ID identifies the runtime, not a separate purchased license.
Nevertheless, the selected runtime must actually have access to the applicable
licenses. A valid license on another runtime or an existing response file alone
is not proof of that availability.

## Trials And CI

Beckhoff's documented trial-creation procedure requires XAE and interactive
security-code entry. The Runtime itself cannot generate trials. No supported
unattended generation or renewal path was established in the reviewed public
Automation Interface and installed APIs. See [license types](https://infosys.beckhoff.com/content/1033/tc3_licensing/955881355.html)
and [trial creation](https://infosys.beckhoff.com/content/1033/tc3_licensing/3510308491.html).

Existing valid trials can be used for execution; this does not make renewal
unattended. Continuous CI without manual renewal requires appropriate permanent
licenses. Additional functions may have their own product-specific conditions.
WPF stays vendor-neutral: users activate licenses in XAE, and the profile reports
failures through existing diagnostics. No automated security-code handling or
license activation dialog is part of this integration.

Creating a Runtime process alone is not sufficient for a usable CI target: that
instance needs access to valid licenses. Trial activation was a manual step for
the isolated test Runtime. The verified access paths cannot automate that step,
so unattended per-job creation of a newly trial-licensed Runtime is not a viable
integration path. This is a licensing/provisioning limitation, not merely a scope
preference. Device-based licensing does not establish automatic license
availability in a fresh Runtime configuration.

Consequently, the profile targets existing Runtime instances in both WPF and
CLI/CI. Operators provision those instances and their required licenses before
use; the profile does not create or delete Runtime instances per job.
Application-managed project build and download into the selected existing Runtime
remain in scope.

The Automation Interface can [import an existing license response](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/4554565515.html).
That operation does not generate a trial or extend its validity. It has not been
selected as an implicit step of runtime creation, download or reconnect.

## Status And Diagnostics

The target's ADS license server on port 30 supports online license queries without
opening XAE. A read-only probe against the installed Usermode Runtime verified
valid trial status, expiration data and a missing-license result for a separate
unused product. Beckhoff also publishes [a license-status client](https://github.com/Beckhoff/ADS/blob/master/AdsLib/LicenseAccess.cpp)
using this service. This is separate from the COM license-descriptor catalogue,
which describes products rather than proving their online validity.

Distinguish transport success from the returned license status. Relevant failures
include missing (0x724), expired (0x725), invalid (0x727), wrong System ID (0x728),
trial denied (0x732), and wrong platform (0x736). Preserve the operation, target,
vendor code and product identity where available; an unreachable server is not
proof of a missing license. See [vendor error definitions](https://infosys.beckhoff.com/content/1033/tcplclib_tc2_utilities/374277003.html).

Only licenses required by the selected operation/project matter. Absence of an
unused product must not reject an otherwise usable target. An inventory of valid
licenses does not itself establish every project dependency. Exact preflight
query timing remains an implementation-design concern;
license inspection does not belong in each event-buffer read.

Real-target checks have not yet qualified permanent/dongle licenses, expiration
during a run, activation/import or fresh-runtime provisioning. No production
dependency or application contract is implied by the temporary feasibility probe.

A managed-transfer check also observed an XAE `license not found` dialog during
system activation. The profile observed CONFIG rather than confirmed RUN and
reported a transfer-readiness timeout. The modal vendor dialog prevented normal
XAE cleanup (`RPC_E_CALL_REJECTED`), followed by a profile-unload timeout; the CLI
reported CleanupFailed, not a successful test run. The application diagnostics
did not identify the missing license product or prove that it had expired.
Restoring usable licensing manually allowed the same packaged workflow to pass.
Do not assume every vendor license failure produces a specific headless license
diagnostic or that a blocked XAE will always close automatically. Validate the
selected runtime's required licenses before unattended execution.
