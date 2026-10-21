using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18.Worker
{
    internal sealed class WorkerTransferOperation
    {
        private readonly ILogger logger;
        private readonly WorkerExceptionMapper exceptionMapper;
        private readonly WorkerTargetDiscovery targetDiscovery;
        private readonly WorkerReflectionAccess reflectionAccess;
        private readonly WorkerSafetyProgramInspector safetyProgramInspector;

        public WorkerTransferOperation(ILogger logger, WorkerExceptionMapper exceptionMapper, WorkerTargetDiscovery targetDiscovery, WorkerReflectionAccess reflectionAccess, WorkerSafetyProgramInspector safetyProgramInspector)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.exceptionMapper = exceptionMapper ?? throw new ArgumentNullException(nameof(exceptionMapper));
            this.targetDiscovery = targetDiscovery ?? throw new ArgumentNullException(nameof(targetDiscovery));
            this.reflectionAccess = reflectionAccess ?? throw new ArgumentNullException(nameof(reflectionAccess));
            this.safetyProgramInspector = safetyProgramInspector ?? throw new ArgumentNullException(nameof(safetyProgramInspector));
        }

        public void WriteResult(
            StreamWriter writer,
            object project,
            string targetId,
            string runtimeTargetId,
            string runtimeTargetDisplayName,
            string runtimeInstanceName,
            string connectionModeName,
            string pcInterfaceName,
            int? pcInterfaceNumber,
            string targetInterfaceName)
        {
            logger.LogInformation(
                "Starting TIA Openness transfer operation. TargetId={TargetId} RuntimeTargetId={RuntimeTargetId}",
                targetId,
                runtimeTargetId);
            var recorder = new TransferRecorder();
            try
            {
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferTargetMissing", "Transfer target id is required.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(connectionModeName))
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferConnectionModeMissing", "Transfer connection mode name is required.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(pcInterfaceName))
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferPcInterfaceMissing", "Transfer PC interface name is required.");
                    return;
                }

                var target = targetDiscovery.ProbeTargetContexts(project)
                    .FirstOrDefault(context => string.Equals(context.Target.Id, targetId, StringComparison.Ordinal));
                if (target is null)
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferTargetNotFound", "Transfer target was not found in the opened TIA project.");
                    return;
                }

                if (safetyProgramInspector.Inspect(target).HasSafetyProgram)
                {
                    safetyProgramInspector.WriteNotSupported(writer, "transfer");
                    return;
                }

                var downloadProvider = ResolveDownloadProvider(target.DeviceItem);
                if (downloadProvider is null)
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferProviderMissing", "Transfer target does not provide a TIA Openness download provider service.");
                    return;
                }

                var connectionConfiguration = downloadProvider.GetType().GetProperty("Configuration")?.GetValue(downloadProvider, null);
                if (connectionConfiguration is null)
                {
                    WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferConfigurationMissing", "TIA Openness download provider did not expose a connection configuration.");
                    return;
                }

                var onlineLegitimation = AttachOnlineLegitimationHandler(connectionConfiguration, recorder);
                try
                {
                    var targetInterface = ResolvePlcSimTargetInterface(
                        connectionConfiguration,
                        recorder,
                        connectionModeName,
                        pcInterfaceName,
                        pcInterfaceNumber,
                        targetInterfaceName);
                    if (targetInterface is null)
                    {
                        foreach (var evidence in recorder.Evidence)
                        {
                            WriteTransferEvidence(writer, evidence);
                        }

                        foreach (var diagnostic in recorder.Diagnostics)
                        {
                            WriteTransferDiagnostic(writer, diagnostic);
                        }

                        WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferTargetInterfaceMissing", "TIA Openness PLCSIM target interface could not be resolved.");
                        return;
                    }

                    recorder.Evidence.Add(new TransferEvidence("tia-openness-v18-transfer-project-target", "TransferTarget", target.Target.Id));
                    recorder.Evidence.Add(new TransferEvidence("tia-openness-v18-transfer-runtime-target", "RuntimeTarget", runtimeTargetId));
                    if (!string.IsNullOrWhiteSpace(runtimeTargetDisplayName))
                    {
                        recorder.Evidence.Add(new TransferEvidence(
                            "tia-openness-v18-transfer-runtime-target-display",
                            "RuntimeTargetDisplayName",
                            runtimeTargetDisplayName));
                    }

                    if (!string.IsNullOrWhiteSpace(runtimeInstanceName))
                    {
                        recorder.Evidence.Add(new TransferEvidence(
                            "tia-openness-v18-transfer-runtime-instance",
                            "RuntimeInstanceName",
                            runtimeInstanceName));
                    }

                    var download = downloadProvider.GetType().GetMethods()
                        .FirstOrDefault(method => method.Name == "Download"
                            && method.GetParameters().Length == 4
                            && method.GetParameters()[0].ParameterType.FullName == "Siemens.Engineering.Connection.IConfiguration");
                    if (download is null)
                    {
                        WorkerProtocol.WriteError(logger, writer, "TiaOpenness.V18.Worker.TransferMethodMissing", "TIA Openness download provider does not provide the expected Download(IConfiguration, ..., DownloadOptions) method.");
                        return;
                    }

                    var parameters = download.GetParameters();
                    var preDownload = CreateDownloadConfigurationDelegate(parameters[1].ParameterType, recorder, false);
                    var postDownload = CreateDownloadConfigurationDelegate(parameters[2].ParameterType, recorder, true);
                    var options = CreateDownloadOptions(parameters[3].ParameterType);
                    recorder.Evidence.Add(new TransferEvidence("tia-openness-v18-transfer-options", "DownloadOptions", options.ToString() ?? string.Empty));

                    var result = download.Invoke(
                        downloadProvider,
                        new[] { targetInterface, preDownload, postDownload, options })
                        ?? throw new InvalidOperationException("TIA Openness download returned no result.");

                    foreach (var evidence in recorder.Evidence)
                    {
                        WriteTransferEvidence(writer, evidence);
                    }

                    var messages = reflectionAccess.ReadOperationMessages(result, out var messagesTruncated);
                    for (var index = 0; index < messages.Count; index++)
                    {
                        WriteDownloadMessage(writer, messages[index], index);
                    }
                    if (messagesTruncated)
                    {
                        WriteTransferDiagnostic(
                            writer,
                            new TransferDiagnostic(
                                "TiaOpenness.V18.DownloadMessagesTruncated",
                                "Warning",
                                "TIA Openness download messages exceeded the worker evidence limit and were truncated.",
                                "Transfer",
                                null));
                    }

                    foreach (var diagnostic in recorder.Diagnostics)
                    {
                        WriteTransferDiagnostic(writer, diagnostic);
                    }

                    var status = MapDownloadStatus(
                        targetDiscovery.ReadStringProperty(result, "State"),
                        reflectionAccess.ReadIntProperty(result, "WarningCount"),
                        reflectionAccess.ReadIntProperty(result, "ErrorCount"));
                    WorkerProtocol.WriteLine(
                        writer,
                        WorkerProtocol.CreateMessage("TransferResult")
                        + "|status=" + WorkerProtocol.Escape(status));
                }
                finally
                {
                    DetachOnlineLegitimationHandler(connectionConfiguration, onlineLegitimation);
                }
            }
            catch (TargetInvocationException ex)
            {
                foreach (var evidence in recorder.Evidence)
                {
                    WriteTransferEvidence(writer, evidence);
                }

                foreach (var diagnostic in recorder.Diagnostics)
                {
                    WriteTransferDiagnostic(writer, diagnostic);
                }

                exceptionMapper.WriteExceptionError(
                    writer,
                    ex,
                    "TiaOpenness.V18.Worker.TransferFailed",
                    "transfer the selected PLC target");
            }
            catch (Exception ex)
            {
                foreach (var evidence in recorder.Evidence)
                {
                    WriteTransferEvidence(writer, evidence);
                }

                foreach (var diagnostic in recorder.Diagnostics)
                {
                    WriteTransferDiagnostic(writer, diagnostic);
                }

                exceptionMapper.WriteExceptionError(
                    writer,
                    ex,
                    "TiaOpenness.V18.Worker.TransferFailed",
                    "transfer the selected PLC target");
            }
        }


        private object? ResolveDownloadProvider(object instance)
        {
            try
            {
                return reflectionAccess.ResolveService(instance, "Siemens.Engineering.Download.DownloadProvider");
            }
            catch
            {
                return null;
            }
        }


        private object? ResolvePlcSimTargetInterface(
            object connectionConfiguration,
            TransferRecorder recorder,
            string connectionModeName,
            string pcInterfaceName,
            int? pcInterfaceNumber,
            string targetInterfaceName)
        {
            var modes = connectionConfiguration.GetType().GetProperty("Modes")?.GetValue(connectionConfiguration, null);
            var mode = FindByName(modes, connectionModeName);
            if (mode is null)
            {
                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferConnectionModeMissing",
                    "Error",
                    "TIA Openness connection mode '" + connectionModeName + "' was not available for transfer.",
                    "Transfer",
                    "tia-openness-v18-transfer-connection-mode"));
                return null;
            }

            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-connection-mode",
                "ConnectionMode",
                targetDiscovery.ReadStringProperty(mode, "Name")));

            var pcInterfaces = mode.GetType().GetProperty("PcInterfaces")?.GetValue(mode, null);
            var pcInterface = pcInterfaceNumber is null
                ? FindByName(pcInterfaces, pcInterfaceName)
                : FindByNameAndNumber(pcInterfaces, pcInterfaceName, pcInterfaceNumber.Value);
            if (pcInterface is null)
            {
                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferPcInterfaceMissing",
                    "Error",
                    "TIA Openness PC interface '" + pcInterfaceName + "' was not available for transfer.",
                    "Transfer",
                    "tia-openness-v18-transfer-pc-interface"));
                return null;
            }

            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-pc-interface",
                "PcInterface",
                targetDiscovery.ReadStringProperty(pcInterface, "Name")));
            if (pcInterfaceNumber is not null)
            {
                recorder.Evidence.Add(new TransferEvidence(
                    "tia-openness-v18-transfer-pc-interface-number",
                    "PcInterfaceNumber",
                    pcInterfaceNumber.Value.ToString()));
            }

            var targetInterfaces = targetDiscovery.ReadEnumerableProperty(pcInterface, "TargetInterfaces");
            var targetInterface = string.IsNullOrWhiteSpace(targetInterfaceName)
                ? targetInterfaces.FirstOrDefault()
                : targetInterfaces.FirstOrDefault(candidate =>
                    string.Equals(targetDiscovery.ReadStringProperty(candidate, "Name"), targetInterfaceName, StringComparison.OrdinalIgnoreCase));
            if (targetInterface is null)
            {
                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferTargetInterfaceMissing",
                    "Error",
                    string.IsNullOrWhiteSpace(targetInterfaceName)
                        ? "TIA Openness PC interface '" + pcInterfaceName + "' did not expose a target interface."
                        : "TIA Openness PC interface '" + pcInterfaceName + "' did not expose target interface '" + targetInterfaceName + "'.",
                    "Transfer",
                    "tia-openness-v18-transfer-target-interface"));
                return null;
            }

            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-target-interface",
                "TargetInterface",
                targetDiscovery.ReadStringProperty(targetInterface, "Name")));
            return targetInterface;
        }


        private object? FindByName(object? composition, string name)
        {
            return composition?.GetType()
                .GetMethod("Find", new[] { typeof(string) })
                ?.Invoke(composition, new object[] { name });
        }


        private object? FindByNameAndNumber(object? composition, string name, int number)
        {
            return composition?.GetType()
                .GetMethod("Find", new[] { typeof(string), typeof(int) })
                ?.Invoke(composition, new object[] { name, number });
        }


        private Delegate CreateDownloadConfigurationDelegate(
            Type delegateType,
            TransferRecorder recorder,
            bool postDownload)
        {
            var invoke = delegateType.GetMethod("Invoke")
                ?? throw new InvalidOperationException("Download configuration delegate does not expose Invoke().");
            var parameterType = invoke.GetParameters().Single().ParameterType;
            var parameter = Expression.Parameter(parameterType, "configuration");
            var handle = typeof(WorkerTransferOperation).GetMethod(
                    nameof(HandleDownloadConfiguration),
                    BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Download configuration handler was not found.");
            var body = Expression.Call(
                Expression.Constant(this),
                handle,
                Expression.Constant(recorder),
                Expression.Convert(parameter, typeof(object)),
                Expression.Constant(postDownload));
            return Expression.Lambda(delegateType, body, parameter).Compile();
        }


        private Delegate? AttachOnlineLegitimationHandler(object connectionConfiguration, TransferRecorder recorder)
        {
            var onlineLegitimation = connectionConfiguration.GetType().GetEvent("OnlineLegitimation");
            var delegateType = onlineLegitimation?.EventHandlerType;
            if (onlineLegitimation is null || delegateType is null)
            {
                recorder.Evidence.Add(new TransferEvidence(
                    "tia-openness-v18-transfer-online-legitimation",
                    "OnlineLegitimation",
                    "Connection configuration does not expose OnlineLegitimation."));
                return null;
            }

            var invoke = delegateType.GetMethod("Invoke");
            var parameters = invoke?.GetParameters();
            if (parameters is null || parameters.Length != 1)
            {
                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferOnlineLegitimationUnsupported",
                    "Warning",
                    "TIA Openness OnlineLegitimation event has an unsupported delegate signature.",
                    "Transfer",
                    "tia-openness-v18-transfer-online-legitimation"));
                return null;
            }

            var parameter = Expression.Parameter(parameters[0].ParameterType, "configuration");
            var handle = typeof(WorkerTransferOperation).GetMethod(
                    nameof(HandleOnlineLegitimation),
                    BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Online legitimation handler was not found.");
            var body = Expression.Call(
                Expression.Constant(this),
                handle,
                Expression.Constant(recorder),
                Expression.Convert(parameter, typeof(object)));
            var handler = Expression.Lambda(delegateType, body, parameter).Compile();
            onlineLegitimation.AddEventHandler(connectionConfiguration, handler);
            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-online-legitimation",
                "OnlineLegitimation",
                "OnlineLegitimation handler attached."));
            return handler;
        }


        private void DetachOnlineLegitimationHandler(object connectionConfiguration, Delegate? handler)
        {
            if (handler is null)
            {
                return;
            }

            try
            {
                connectionConfiguration.GetType().GetEvent("OnlineLegitimation")?.RemoveEventHandler(connectionConfiguration, handler);
            }
            catch
            {
            }
        }


        private void HandleOnlineLegitimation(TransferRecorder recorder, object configuration)
        {
            var typeName = configuration.GetType().Name;
            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-online-legitimation-" + recorder.NextConfigurationIndex(),
                "OnlineLegitimation",
                typeName + ":" + targetDiscovery.ReadStringProperty(configuration, "PlcName") + ":" + targetDiscovery.ReadStringProperty(configuration, "VerificationInfo")));

            if (string.Equals(typeName, "TlsVerificationConfiguration", StringComparison.Ordinal))
            {
                SetEnumProperty(configuration, "CurrentSelection", "Trusted");
                recorder.Evidence.Add(new TransferEvidence(
                    "tia-openness-v18-transfer-online-legitimation-trusted",
                    "OnlineLegitimation",
                    "TlsVerificationConfiguration set to Trusted."));
                return;
            }

            recorder.Diagnostics.Add(new TransferDiagnostic(
                "TiaOpenness.V18.TransferOnlineLegitimationUnhandled",
                "Error",
                "TIA Openness requested unsupported online legitimation configuration type '" + typeName + "'.",
                "Transfer",
                null));
            throw new InvalidOperationException(
                "TIA Openness requested unsupported online legitimation configuration type '" + typeName + "'.");
        }


        private void HandleDownloadConfiguration(
            TransferRecorder recorder,
            object configuration,
            bool postDownload)
        {
            var typeName = configuration.GetType().Name;
            var phase = postDownload ? "PostDownload" : "PreDownload";
            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-configuration-" + recorder.NextConfigurationIndex(),
                "DownloadConfiguration",
                phase + ":" + typeName + ":" + targetDiscovery.ReadStringProperty(configuration, "Message")));

            if (string.Equals(typeName, "TargetForSoftware", StringComparison.Ordinal))
            {
                SetDownloadSelection(recorder, configuration, "PlcSimulationAdvanced");
                return;
            }

            if (string.Equals(typeName, "StartModules", StringComparison.Ordinal))
            {
                SetDownloadSelection(recorder, configuration, "NoAction");
                return;
            }

            if (string.Equals(typeName, "StopModules", StringComparison.Ordinal))
            {
                SetDownloadSelection(recorder, configuration, "StopAll");
                return;
            }

            if (string.Equals(typeName, "ConsistentBlocksDownload", StringComparison.Ordinal)
                || string.Equals(typeName, "AlarmTextLibrariesDownload", StringComparison.Ordinal))
            {
                SetDownloadSelection(recorder, configuration, "ConsistentDownload");
                return;
            }

            if (string.Equals(typeName, "UserManagementDownload", StringComparison.Ordinal))
            {
                RecordRetainedDownloadSelection(recorder, configuration);
                return;
            }

            if (string.Equals(typeName, "UpgradeTargetDevice", StringComparison.Ordinal))
            {
                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferTargetUpgradeRequired",
                    "Error",
                    "TIA Openness requested a target-device upgrade during download. Automatic target upgrades are not supported by the V18 worker.",
                    "Transfer",
                    null));
                throw new InvalidOperationException("TIA Openness requested a target-device upgrade during download.");
            }

            if (string.Equals(typeName, "DifferentTargetConfiguration", StringComparison.Ordinal))
            {
                if (TrySetDownloadSelection(recorder, configuration, "NoAction"))
                {
                    return;
                }

                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferDifferentTargetUnsupported",
                    "Error",
                    "TIA Openness reported a different target configuration that cannot be rejected explicitly by the V18 worker.",
                    "Transfer",
                    null));
                throw new InvalidOperationException("TIA Openness reported an unsupported different target configuration.");
            }

            if (string.Equals(typeName, "CheckBeforeDownload", StringComparison.Ordinal)
                || string.Equals(typeName, "DowngradeTargetDevice", StringComparison.Ordinal))
            {
                SetBoolProperty(configuration, "Checked", true);
                return;
            }

            if (IsDownloadPasswordConfiguration(configuration))
            {
                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferPasswordRequired",
                    "Error",
                    "TIA Openness requested a download password configuration, which is not supported by the non-interactive V18 worker slice.",
                    "Transfer",
                    null));
                throw new InvalidOperationException("TIA Openness requested a download password configuration.");
            }

            if (IsDownloadSelectionConfiguration(configuration) || IsDownloadCheckConfiguration(configuration))
            {
                recorder.Diagnostics.Add(new TransferDiagnostic(
                    "TiaOpenness.V18.TransferConfigurationUnhandled",
                    "Error",
                    "TIA Openness transfer reported an unhandled blocking download configuration type '" + typeName + "'.",
                    "Transfer",
                    null));
                throw new InvalidOperationException(
                    "TIA Openness reported an unhandled blocking download configuration type '" + typeName + "'.");
            }
        }


        private bool IsDownloadSelectionConfiguration(object configuration)
        {
            return InheritsFrom(configuration.GetType(), "Siemens.Engineering.Download.Configurations.DownloadSelectionConfiguration");
        }


        private bool IsDownloadCheckConfiguration(object configuration)
        {
            return InheritsFrom(configuration.GetType(), "Siemens.Engineering.Download.Configurations.DownloadCheckConfiguration");
        }


        private bool IsDownloadPasswordConfiguration(object configuration)
        {
            return InheritsFrom(configuration.GetType(), "Siemens.Engineering.Download.Configurations.DownloadPasswordConfiguration");
        }


        private bool InheritsFrom(Type type, string fullName)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }


        private void SetEnumProperty(object instance, string propertyName, string valueName)
        {
            var property = instance.GetType().GetProperty(propertyName)
                ?? throw new InvalidOperationException(instance.GetType().Name + "." + propertyName + " was not found.");
            var value = Enum.Parse(property.PropertyType, valueName);
            property.SetValue(instance, value, null);
        }


        private void SetDownloadSelection(TransferRecorder recorder, object configuration, string valueName)
        {
            if (!TrySetDownloadSelection(recorder, configuration, valueName))
            {
                throw new InvalidOperationException(
                    configuration.GetType().Name + ".CurrentSelection does not support '" + valueName + "'.");
            }
        }


        private bool TrySetDownloadSelection(TransferRecorder recorder, object configuration, string valueName)
        {
            var property = configuration.GetType().GetProperty("CurrentSelection");
            if (property is null || !property.PropertyType.IsEnum)
            {
                return false;
            }

            if (!Enum.IsDefined(property.PropertyType, valueName))
            {
                return false;
            }

            var value = Enum.Parse(property.PropertyType, valueName);
            property.SetValue(configuration, value, null);
            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-selection-" + recorder.NextConfigurationIndex(),
                "DownloadSelection",
                configuration.GetType().Name + " set to " + valueName + "."));
            return true;
        }


        private void RecordRetainedDownloadSelection(TransferRecorder recorder, object configuration)
        {
            recorder.Evidence.Add(new TransferEvidence(
                "tia-openness-v18-transfer-selection-" + recorder.NextConfigurationIndex(),
                "DownloadSelection",
                configuration.GetType().Name + " retained current selection "
                + targetDiscovery.ReadStringProperty(configuration, "CurrentSelection")
                + "."));
        }


        private void SetBoolProperty(object instance, string propertyName, bool value)
        {
            var property = instance.GetType().GetProperty(propertyName)
                ?? throw new InvalidOperationException(instance.GetType().Name + "." + propertyName + " was not found.");
            property.SetValue(instance, value, null);
        }


        private object CreateDownloadOptions(Type optionsType)
        {
            var software = Enum.Parse(optionsType, "Software");
            var hardware = Enum.Parse(optionsType, "Hardware");
            var combined = Convert.ToInt64(software) | Convert.ToInt64(hardware);
            return Enum.ToObject(optionsType, combined);
        }


        private void WriteDownloadMessage(StreamWriter writer, object downloadMessage, int index)
        {
            var state = targetDiscovery.ReadStringProperty(downloadMessage, "State");
            var warningCount = reflectionAccess.ReadIntProperty(downloadMessage, "WarningCount");
            var errorCount = reflectionAccess.ReadIntProperty(downloadMessage, "ErrorCount");
            var severity = MapDownloadSeverity(state, warningCount, errorCount);
            var message = WorkerProtocol.BoundText(
                targetDiscovery.ReadStringProperty(downloadMessage, "Message"),
                WorkerProtocol.MaximumOperationMessageLength);
            var evidenceId = "tia-openness-v18-transfer-message-" + index.ToString();

            WorkerProtocol.WriteLine(
                writer,
                WorkerProtocol.CreateMessage("TransferEvidence")
                + "|id=" + WorkerProtocol.Escape(evidenceId)
                + "|kind=" + WorkerProtocol.Escape("DownloadMessage")
                + "|summary=" + WorkerProtocol.Escape(message));
            if (string.Equals(severity, "Information", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            WorkerProtocol.WriteLine(
                writer,
                WorkerProtocol.CreateMessage("TransferDiagnostic")
                + "|code=" + WorkerProtocol.Escape("TiaOpenness.V18.DownloadMessage")
                + "|severity=" + WorkerProtocol.Escape(severity)
                + "|message=" + WorkerProtocol.Escape(string.IsNullOrWhiteSpace(message) ? "TIA Openness download message." : message)
                + "|phase=" + WorkerProtocol.Escape("Transfer")
                + "|evidenceId=" + WorkerProtocol.Escape(evidenceId));
        }


        private void WriteTransferEvidence(StreamWriter writer, TransferEvidence evidence)
        {
            WorkerProtocol.WriteLine(
                writer,
                WorkerProtocol.CreateMessage("TransferEvidence")
                + "|id=" + WorkerProtocol.Escape(evidence.Id)
                + "|kind=" + WorkerProtocol.Escape(evidence.Kind)
                + "|summary=" + WorkerProtocol.Escape(WorkerProtocol.BoundText(evidence.Summary, WorkerProtocol.MaximumOperationMessageLength)));
        }


        private void WriteTransferDiagnostic(StreamWriter writer, TransferDiagnostic diagnostic)
        {
            WorkerProtocol.WriteLine(
                writer,
                WorkerProtocol.CreateMessage("TransferDiagnostic")
                + "|code=" + WorkerProtocol.Escape(diagnostic.Code)
                + "|severity=" + WorkerProtocol.Escape(diagnostic.Severity)
                + "|message=" + WorkerProtocol.Escape(WorkerProtocol.BoundText(diagnostic.Message, WorkerProtocol.MaximumOperationMessageLength))
                + "|phase=" + WorkerProtocol.Escape(diagnostic.Phase)
                + "|evidenceId=" + WorkerProtocol.Escape(diagnostic.EvidenceId ?? string.Empty));
        }


        private string MapDownloadStatus(string state, int warningCount, int errorCount)
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


        private string MapDownloadSeverity(string state, int warningCount, int errorCount)
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


        private sealed class TransferRecorder
        {
            private int configurationIndex;

            public List<TransferEvidence> Evidence { get; } = new List<TransferEvidence>();

            public List<TransferDiagnostic> Diagnostics { get; } = new List<TransferDiagnostic>();

            public int NextConfigurationIndex()
            {
                configurationIndex++;
                return configurationIndex;
            }
        }


        private sealed class TransferEvidence
        {
            public TransferEvidence(string id, string kind, string summary)
            {
                Id = id;
                Kind = kind;
                Summary = summary;
            }

            public string Id { get; }

            public string Kind { get; }

            public string Summary { get; }
        }


        private sealed class TransferDiagnostic
        {
            public TransferDiagnostic(
                string code,
                string severity,
                string message,
                string phase,
                string? evidenceId)
            {
                Code = code;
                Severity = severity;
                Message = message;
                Phase = phase;
                EvidenceId = evidenceId;
            }

            public string Code { get; }

            public string Severity { get; }

            public string Message { get; }

            public string Phase { get; }

            public string? EvidenceId { get; }
        }


    }
}
