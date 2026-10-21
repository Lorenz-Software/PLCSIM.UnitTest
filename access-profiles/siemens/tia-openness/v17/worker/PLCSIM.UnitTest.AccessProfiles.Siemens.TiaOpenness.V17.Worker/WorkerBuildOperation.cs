using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.Worker
{
    internal sealed class WorkerBuildOperation
    {
        private readonly ILogger logger;
        private readonly WorkerExceptionMapper exceptionMapper;
        private readonly WorkerTargetDiscovery targetDiscovery;
        private readonly WorkerReflectionAccess reflectionAccess;
        private readonly WorkerSafetyProgramInspector safetyProgramInspector;

        public WorkerBuildOperation(ILogger logger, WorkerExceptionMapper exceptionMapper, WorkerTargetDiscovery targetDiscovery, WorkerReflectionAccess reflectionAccess, WorkerSafetyProgramInspector safetyProgramInspector)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.exceptionMapper = exceptionMapper ?? throw new ArgumentNullException(nameof(exceptionMapper));
            this.targetDiscovery = targetDiscovery ?? throw new ArgumentNullException(nameof(targetDiscovery));
            this.reflectionAccess = reflectionAccess ?? throw new ArgumentNullException(nameof(reflectionAccess));
            this.safetyProgramInspector = safetyProgramInspector ?? throw new ArgumentNullException(nameof(safetyProgramInspector));
        }

        public void WriteResult(StreamWriter writer, object project, string targetId)
        {
            logger.LogInformation(
                "Starting TIA Openness build operation. TargetId={TargetId}",
                targetId);
            try
            {
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V17.Worker.BuildTargetMissing", "Build target id is required.");
                    return;
                }

                var target = targetDiscovery.ProbeTargetContexts(project)
                    .FirstOrDefault(context => string.Equals(context.Target.Id, targetId, StringComparison.Ordinal));
                if (target is null)
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V17.Worker.BuildTargetNotFound", "Build target was not found in the opened TIA project.");
                    return;
                }

                if (safetyProgramInspector.Inspect(target).HasSafetyProgram)
                {
                    safetyProgramInspector.WriteNotSupported(writer, "build");
                    return;
                }

                var compilable = ResolveCompilable(target.Device);
                if (compilable is null)
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V17.Worker.BuildTargetNotCompilable", "The TIA device containing the selected PLC target does not provide a compiler service.");
                    return;
                }

                var compile = compilable.GetType().GetMethod("Compile", Type.EmptyTypes)
                    ?? throw new InvalidOperationException("TIA Openness compiler service does not provide Compile().");
                var result = compile.Invoke(compilable, null)
                    ?? throw new InvalidOperationException("TIA Openness compiler returned no result.");

                foreach (var evidence in CreateBuildEvidence(target.Target, result))
                {
                    WorkerProtocol.WriteLine(
                        writer,
                        WorkerProtocol.CreateMessage("BuildEvidence")
                        + "|id=" + WorkerProtocol.Escape(evidence.Id)
                        + "|kind=" + WorkerProtocol.Escape(evidence.Kind)
                        + "|summary=" + WorkerProtocol.Escape(WorkerProtocol.BoundText(evidence.Summary, WorkerProtocol.MaximumOperationMessageLength)));
                }

                var messages = reflectionAccess.ReadOperationMessages(result, out var messagesTruncated);
                for (var index = 0; index < messages.Count; index++)
                {
                    WriteBuildDiagnostic(writer, messages[index], index);
                }
                if (messagesTruncated)
                {
                    WorkerProtocol.WriteLine(
                        writer,
                        WorkerProtocol.CreateMessage("BuildDiagnostic")
                        + "|code=" + WorkerProtocol.Escape("TiaOpenness.V17.CompilerMessagesTruncated")
                        + "|severity=" + WorkerProtocol.Escape("Warning")
                        + "|message=" + WorkerProtocol.Escape("TIA Openness compiler messages exceeded the worker evidence limit and were truncated.")
                        + "|phase=" + WorkerProtocol.Escape("Build")
                        + "|evidenceId=");
                }

                var warningCount = reflectionAccess.ReadIntProperty(result, "WarningCount");
                var errorCount = reflectionAccess.ReadIntProperty(result, "ErrorCount");
                var status = MapBuildStatus(targetDiscovery.ReadStringProperty(result, "State"), warningCount, errorCount);
                WorkerProtocol.WriteLine(
                    writer,
                    WorkerProtocol.CreateMessage("BuildResult")
                    + "|status=" + WorkerProtocol.Escape(status)
                    + "|warningCount=" + WorkerProtocol.Escape(warningCount.ToString())
                    + "|errorCount=" + WorkerProtocol.Escape(errorCount.ToString()));
            }
            catch (TargetInvocationException ex)
            {
                exceptionMapper.WriteExceptionError(
                    writer,
                    ex,
                    "TiaOpenness.V17.Worker.BuildFailed",
                    "build the selected PLC target");
            }
            catch (Exception ex)
            {
                exceptionMapper.WriteExceptionError(
                    writer,
                    ex,
                    "TiaOpenness.V17.Worker.BuildFailed",
                    "build the selected PLC target");
            }
        }


        private object? ResolveCompilable(object instance)
        {
            try
            {
                return reflectionAccess.ResolveService(instance, "Siemens.Engineering.Compiler.ICompilable");
            }
            catch
            {
                return null;
            }
        }


        private void WriteBuildDiagnostic(StreamWriter writer, object compilerMessage, int index)
        {
            var state = targetDiscovery.ReadStringProperty(compilerMessage, "State");
            var warningCount = reflectionAccess.ReadIntProperty(compilerMessage, "WarningCount");
            var errorCount = reflectionAccess.ReadIntProperty(compilerMessage, "ErrorCount");
            var severity = MapCompilerSeverity(state, warningCount, errorCount);
            var path = targetDiscovery.ReadStringProperty(compilerMessage, "Path");
            var description = targetDiscovery.ReadStringProperty(compilerMessage, "Description");
            var summary = string.IsNullOrWhiteSpace(path)
                ? description
                : path + ": " + description;
            summary = WorkerProtocol.BoundText(summary, WorkerProtocol.MaximumOperationMessageLength);
            description = WorkerProtocol.BoundText(description, WorkerProtocol.MaximumOperationMessageLength);
            var evidenceId = "tia-openness-v17-build-message-" + index.ToString();

            WorkerProtocol.WriteLine(
                writer,
                WorkerProtocol.CreateMessage("BuildEvidence")
                + "|id=" + WorkerProtocol.Escape(evidenceId)
                + "|kind=" + WorkerProtocol.Escape("CompilerMessage")
                + "|summary=" + WorkerProtocol.Escape(summary));
            if (string.Equals(severity, "Information", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            WorkerProtocol.WriteLine(
                writer,
                WorkerProtocol.CreateMessage("BuildDiagnostic")
                + "|code=" + WorkerProtocol.Escape("TiaOpenness.V17.CompilerMessage")
                + "|severity=" + WorkerProtocol.Escape(severity)
                + "|message=" + WorkerProtocol.Escape(string.IsNullOrWhiteSpace(description) ? summary : description)
                + "|phase=" + WorkerProtocol.Escape("Build")
                + "|evidenceId=" + WorkerProtocol.Escape(evidenceId));
        }


        private IEnumerable<BuildEvidence> CreateBuildEvidence(TiaTargetProbe target, object compilerResult)
        {
            yield return new BuildEvidence(
                "tia-openness-v17-build-target",
                "BuildTarget",
                target.Id);
            yield return new BuildEvidence(
                "tia-openness-v17-build-result",
                "CompilerResult",
                "State=" + targetDiscovery.ReadStringProperty(compilerResult, "State")
                + "; Warnings=" + reflectionAccess.ReadIntProperty(compilerResult, "WarningCount")
                + "; Errors=" + reflectionAccess.ReadIntProperty(compilerResult, "ErrorCount"));
        }


        private string MapBuildStatus(string state, int warningCount, int errorCount)
        {
            if (errorCount > 0 || string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return "Failed";
            }

            if (warningCount > 0 || string.Equals(state, "Warning", StringComparison.OrdinalIgnoreCase))
            {
                return "SucceededWithWarnings";
            }

            if (string.Equals(state, "Success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "Information", StringComparison.OrdinalIgnoreCase))
            {
                return "Succeeded";
            }

            return "Unclear";
        }


        private string MapCompilerSeverity(string state, int warningCount, int errorCount)
        {
            if (errorCount > 0 || string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return "Error";
            }

            if (warningCount > 0 || string.Equals(state, "Warning", StringComparison.OrdinalIgnoreCase))
            {
                return "Warning";
            }

            return "Information";
        }


        private sealed class BuildEvidence
        {
            public BuildEvidence(string id, string kind, string summary)
            {
                Id = id;
                Kind = kind;
                Summary = summary;
            }

            public string Id { get; }

            public string Kind { get; }

            public string Summary { get; }
        }


    }
}
