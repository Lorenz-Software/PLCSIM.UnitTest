# V8 test workers

The projects below compile the V8 production-worker sources into separately named test executables.
They are test infrastructure and must not be copied into an access-profile binding package or an
application deployment.

For build/test commands, start with the [binding README](../../README.md#verification).
Use [worker implementation](../../docs/implementation.md) for the production
boundary and [performance harness](../performance/README.md) for controlled runs.

## Compiler symbols

| Symbol | Owning project | Purpose |
| --- | --- | --- |
| `VALIDATION_WORKER` | `ValidationWorker` | Enables deterministic Runtime API validation and the synthetic performance harness. |
| `EVENTSTREAM_PERFORMANCE_MEASUREMENT` | `PerformanceMeasurementWorker` | Enables the controlled real-runtime payload-read gate and its measurement-only logging. |

Normal Debug and Release builds of the production V8 worker define neither symbol. Operational
timing and Runtime API call-count logging remains part of the production worker because it observes
normal behavior without changing EventStream processing.

The real-runtime payload-read test resolves `PerformanceMeasurementWorker` explicitly. It does not
replace the production worker binary and does not alter the default binding worker path.
