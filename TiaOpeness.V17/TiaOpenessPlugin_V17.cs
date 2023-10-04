using ApplicationUtilities.DI;
using System;
using System.Reflection;
using TiaOpeness.V17.Internal;

namespace TiaOpeness.V17
{
    public class TiaOpenessPlugin_V17 : TiaOpenessPlugin
    {
        private const string DOMAINNAME = "TiaV17";
        private const string PLUGINNAME = "TIA Openess Plugin";
        private const string VERSION = "17.0.0.0";
        private const string CMDOPTION = "v17";
        private const string DESCRIPTION = "TIA Openess Plugin (v17.0)";

        public TiaOpenessPlugin_V17(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
            this.description = DESCRIPTION;
            this.domainName = DOMAINNAME;
        }

        public override bool IsTiaOpenessInstalled()
        {
            return TiaOpenessHelper_V17.IsInstalled();
        }

        public override void AllowFirewallAccess(Assembly assembly)
        {
            if (IsTiaOpenessInstalled())
                TiaOpenessFirewall_V17.AllowAccess(assembly);
        }

        public override bool Initialize()
        {
            string tiaInstallationPath = TiaOpenessHelper_V17.GetInstallationPath();
            domain = CreateDomain(tiaInstallationPath);
            AppDomain.CurrentDomain.AssemblyResolve += TiaOpenessApiResolver_V17.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        protected override ITiaOpeness CreateTiaOpenessInstance()
        {
            return new TiaOpeness_V17();
        }

    }
}
