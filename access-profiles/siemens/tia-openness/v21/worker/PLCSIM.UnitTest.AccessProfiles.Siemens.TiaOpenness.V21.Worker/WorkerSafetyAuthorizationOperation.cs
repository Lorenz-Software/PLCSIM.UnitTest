using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.Worker
{
    internal sealed class WorkerSafetyAuthorizationOperation
    {
        private readonly ILogger logger;
        private readonly WorkerExceptionMapper exceptionMapper;
        private readonly WorkerSafetyProgramInspector safetyProgramInspector;

        public WorkerSafetyAuthorizationOperation(
            ILogger logger,
            WorkerExceptionMapper exceptionMapper,
            WorkerSafetyProgramInspector safetyProgramInspector)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.exceptionMapper = exceptionMapper ?? throw new ArgumentNullException(nameof(exceptionMapper));
            this.safetyProgramInspector = safetyProgramInspector ?? throw new ArgumentNullException(nameof(safetyProgramInspector));
        }

        public void WriteRequirement(StreamWriter writer, object project, string targetId)
        {
            try
            {
                var inspection = safetyProgramInspector.Inspect(project, targetId);
                if (inspection.HasSafetyProgram)
                {
                    safetyProgramInspector.WriteNotSupported(writer, "Safety authorization requirement discovery");
                    return;
                }

                WorkerProtocol.WriteLine(
                    writer,
                    WorkerProtocol.CreateMessage("SafetyAuthorizationRequirementResult")
                    + "|status=" + WorkerProtocol.Escape("Succeeded")
                    + "|requirement=" + WorkerProtocol.Escape("NotRequired"));
            }
            catch (WorkerSafetyTargetNotFoundException)
            {
                WorkerProtocol.WriteError(
                    logger,
                    writer,
                    "TiaOpenness.V21.Worker.SafetyAuthorizationTargetNotFound",
                    "The selected PLC target is no longer available in the open TIA project.",
                    true);
            }
            catch (WorkerSafetyApiNotSupportedException)
            {
                WorkerProtocol.WriteError(
                    logger,
                    writer,
                    "TiaOpenness.V21.Worker.SafetyAuthorizationNotSupported",
                    "The installed TIA Openness V21 API does not provide offline Safety-program authorization.");
            }
            catch (Exception ex)
            {
                exceptionMapper.WriteExceptionError(
                    writer,
                    ex,
                    "TiaOpenness.V21.Worker.SafetyAuthorizationRequirementFailed",
                    "discover the offline Safety-program authorization requirement");
            }
        }

        public void WriteAuthorization(
            StreamWriter writer,
            object project,
            WorkerProjectOwner projectOwner,
            string targetId,
            string protectedCredential)
        {
            try
            {
                var inspection = safetyProgramInspector.Inspect(project, targetId);
                if (inspection.HasSafetyProgram)
                {
                    safetyProgramInspector.WriteNotSupported(writer, "Safety authorization");
                    return;
                }

                WriteAuthorizationResult(writer);
            }
            catch (WorkerSafetyTargetNotFoundException)
            {
                WorkerProtocol.WriteError(
                    logger,
                    writer,
                    "TiaOpenness.V21.Worker.SafetyAuthorizationTargetNotFound",
                    "The selected PLC target is no longer available in the open TIA project.",
                    true);
            }
            catch (WorkerSafetyApiNotSupportedException)
            {
                WorkerProtocol.WriteError(
                    logger,
                    writer,
                    "TiaOpenness.V21.Worker.SafetyAuthorizationNotSupported",
                    "The installed TIA Openness V21 API does not provide offline Safety-program authorization.");
            }
            catch (Exception ex)
            {
                var rootException = ex.GetBaseException();
                var failure = exceptionMapper.ClassifyException(
                    rootException,
                    "TiaOpenness.V21.Worker.SafetyAuthorizationRejected",
                    "authorize the offline Safety program");
                if (failure.SessionInvalidated)
                {
                    WorkerProtocol.WriteError(logger, writer, failure.Code, failure.Message, true);
                    return;
                }

                logger.LogWarning(
                    "Offline Safety-program authorization was rejected. ExceptionType={ExceptionType}",
                    rootException.GetType().FullName);
                WorkerProtocol.WriteError(
                    logger,
                    writer,
                    "TiaOpenness.V21.Worker.SafetyAuthorizationRejected",
                    "TIA Openness rejected the offline Safety-program authorization. Verify the configured Safety password and retry.");
            }
        }

        private static void WriteAuthorizationResult(StreamWriter writer)
        {
            WorkerProtocol.WriteLine(
                writer,
                WorkerProtocol.CreateMessage("SafetyAuthorizationResult")
                + "|status=" + WorkerProtocol.Escape("Succeeded")
                + "|authorized=" + WorkerProtocol.Escape("true"));
        }

    }
}
