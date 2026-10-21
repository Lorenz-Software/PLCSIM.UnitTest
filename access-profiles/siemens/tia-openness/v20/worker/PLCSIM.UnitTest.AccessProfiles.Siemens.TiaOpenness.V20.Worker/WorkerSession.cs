using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20.Worker
{
    internal sealed class WorkerSession
    {
        private const string EngineeringAssemblyName = "Siemens.Engineering.dll";
        private readonly ILogger logger;
        private readonly WorkerExceptionMapper exceptionMapper;
        private readonly WorkerTargetDiscovery targetDiscovery;
        private readonly WorkerReflectionAccess reflectionAccess;
        private readonly WorkerSafetyAuthorizationOperation safetyAuthorizationOperation;
        private readonly WorkerBuildOperation buildOperation;
        private readonly WorkerTransferOperation transferOperation;

        public WorkerSession(ILogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            exceptionMapper = new WorkerExceptionMapper(this.logger);
            targetDiscovery = new WorkerTargetDiscovery(this.logger, exceptionMapper);
            reflectionAccess = new WorkerReflectionAccess(targetDiscovery);
            var safetyProgramInspector = new WorkerSafetyProgramInspector(this.logger, targetDiscovery, reflectionAccess);
            safetyAuthorizationOperation = new WorkerSafetyAuthorizationOperation(this.logger, exceptionMapper, safetyProgramInspector);
            buildOperation = new WorkerBuildOperation(this.logger, exceptionMapper, targetDiscovery, reflectionAccess, safetyProgramInspector);
            transferOperation = new WorkerTransferOperation(this.logger, exceptionMapper, targetDiscovery, reflectionAccess, safetyProgramInspector);
        }

        public int Run(string[] args)
        {
            var projectOwner = new WorkerProjectOwner(logger);
            StreamWriter? protocolWriter = null;
            try
            {
                var pipeName = ReadArgument(args, "--pipe");
                if (string.IsNullOrWhiteSpace(pipeName))
                {
                    const string message = "Named-pipe argument is required.";
                    logger.LogError(message);
                    WorkerLogging.WriteFallback(message);
                    return 2;
                }

                using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                logger.LogDebug("Waiting for named-pipe client connection.");
                pipe.WaitForConnection();
                logger.LogInformation("Named-pipe client connected.");
                using var protocolReader = new StreamReader(pipe, Encoding.UTF8, true, 1024);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024)
                {
                    AutoFlush = true
                };
                protocolWriter = writer;

                var publicApiDirectory = ReadArgument(args, "--public-api");
                if (string.IsNullOrWhiteSpace(publicApiDirectory))
                {
                    WorkerProtocol.WriteError(logger, protocolWriter, "TiaOpenness.V20.Worker.PublicApiMissing", "PublicAPI directory argument is required.");
                    return 2;
                }

                logger.LogDebug("TIA Openness PublicAPI directory resolved. PublicApiDirectory={PublicApiDirectory}", publicApiDirectory);
                var engineeringAssemblyPath = Path.Combine(publicApiDirectory, EngineeringAssemblyName);
                if (!File.Exists(engineeringAssemblyPath))
                {
                    WorkerProtocol.WriteError(logger, protocolWriter, "TiaOpenness.V20.Worker.EngineeringAssemblyMissing", "Siemens.Engineering.dll was not found.");
                    return 3;
                }

                AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
                {
                    var requested = new AssemblyName(eventArgs.Name).Name + ".dll";
                    var candidate = Path.Combine(publicApiDirectory, requested);
                    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                };

                logger.LogInformation("Loading TIA Openness engineering assembly.");
                logger.LogDebug("TIA Openness engineering assembly path resolved. AssemblyPath={AssemblyPath}", engineeringAssemblyPath);
                var engineeringAssembly = Assembly.LoadFrom(engineeringAssemblyPath);
                logger.LogInformation(
                    "TIA Openness engineering assembly loaded. AssemblyVersion={AssemblyVersion}",
                    engineeringAssembly.GetName().Version);
                var portalModeType = engineeringAssembly.GetType("Siemens.Engineering.TiaPortalMode", throwOnError: true);
                var portalType = engineeringAssembly.GetType("Siemens.Engineering.TiaPortal", throwOnError: true);
                var withoutUserInterface = Enum.Parse(portalModeType, "WithoutUserInterface");
                var portalTimer = Stopwatch.StartNew();
                logger.LogInformation("Creating TIA Portal session without user interface.");
                projectOwner.AdoptPortal(Activator.CreateInstance(portalType, withoutUserInterface));
                portalTimer.Stop();
                logger.LogInformation("TIA Portal session created. DurationMs={DurationMs}", portalTimer.ElapsedMilliseconds);

                if (bool.TryParse(ReadArgument(args, "--authorization-probe"), out var authorizationProbe)
                    && authorizationProbe)
                {
                    return RunAuthorizationProbe(protocolReader, protocolWriter, projectOwner);
                }

                var projectSource = ReadArgument(args, "--project-source");
                if (string.IsNullOrWhiteSpace(projectSource))
                {
                    WorkerProtocol.WriteError(logger, protocolWriter, "TiaOpenness.V20.Worker.ProjectSourceMissing", "Project source argument is required.");
                    projectOwner.ReleasePortal();
                    return 4;
                }

                if (!File.Exists(projectSource))
                {
                    WorkerProtocol.WriteError(logger, protocolWriter, "TiaOpenness.V20.Worker.ProjectSourceNotFound", "Project source file was not found.");
                    projectOwner.ReleasePortal();
                    return 5;
                }

                var projectOpenMode = ReadArgument(args, "--project-open-mode");
                if (!string.Equals(projectOpenMode, "DirectProject", StringComparison.Ordinal)
                    && !string.Equals(projectOpenMode, "RetrievedArchive", StringComparison.Ordinal))
                {
                    WorkerProtocol.WriteError(
                        logger,
                        protocolWriter,
                        "TiaOpenness.V20.Worker.ProjectOpenModeUnsupported",
                        "Project open mode must be DirectProject or RetrievedArchive.");
                    projectOwner.ReleasePortal();
                    return 6;
                }

                var archiveWorkDirectory = ReadArgument(args, "--archive-work-directory");
                logger.LogDebug("TIA project source resolved. ProjectSource={ProjectSource}", projectSource);
                var projectOpenTimer = Stopwatch.StartNew();
                if (string.Equals(projectOpenMode, "RetrievedArchive", StringComparison.Ordinal))
                {
                    projectOwner.AdoptArchiveWorkDirectory(projectOwner.PrepareOwnedArchiveWorkDirectory(archiveWorkDirectory));
                    logger.LogInformation(
                        "Retrieving TIA project archive. ArchiveFileName={ArchiveFileName}",
                        Path.GetFileName(projectSource));
                    logger.LogDebug(
                        "TIA project archive workspace created. ArchiveWorkDirectory={ArchiveWorkDirectory}",
                        projectOwner.OwnedArchiveWorkDirectory);
                    projectOwner.AdoptProject(projectOwner.RetrieveProject(projectOwner.Portal!, projectSource, projectOwner.OwnedArchiveWorkDirectory!));
                }
                else
                {
                    logger.LogInformation("Opening TIA project. ProjectFileName={ProjectFileName}", Path.GetFileName(projectSource));
                    if (!string.IsNullOrWhiteSpace(archiveWorkDirectory))
                    {
                        logger.LogDebug("Archive work-directory argument is inactive for direct project open. ArchiveWorkDirectory={ArchiveWorkDirectory}", archiveWorkDirectory);
                    }

                    projectOwner.AdoptProject(projectOwner.OpenProject(projectOwner.Portal!, projectSource));
                }

                projectOpenTimer.Stop();
                logger.LogInformation(
                    "TIA project source opened. ProjectFileName={ProjectFileName} ProjectOpenMode={ProjectOpenMode} DurationMs={DurationMs}",
                    Path.GetFileName(projectSource),
                    projectOpenMode,
                    projectOpenTimer.ElapsedMilliseconds);
                projectOwner.EnableSimulationDuringBlockCompilation(projectOwner.Project!);
                var sessionId = Guid.NewGuid().ToString("N");
                var projectVersion = projectOwner.ReadProjectVersion(projectOwner.Project!);
                logger.LogInformation(
                    "TIA engineering session is ready. SessionId={SessionId} ProductVersion={ProductVersion} ProjectVersion={ProjectVersion}",
                    sessionId,
                    "20",
                    projectVersion);
                WorkerProtocol.WriteReady(
                    protocolWriter,
                    sessionId,
                    projectSource,
                    "20",
                    projectVersion);

                var command = protocolReader.ReadLine();
                while (!string.IsNullOrWhiteSpace(command))
                {
                    var commandTimer = Stopwatch.StartNew();
                    var message = WorkerProtocol.ParseProtocolMessage(command);
                    logger.LogInformation("Worker command received. CommandType={CommandType}", message.Type);
                    if (!message.IsProtocolVersionSupported)
                    {
                        WorkerProtocol.WriteError(logger, protocolWriter, "TiaOpenness.V20.Worker.ProtocolUnsupported", "Worker command protocol version is not supported.");
                    }
                    else if (string.Equals(message.Type, "Close", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleanupFailures = projectOwner.ReleaseAll();
                        WorkerProtocol.WriteCloseResult(protocolWriter, cleanupFailures);
                        commandTimer.Stop();
                        logger.LogInformation("Worker command completed. CommandType={CommandType} DurationMs={DurationMs}", message.Type, commandTimer.ElapsedMilliseconds);
                        return cleanupFailures.Count == 0 ? 0 : 1;
                    }
                    else if (string.Equals(message.Type, "ProbeTargets", StringComparison.OrdinalIgnoreCase))
                    {
                        targetDiscovery.WriteTargetProbe(protocolWriter, projectOwner.Project!);
                    }
                    else if (string.Equals(message.Type, "BuildTarget", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetId = message.Values.TryGetValue("targetId", out var value) ? value : string.Empty;
                        buildOperation.WriteResult(protocolWriter, projectOwner.Project!, targetId);
                    }
                    else if (string.Equals(message.Type, "ProbeSafetyAuthorization", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetId = message.Values.TryGetValue("targetId", out var value) ? value : string.Empty;
                        safetyAuthorizationOperation.WriteRequirement(protocolWriter, projectOwner.Project!, targetId);
                    }
                    else if (string.Equals(message.Type, "AuthorizeSafetyProgram", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetId = message.Values.TryGetValue("targetId", out var value) ? value : string.Empty;
                        var protectedCredential = message.Values.TryGetValue("protectedCredential", out var credentialValue)
                            ? credentialValue
                            : string.Empty;
                        safetyAuthorizationOperation.WriteAuthorization(
                            protocolWriter,
                            projectOwner.Project!,
                            projectOwner,
                            targetId,
                            protectedCredential);
                    }                    else if (string.Equals(message.Type, "TransferTarget", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetId = message.Values.TryGetValue("targetId", out var value) ? value : string.Empty;
                        var runtimeTargetId = message.Values.TryGetValue("runtimeTargetId", out var runtimeValue) ? runtimeValue : string.Empty;
                        var runtimeTargetDisplayName = message.Values.TryGetValue("runtimeTargetDisplayName", out var displayValue)
                            ? displayValue
                            : string.Empty;
                        var runtimeInstanceName = message.Values.TryGetValue("runtimeInstanceName", out var runtimeInstanceValue)
                            ? runtimeInstanceValue
                            : string.Empty;
                        var connectionModeName = message.Values.TryGetValue("connectionModeName", out var connectionModeValue)
                            ? connectionModeValue
                            : string.Empty;
                        var pcInterfaceName = message.Values.TryGetValue("pcInterfaceName", out var pcInterfaceValue)
                            ? pcInterfaceValue
                            : string.Empty;
                        var pcInterfaceNumber = message.Values.TryGetValue("pcInterfaceNumber", out var pcInterfaceNumberValue)
                            && int.TryParse(pcInterfaceNumberValue, out var parsedPcInterfaceNumber)
                                ? parsedPcInterfaceNumber
                                : (int?)null;
                        var targetInterfaceName = message.Values.TryGetValue("targetInterfaceName", out var targetInterfaceValue)
                            ? targetInterfaceValue
                            : string.Empty;
                        transferOperation.WriteResult(
                            protocolWriter,
                            projectOwner.Project!,
                            targetId,
                            runtimeTargetId,
                            runtimeTargetDisplayName,
                            runtimeInstanceName,
                            connectionModeName,
                            pcInterfaceName,
                            pcInterfaceNumber,
                            targetInterfaceName);
                    }
                    else
                    {
                        WorkerProtocol.WriteError(logger, protocolWriter, "TiaOpenness.V20.Worker.CommandUnsupported", "Worker command is not supported.");
                    }

                    commandTimer.Stop();
                    logger.LogInformation("Worker command completed. CommandType={CommandType} DurationMs={DurationMs}", message.Type, commandTimer.ElapsedMilliseconds);
                    command = protocolReader.ReadLine();
                }

                logger.LogInformation("Named-pipe client disconnected. Releasing TIA resources.");
                projectOwner.ReleaseAll();
                return 0;
            }
            catch (Exception ex)
            {
                var rootException = ex.GetBaseException();
                logger.LogError(rootException, "TIA Openness worker failed.");
                projectOwner.ReleaseAll();
                if (protocolWriter is null)
                {
                    WorkerLogging.WriteFallback(rootException.Message);
                }
                else
                {
                    try
                    {
                        var failure = exceptionMapper.ClassifyException(
                            rootException,
                            "TiaOpenness.V20.Worker.EngineeringSessionOpenFailed",
                            "open the TIA engineering session");
                        WorkerProtocol.WriteError(
                            logger,
                            protocolWriter,
                            failure.Code,
                            failure.Message,
                            failure.SessionInvalidated);
                    }
                    catch (ObjectDisposedException)
                    {
                        WorkerLogging.WriteFallback(rootException.Message);
                    }
                    catch (IOException)
                    {
                        WorkerLogging.WriteFallback(rootException.Message);
                    }
                }

                return 1;
            }
        }

        private int RunAuthorizationProbe(
            StreamReader protocolReader,
            StreamWriter protocolWriter,
            WorkerProjectOwner projectOwner)
        {
            logger.LogInformation("TIA Openness authorization probe opened a portal session.");
            WorkerProtocol.WriteAuthorizationReady(protocolWriter);

            var command = protocolReader.ReadLine();
            if (string.IsNullOrWhiteSpace(command))
            {
                logger.LogWarning("Authorization-probe client disconnected before cleanup was requested.");
                return projectOwner.ReleaseAll().Count == 0 ? 0 : 1;
            }

            var message = WorkerProtocol.ParseProtocolMessage(command);
            if (!message.IsProtocolVersionSupported
                || !string.Equals(message.Type, "Close", StringComparison.OrdinalIgnoreCase))
            {
                WorkerProtocol.WriteError(
                    logger,
                    protocolWriter,
                    "TiaOpenness.V20.Worker.AuthorizationProbeCommandUnsupported",
                    "The authorization-probe worker accepts only the Close command.");
                return projectOwner.ReleaseAll().Count == 0 ? 1 : 2;
            }

            var cleanupFailures = projectOwner.ReleaseAll();
            WorkerProtocol.WriteCloseResult(protocolWriter, cleanupFailures);
            logger.LogInformation(
                "TIA Openness authorization probe cleanup completed. Succeeded={Succeeded}",
                cleanupFailures.Count == 0);
            return cleanupFailures.Count == 0 ? 0 : 1;
        }

        private string ReadArgument(string[] args, string name)
        {
            var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
        }

    }
}
