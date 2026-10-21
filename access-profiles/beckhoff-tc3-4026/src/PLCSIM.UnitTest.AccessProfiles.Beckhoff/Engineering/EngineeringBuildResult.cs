using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;

internal sealed record EngineeringBuildResult(bool Succeeded, IReadOnlyList<EngineeringBuildMessage> Messages);

internal sealed record EngineeringBuildMessage(string Code, LogLevel Level, string Message);
