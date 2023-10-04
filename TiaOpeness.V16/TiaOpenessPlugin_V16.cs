using ApplicationUtilities.DI;
using System;
using System.Reflection;
using TiaOpeness.V16.Internal;

namespace TiaOpeness.V16
{
    public class TiaOpenessPlugin_V16 : TiaOpenessPlugin
    {
        private const string DOMAINNAME = "TiaV16";
        private const string PLUGINNAME = "TIA Openess Plugin";
        private const string VERSION = "16.0.0.0";
        private const string CMDOPTION = "v16";
        private const string DESCRIPTION = "TIA Openess Plugin (v16.0)";

        public TiaOpenessPlugin_V16(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
            this.description = DESCRIPTION;
            this.domainName = DOMAINNAME;
        }

        public override bool IsTiaOpenessInstalled()
        {
            return TiaOpenessHelper_V16.IsInstalled();
        }

        public override void AllowFirewallAccess(Assembly assembly)
        {
            if (IsTiaOpenessInstalled())
                TiaOpenessFirewall_V16.AllowAccess(assembly);
        }

        public override bool Initialize()
        {
            string tiaInstallationPath = TiaOpenessHelper_V16.GetInstallationPath();
            domain = CreateDomain(tiaInstallationPath);
            AppDomain.CurrentDomain.AssemblyResolve += TiaOpenessApiResolver_V16.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        protected override ITiaOpeness CreateTiaOpenessInstance()
        {
            return new TiaOpeness_V16();
        }
    }
}
