using ApplicationUtilities.Logger;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition.Primitives;
using System.Linq;

namespace ApplicationUtilities.DI
{
    public static class DependencyInjectionHelper
    {

        public static void LogParts()
        {
            var logger = Context.Get<IApplicationLogger>();
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));
            foreach (var part in Context.Container.Catalog.Parts)
            {
                logger.Debug($"Dependency injection definition: {part}");
                foreach (var import in part.ImportDefinitions)
                    logger.Debug($"\tImport: {import.ContractName}");
                foreach (var export in part.ExportDefinitions)
                    logger.Debug($"\tExport: {export.ContractName}");
            }
        }

        public static void LogSuccessfulImports()
        {
            var logger = Context.Get<IApplicationLogger>();
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));
            var imports = Context.Container.Catalog
                            .SelectMany(part => part.ImportDefinitions)
                            .Where(impDef => !IsInvalidImport(impDef))
                            .Select(impDef => impDef.ContractName)
                            .Distinct()
                            .OrderBy(contract => contract)
                            .ToList();
            foreach (var import in imports)
                logger.Debug($"Dependency injection import: {import}");
        }

        public static void LogMissingImports()
        {
            var logger = Context.Get<IApplicationLogger>();
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));
            var brokenExports = GetBrokenImportExports();
            var imports = Context.Container.Catalog
                            .SelectMany(part => part.ImportDefinitions)
                            .Where(impDef => IsInvalidImport(impDef))
                            .Select(impDef => impDef.ContractName)
                            .Distinct()
                            .Where(contract => !brokenExports.Contains(contract))
                            .OrderBy(contract => contract)
                            .ToList();
            if (imports.Count == 0)
                logger.Debug($"No missing dependency injection imports");
            else
            {
                foreach (var import in imports)
                    logger.Debug($"Missing dependency injection import: {import}");
            }
        }

        public static List<string> GetBrokenImportExports()
        {
            return Context.Container.Catalog
                            .Where(part => part.ImportDefinitions.Any(impDef => IsInvalidImport(impDef)))
                            .SelectMany(part => part.ExportDefinitions)
                            .Select(exDef => exDef.ContractName)
                            .Distinct()
                            .OrderBy(contract => contract)
                            .ToList();
        }

        private static bool IsInvalidImport(ImportDefinition impDef)
        {
            try
            {
                Context.Container.GetExports(impDef);
            }
            catch
            {
                return true;
            }

            return false;
        }
    }
}
