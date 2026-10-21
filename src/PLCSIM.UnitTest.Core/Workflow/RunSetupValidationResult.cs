using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.Core.Workflow;

public sealed record RunSetupValidationResult(
    bool IsValid,
    RunSetup? Setup,
    IReadOnlyList<Diagnostic> Diagnostics);
