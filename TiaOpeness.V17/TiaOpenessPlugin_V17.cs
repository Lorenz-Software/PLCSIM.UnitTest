using ApplicationUtilities.DI;
using System;
using System.Reflection;

namespace TiaOpeness.V17
{
    public class TiaOpenessPlugin_V17 : TiaOpenessPlugin
    {
        private const string PLUGINNAME = "TIA Openess Plugin";
        private const string VERSION = "17.0.0.0";
        private const string CMDOPTION = "v17";

        public TiaOpenessPlugin_V17(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
        }

        public override bool IsTiaOpenessInstalled()
        {
            return TiaOpenessApiResolver_V17.IsInstalled();
        }

        public override void AllowFirewallAccess(Assembly assembly)
        {
            if (IsTiaOpenessInstalled())
                TiaOpenessFirewall_V17.AllowAccess(assembly);
        }

        public override bool Initialize()
        {
            TiaOpenessApiResolver_V17.CreateDomain();
            AppDomain.CurrentDomain.AssemblyResolve += TiaOpenessApiResolver_V17.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        protected override ITiaOpeness CreateTiaOpenessInstance()
        {
            return new TiaOpeness_V17();
        }

        public override void Cleanup()
        {
            tia = null;
            TiaOpenessApiResolver_V17.UnloadDomain();
            isInitialized = false;
        }
    }
}
