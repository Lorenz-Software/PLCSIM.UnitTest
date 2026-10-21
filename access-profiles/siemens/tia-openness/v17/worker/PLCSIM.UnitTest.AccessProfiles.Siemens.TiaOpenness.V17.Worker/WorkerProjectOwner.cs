using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17.Worker
{
    internal sealed class WorkerProjectOwner
    {
        private readonly ILogger logger;

        public WorkerProjectOwner(ILogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public object? Portal { get; private set; }

        public object? Project { get; private set; }

        public string? OwnedArchiveWorkDirectory { get; private set; }

        public void AdoptPortal(object portal)
        {
            Portal = portal ?? throw new ArgumentNullException(nameof(portal));
        }

        public void AdoptProject(object project)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
        }

        public void AdoptArchiveWorkDirectory(string ownedDirectory)
        {
            OwnedArchiveWorkDirectory = ownedDirectory;
        }

        public Exception? ReleasePortal()
        {
            var failure = TryDisposePortal(Portal);
            Portal = null;
            return failure;
        }

        public IReadOnlyList<Exception> ReleaseAll()
        {
            var failures = new List<Exception>();
            var projectFailure = CloseProject(Project);
            if (projectFailure is not null)
            {
                failures.Add(projectFailure);
            }

            Project = null;
            var portalFailure = ReleasePortal();
            if (portalFailure is not null)
            {
                failures.Add(portalFailure);
            }

            var archiveFailure = DeleteOwnedArchiveWorkDirectory(OwnedArchiveWorkDirectory);
            if (archiveFailure is not null)
            {
                failures.Add(archiveFailure);
            }

            OwnedArchiveWorkDirectory = null;
            return failures;
        }

        public object OpenProject(object tiaPortal, string projectSource)
        {
            var projectsProperty = tiaPortal.GetType().GetProperty("Projects")
                ?? throw new InvalidOperationException("TIA Portal Projects property was not found.");
            var projects = projectsProperty.GetValue(tiaPortal, null)
                ?? throw new InvalidOperationException("TIA Portal Projects composition was not available.");
            var openMethod = projects.GetType().GetMethod("Open", new[] { typeof(FileInfo) })
                ?? throw new InvalidOperationException("TIA Portal Projects.Open(FileInfo) method was not found.");
            var project = openMethod.Invoke(projects, new object[] { new FileInfo(projectSource) });
            return project ?? throw new InvalidOperationException("TIA Portal project open returned no project.");
        }


        public object RetrieveProject(
            object tiaPortal,
            string projectSource,
            string targetDirectory)
        {
            var projectsProperty = tiaPortal.GetType().GetProperty("Projects")
                ?? throw new InvalidOperationException("TIA Portal Projects property was not found.");
            var projects = projectsProperty.GetValue(tiaPortal, null)
                ?? throw new InvalidOperationException("TIA Portal Projects composition was not available.");
            var retrieveMethod = projects.GetType().GetMethod(
                "Retrieve",
                new[] { typeof(FileInfo), typeof(DirectoryInfo) })
                ?? throw new InvalidOperationException(
                    "TIA Portal Projects.Retrieve(FileInfo, DirectoryInfo) method was not found.");
            var project = retrieveMethod.Invoke(
                projects,
                new object[] { new FileInfo(projectSource), new DirectoryInfo(targetDirectory) });
            return project ?? throw new InvalidOperationException("TIA Portal project archive retrieval returned no project.");
        }


        public string PrepareOwnedArchiveWorkDirectory(string configuredDirectory)
        {
            if (string.IsNullOrWhiteSpace(configuredDirectory))
            {
                throw new InvalidOperationException(
                    "TIA project archive retrieval requires a binding-owned work directory.");
            }

            var ownedDirectory = Path.GetFullPath(configuredDirectory);
            Directory.CreateDirectory(ownedDirectory);
            return ownedDirectory;
        }


        public Exception? DeleteOwnedArchiveWorkDirectory(string? ownedDirectory)
        {
            if (string.IsNullOrWhiteSpace(ownedDirectory))
            {
                return null;
            }

            try
            {
                var normalizedOwnedDirectory = Path.GetFullPath(ownedDirectory);
                var parentDirectory = Directory.GetParent(normalizedOwnedDirectory)?.FullName
                    ?? throw new InvalidOperationException("Owned TIA archive workspace has no parent directory.");
                EnsureOwnedChildPath(parentDirectory, normalizedOwnedDirectory);
                if (Directory.Exists(normalizedOwnedDirectory))
                {
                    logger.LogInformation("Deleting owned TIA project archive workspace.");
                    logger.LogDebug(
                        "Deleting owned TIA project archive workspace. ArchiveWorkDirectory={ArchiveWorkDirectory}",
                        normalizedOwnedDirectory);
                    Directory.Delete(normalizedOwnedDirectory, recursive: true);
                }

                return null;
            }
            catch (Exception ex)
            {
                var rootException = ex.GetBaseException();
                logger.LogWarning(rootException, "Owned TIA project archive workspace cleanup failed.");
                return rootException;
            }
        }


        private void EnsureOwnedChildPath(string baseDirectory, string childDirectory)
        {
            var normalizedBaseDirectory = Path.GetFullPath(baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedChildDirectory = Path.GetFullPath(childDirectory);
            var requiredPrefix = normalizedBaseDirectory + Path.DirectorySeparatorChar;
            if (!normalizedChildDirectory.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "TIA archive workspace must be a child of its configured base directory.");
            }
        }


        public void EnableSimulationDuringBlockCompilation(object project)
        {
            var property = project.GetType().GetProperty("IsSimulationDuringBlockCompilationEnabled");
            if (property is null || property.PropertyType != typeof(bool) || !property.CanWrite)
            {
                logger.LogDebug("Simulation during block compilation property is unavailable.");
                return;
            }

            var enabled = property.GetValue(project, null) is true;
            if (enabled)
            {
                logger.LogDebug("Simulation during block compilation is already enabled.");
                return;
            }

            logger.LogDebug("Enabling simulation during block compilation.");
            property.SetValue(project, true, null);
        }


        public string ReadProjectVersion(object project)
        {
            try
            {
                var version = project.GetType().GetProperty("Version")?.GetValue(project, null);
                var versionText = version?.ToString();
                if (string.IsNullOrWhiteSpace(versionText))
                {
                    return "17";
                }

                return versionText!;
            }
            catch
            {
                return "17";
            }
        }


        public Exception? CloseProject(object? project)
        {
            if (project is null)
            {
                return null;
            }

            try
            {
                logger.LogInformation("Closing TIA project.");
                var close = project.GetType().GetMethod("Close", Type.EmptyTypes);
                if (close is null)
                {
                    throw new InvalidOperationException("TIA project Close() method was not found.");
                }

                close.Invoke(project, null);
                logger.LogInformation("TIA project closed.");
                return null;
            }
            catch (Exception ex)
            {
                var rootException = ex.GetBaseException();
                logger.LogWarning(rootException, "TIA project close failed during cleanup.");
                return rootException;
            }
        }


        public Exception? TryDisposePortal(object? tiaPortal)
        {
            try
            {
                DisposePortal(tiaPortal);
                return null;
            }
            catch (Exception ex)
            {
                var rootException = ex.GetBaseException();
                logger.LogWarning(rootException, "TIA Portal session disposal failed during cleanup.");
                return rootException;
            }
        }


        public void DisposePortal(object? tiaPortal)
        {
            if (tiaPortal is not IDisposable disposable)
            {
                return;
            }

            logger.LogInformation("Disposing TIA Portal session.");
            disposable.Dispose();
            logger.LogInformation("TIA Portal session disposed.");
        }


    }
}
