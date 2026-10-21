using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.Worker
{
    internal sealed class WorkerExceptionMapper
    {
        private readonly ILogger logger;

        public WorkerExceptionMapper(ILogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal void WriteExceptionError(
            StreamWriter writer,
            Exception exception,
            string fallbackCode,
            string operation)
        {
            var rootException = exception.GetBaseException();
            logger.LogError(
                rootException,
                "TIA Openness operation failed. Operation={Operation} ExceptionType={ExceptionType}",
                operation,
                rootException.GetType().FullName);
            var failure = ClassifyException(rootException, fallbackCode, operation);
            WorkerProtocol.WriteError(logger, writer, failure.Code, failure.Message, failure.SessionInvalidated);
        }


        internal WorkerFailure ClassifyException(
            Exception exception,
            string fallbackCode,
            string operation)
        {
            var rootException = exception.GetBaseException();
            var exceptionType = rootException.GetType().FullName ?? rootException.GetType().Name;
            var detail = WorkerProtocol.BoundText(rootException.Message, 1024);

            if (string.Equals(
                exceptionType,
                "Siemens.Engineering.EngineeringSecurityException",
                StringComparison.Ordinal))
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.SecurityDenied",
                    "TIA Openness denied access. Ensure the current Windows user belongs to the local 'Siemens TIA Openness' group and approve the deployed worker executable in the TIA Openness firewall/AllowList. Details: " + detail,
                    false);
            }

            if (rootException is UnauthorizedAccessException)
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.AccessDenied",
                    "Access was denied while attempting to " + operation + ". Verify file-system and TIA Openness permissions. Details: " + detail,
                    false);
            }

            if (string.Equals(
                exceptionType,
                "Siemens.Engineering.NonRecoverableException",
                StringComparison.Ordinal))
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.NonRecoverableFailure",
                    "TIA Portal reported a non-recoverable failure while attempting to " + operation + "; the engineering session must be reopened. Details: " + detail,
                    true);
            }

            if (string.Equals(
                exceptionType,
                "Siemens.Engineering.EngineeringObjectDisposedException",
                StringComparison.Ordinal)
                || rootException is ObjectDisposedException)
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.EngineeringObjectDisposed",
                    "A TIA Openness object required to " + operation + " is no longer available; the engineering session must be reopened. Details: " + detail,
                    true);
            }

            if (string.Equals(
                exceptionType,
                "Siemens.Engineering.EngineeringUserAbortException",
                StringComparison.Ordinal))
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.OperationCanceledByUser",
                    "TIA Portal reported that the operation was canceled by the user. Details: " + detail,
                    false);
            }

            if (string.Equals(
                exceptionType,
                "Siemens.Engineering.EngineeringNotSupportedException",
                StringComparison.Ordinal))
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.OperationNotSupported",
                    "TIA Openness does not support the requested operation in the selected context. Details: " + detail,
                    false);
            }

            if (string.Equals(
                exceptionType,
                "Siemens.Engineering.EngineeringOutOfMemoryException",
                StringComparison.Ordinal))
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.EngineeringResourcesExhausted",
                    "The associated TIA Portal instance does not have enough resources to " + operation + ". Details: " + detail,
                    false);
            }

            if (rootException is FileLoadException
                or FileNotFoundException
                or BadImageFormatException
                or TypeLoadException)
            {
                return new WorkerFailure(
                    "TiaOpenness.V21.AssemblyLoadFailed",
                    "The TIA Openness V21 API assemblies could not be loaded with the required identity. Verify the selected PublicAPI installation. Details: " + detail,
                    false);
            }

            return new WorkerFailure(
                fallbackCode,
                "TIA Openness failed to " + operation + ". Details: " + detail,
                false);
        }


        internal sealed class WorkerFailure
        {
            public WorkerFailure(string code, string message, bool sessionInvalidated)
            {
                Code = code;
                Message = message;
                SessionInvalidated = sessionInvalidated;
            }

            public string Code { get; }

            public string Message { get; }

            public bool SessionInvalidated { get; }
        }


    }
}
