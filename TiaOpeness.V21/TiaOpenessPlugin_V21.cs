using ApplicationUtilities.DI;
using System;
using System.Reflection;
using TiaOpeness.V21.Internal;

namespace TiaOpeness.V21
{
    public class TiaOpenessPlugin_V21 : TiaOpenessPlugin
    {
		private const string DOMAINNAME = "TiaV21";
        private const string PLUGINNAME = "TIA Openess Plugin";
        private const string VERSION = "21.0.0.0";
        private const string CMDOPTION = "v21";
        private const string DESCRIPTION = "TIA Openess Plugin (v21.0)";

        public TiaOpenessPlugin_V21(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
            this.description = DESCRIPTION;
            this.domainName = DOMAINNAME;
        }

        public override bool IsTiaOpenessInstalled()
        {
            return TiaOpenessHelper_V21.IsInstalled();
        }

        public override void AllowFirewallAccess(Assembly assembly)
        {
            if (IsTiaOpenessInstalled())
                TiaOpenessFirewall_V21.AllowAccess(assembly);
        }

        public override bool Initialize()
        {
            string tiaInstallationPath = TiaOpenessHelper_V21.GetInstallationPath();
            domain = CreateDomain(tiaInstallationPath);
            AppDomain.CurrentDomain.AssemblyResolve += TiaOpenessApiResolver_V21.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        protected override ITiaOpeness CreateTiaOpenessInstance()
        {
            return new TiaOpeness_V21();
        }

    }
}
