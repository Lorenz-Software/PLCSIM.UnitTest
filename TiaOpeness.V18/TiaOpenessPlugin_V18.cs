using ApplicationUtilities.DI;
using System;
using System.Reflection;
using TiaOpeness.V18.Internal;

namespace TiaOpeness.V18
{
    public class TiaOpenessPlugin_V18 : TiaOpenessPlugin
    {
		private const string DOMAINNAME = "TiaV18";
        private const string PLUGINNAME = "TIA Openess Plugin";
        private const string VERSION = "18.0.0.0";
        private const string CMDOPTION = "v18";
        private const string DESCRIPTION = "TIA Openess Plugin (v18.0)";

        public TiaOpenessPlugin_V18(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
            this.description = DESCRIPTION;
            this.domainName = DOMAINNAME;
        }

        public override bool IsTiaOpenessInstalled()
        {
            return TiaOpenessHelper_V18.IsInstalled();
        }

        public override void AllowFirewallAccess(Assembly assembly)
        {
            if (IsTiaOpenessInstalled())
                TiaOpenessFirewall_V18.AllowAccess(assembly);
        }

        public override bool Initialize()
        {
            string tiaInstallationPath = TiaOpenessHelper_V18.GetInstallationPath();
            domain = CreateDomain(tiaInstallationPath);
            AppDomain.CurrentDomain.AssemblyResolve += TiaOpenessApiResolver_V18.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        protected override ITiaOpeness CreateTiaOpenessInstance()
        {
            return new TiaOpeness_V18();
        }

    }
}
