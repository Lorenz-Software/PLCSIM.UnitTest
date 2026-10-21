using System;
using System.Collections;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1.Worker
{
    internal sealed class WorkerSafetyProgramInspector
    {
        internal const string NotSupportedCode = "TiaOpenness.V15_1.Worker.SafetyProgramNotSupported";
        internal const string NotSupportedMessage =
            "TIA Openness V15.1 does not support the managed build and online transfer of a project target containing a Safety program.";

        private readonly ILogger logger;
        private readonly WorkerTargetDiscovery targetDiscovery;

        public WorkerSafetyProgramInspector(ILogger logger, WorkerTargetDiscovery targetDiscovery)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.targetDiscovery = targetDiscovery ?? throw new ArgumentNullException(nameof(targetDiscovery));
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
            var rootGroup = target.PlcSoftware.GetType().GetProperty("BlockGroup")?.GetValue(target.PlcSoftware, null)
                ?? throw new WorkerSafetyApiNotSupportedException();
            return new WorkerSafetyProgramInspection(ContainsSafetyBlock(rootGroup));
        }

        public void WriteNotSupported(StreamWriter writer, string operation)
        {
            logger.LogWarning(
                "Rejecting TIA Openness V15.1 {Operation} because the selected target contains a Safety program.",
                operation);
            WorkerProtocol.WriteError(logger, writer, NotSupportedCode, NotSupportedMessage);
        }

        private bool ContainsSafetyBlock(object group)
        {
            foreach (var block in ReadEnumerableProperty(group, "Blocks"))
            {
                var language = targetDiscovery.ReadStringProperty(block, "ProgrammingLanguage");
                if (language.StartsWith("F_", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(language, "F_CALL", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return ReadEnumerableProperty(group, "Groups").Any(ContainsSafetyBlock);
        }

        private static object[] ReadEnumerableProperty(object instance, string propertyName)
        {
            var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance, null) as IEnumerable;
            return value?.Cast<object>().ToArray() ?? Array.Empty<object>();
        }
    }

    internal sealed class WorkerSafetyProgramInspection
    {
        public WorkerSafetyProgramInspection(bool hasSafetyProgram)
        {
            HasSafetyProgram = hasSafetyProgram;
        }

        public bool HasSafetyProgram { get; }
    }

    internal sealed class WorkerSafetyTargetNotFoundException : Exception
    {
    }

    internal sealed class WorkerSafetyApiNotSupportedException : Exception
    {
    }
}