using PLCSIM.UnitTest.Common.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class AccessProfileSelectionContractTests
{
    [Fact]
    public void Default_project_source_evaluation_is_unsupported()
    {
        IAccessProfilePlugin plugin = new MinimalAccessProfilePlugin();
        var result = plugin.EvaluateProjectSource("sample.ap21");

        Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, result.State);
        Assert.Contains("does not implement project source evaluation", result.Message, StringComparison.Ordinal);
    }

    private sealed class MinimalAccessProfilePlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new(
            "minimal",
            "Minimal",
            "1.0.0",
            "Minimal contract test profile.");

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, []);
        }

        public void Initialize(AccessProfileContext context)
        {
        }

        public Task<AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
