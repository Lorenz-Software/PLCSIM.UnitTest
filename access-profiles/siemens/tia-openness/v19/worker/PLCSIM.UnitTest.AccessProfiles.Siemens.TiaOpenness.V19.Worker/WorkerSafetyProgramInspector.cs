using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19.Worker
{
    internal sealed class WorkerSafetyProgramInspector
    {
        internal const string NotSupportedCode = "TiaOpenness.V19.Worker.SafetyProgramNotSupported";
        internal const string NotSupportedMessage =
            "TIA Openness V19 does not support the managed build and online transfer of a project target containing a Safety program.";
        private const string SafetyAdministrationTypeName = "Siemens.Engineering.Safety.SafetyAdministration";

        private readonly ILogger logger;
        private readonly WorkerTargetDiscovery targetDiscovery;
        private readonly WorkerReflectionAccess reflectionAccess;

        public WorkerSafetyProgramInspector(
            ILogger logger,
            WorkerTargetDiscovery targetDiscovery,
            WorkerReflectionAccess reflectionAccess)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.targetDiscovery = targetDiscovery ?? throw new ArgumentNullException(nameof(targetDiscovery));
            this.reflectionAccess = reflectionAccess ?? throw new ArgumentNullException(nameof(reflectionAccess));
        }

        public WorkerSafetyProgramInspection Inspect(object project, string targetId)
        {
            var target = targetDiscovery.ProbeTargetContexts(project)
                .SingleOrDefault(candidate => string.Equals(candidate.Target.Id, targetId, StringComparison.Ordinal));
            if (target == null)
            {
                throw new WorkerSafetyTargetNotFoundException();
            }

            return Inspect(target);
        }

        public WorkerSafetyProgramInspection Inspect(TiaTargetContext target)
        {
            EnsureSafetyAssemblyLoaded();
            var safetyAdministration = reflectionAccess.ResolveService(
                target.DeviceItem,
                SafetyAdministrationTypeName);
            if (safetyAdministration == null)
            {
                return new WorkerSafetyProgramInspection(false, null);
            }

            var runtimeGroups = safetyAdministration.GetType()
                .GetProperty("RuntimeGroups")
                ?.GetValue(safetyAdministration, null)
                ?? throw new WorkerSafetyApiNotSupportedException();
            var anyMethod = runtimeGroups.GetType().GetMethod("Any", Type.EmptyTypes);
            if (anyMethod != null && anyMethod.Invoke(runtimeGroups, null) is bool any)
            {
                return new WorkerSafetyProgramInspection(any, safetyAdministration);
            }

            var count = runtimeGroups.GetType().GetProperty("Count")?.GetValue(runtimeGroups, null);
            if (count is int typedCount)
            {
                return new WorkerSafetyProgramInspection(typedCount > 0, safetyAdministration);
            }

            throw new WorkerSafetyApiNotSupportedException();
        }

        public void WriteNotSupported(StreamWriter writer, string operation)
        {
            logger.LogWarning(
                "Rejecting TIA Openness V19 {Operation} because the selected target contains a Safety program.",
                operation);
            WorkerProtocol.WriteError(logger, writer, NotSupportedCode, NotSupportedMessage);
        }

        private static void EnsureSafetyAssemblyLoaded()
        {
            if (!AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetType(SafetyAdministrationTypeName, false) != null))
            {
                throw new WorkerSafetyApiNotSupportedException();
            }
        }
    }

    internal sealed class WorkerSafetyProgramInspection
    {
        public WorkerSafetyProgramInspection(bool hasSafetyProgram, object? safetyAdministration)
        {
            HasSafetyProgram = hasSafetyProgram;
            SafetyAdministration = safetyAdministration;
        }

        public bool HasSafetyProgram { get; }

        public object? SafetyAdministration { get; }
    }

    internal sealed class WorkerSafetyTargetNotFoundException : Exception
    {
    }

    internal sealed class WorkerSafetyApiNotSupportedException : Exception
    {
    }
}
