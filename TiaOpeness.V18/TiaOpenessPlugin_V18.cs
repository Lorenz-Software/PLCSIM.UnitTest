using ApplicationUtilities.DI;
using System;
using System.Reflection;

namespace TiaOpeness.V18
{
    public class TiaOpenessPlugin_V18 : TiaOpenessPlugin
    {
        private const string PLUGINNAME = "TIA Openess Plugin";
        private const string VERSION = "18.0.0.0";
        private const string CMDOPTION = "v18";

        public TiaOpenessPlugin_V18(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
        }

        public override bool IsTiaOpenessInstalled()
        {
            return TiaOpenessApiResolver_V18.IsInstalled();
        }

        public override void AllowFirewallAccess(Assembly assembly)
        {
            if (IsTiaOpenessInstalled())
                TiaOpenessFirewall_V18.AllowAccess(assembly);
        }

        public override bool Initialize()
        {
            TiaOpenessApiResolver_V18.CreateDomain();
            AppDomain.CurrentDomain.AssemblyResolve += TiaOpenessApiResolver_V18.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        protected override ITiaOpeness CreateTiaOpenessInstance()
        {
            return new TiaOpeness_V18();
        }

        public override void Cleanup()
        {
            tia = null;
            TiaOpenessApiResolver_V18.UnloadDomain();
            isInitialized = false;
        }
    }
}
