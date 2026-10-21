using PLCSIM.UnitTest.Common.Operations;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class OperationResultContractTests
{
    [Fact]
    public void Operation_result_success_and_failure_helpers_set_expected_state()
    {
        var success = OperationResult.Success("ok");
        var failed = OperationResult.Failure("failed");

        Assert.True(success.IsSuccessful);
        Assert.False(success.IsUnsuccessful);
        Assert.Equal(OperationResultState.Succeeded, success.State);
        Assert.False(failed.IsSuccessful);
        Assert.True(failed.IsUnsuccessful);
        Assert.Equal(OperationResultState.Failed, failed.State);
    }

    [Fact]
    public void Generic_operation_result_preserves_payload()
    {
        var result = OperationResult<int>.Success(42);

        Assert.True(result.IsSuccessful);
        Assert.Equal(42, result.Value);
    }
}
