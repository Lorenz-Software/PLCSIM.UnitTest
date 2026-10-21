# Siemens Access Profile Troubleshooting

This document routes Siemens profile setup and runtime problems to the owning
layer. Concrete product versions, vendor API files, and worker-specific errors
belong to the selected binding documentation.

## Initial Checks

1. Confirm that the Siemens profile package and required binding packages are
   deployed below the same profile root used by the application.
2. Run `register-profiles` from the Windows account that will use the
   application after package installation, replacement, or removal. Generic
   profile registration does not require elevation.
3. Use `list-profiles` to inspect the persisted application registry without
   loading profile implementations.
4. Check the application diagnostic first, then correlate it with the
   access-profile and worker logs from the same operation time.

Registration does not authorize TIA Openness workers. TIA authorization and
application profile registration are separate administrative steps and must be
completed before unattended execution.

## Profile Availability

The Siemens profile can register for external project handling when a usable
PLCSIM binding exists even if no usable TIA Openness binding is available.
Managed project handling additionally requires a binding that advertises the
selected project source and is ready in the current environment.

If the profile is missing or unavailable:

- verify that the application and registration command use the same profile
  discovery directory and registry path;
- inspect the profile registry again after explicit registration;
- distinguish a normally absent product from a selected package whose registry
  or API evidence is stale or inaccessible;
- continue with the concrete binding troubleshooting page for the unavailable
  capability.

The profile does not contain hard-coded assumptions about installed concrete
binding versions. Do not repair selection by copying vendor assemblies into the
application directory or by adding application references to a binding.

## Logs And Diagnostics

The application owns the log directory and logging policy. The Siemens profile
uses an `access-profile-siemens-<date>.log` family; process-isolated bindings
write separate worker log families in the same directory. The default
application location is:

```text
%LOCALAPPDATA%\PLCSIM.UnitTest\logs
```

Use application diagnostics for the user-visible failure classification and
logs for technical evidence such as selected binding, worker process, session,
runtime, and operation correlation. Standard error is only fallback evidence
for fatal worker/logger failures. Do not treat it as the regular worker log.

## Recovery Boundary

Use application Back, End Session, or the explicit runtime reconnect operation
according to the current workflow state. A failed or invalidated binding
session is not repaired by editing registry files, replacing package files
while the application is running, or repeatedly issuing the failed command.

When package binaries change, end the application session, redeploy the complete
package, repeat required TIA authorization for the exact worker binary, and
rerun administrative profile registration before retrying.

## Binding-Specific Guidance

Use the troubleshooting document shipped by the selected concrete binding for
product installation, API readiness, worker startup, vendor error codes, and
real-runtime recovery. The Siemens profile deliberately does not duplicate
those version-specific details.
