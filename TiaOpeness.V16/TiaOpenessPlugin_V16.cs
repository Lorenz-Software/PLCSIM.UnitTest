using ApplicationUtilities.DI;
using System;
using System.Reflection;

namespace TiaOpeness.V16
{
    class TiaOpenessPlugin_V16 : TiaOpenessPlugin
    {
        private const string PLUGINNAME = "TIA Openess Plugin";
        private const string VERSION = "16.0.0.0";
        private const string CMDOPTION = "v16";

        public TiaOpenessPlugin_V16(Context context) : base(context)
        {
            this.name = PLUGINNAME;
            this.version = new Version(VERSION);
            this.cmdOption = CMDOPTION;
        }

        public override bool IsTiaOpenessInstalled()
        {
            return TiaOpenessApiResolver_V16.IsInstalled();
        }

        public override void AllowFirewallAccess(Assembly assembly)
        {
            if (IsTiaOpenessInstalled())
                TiaOpenessFirewall_V16.AllowAccess(assembly);
        }

        public override bool Initialize()
        {
            TiaOpenessApiResolver_V16.CreateDomain();
            AppDomain.CurrentDomain.AssemblyResolve += TiaOpenessApiResolver_V16.AssemblyResolver;

            isInitialized = true;
            return isInitialized;
        }

        protected override ITiaOpeness CreateTiaOpenessInstance()
        {
            return new TiaOpeness_V16();
        }

        public override void Cleanup()
        {
            tia = null;
            TiaOpenessApiResolver_V16.UnloadDomain();
            isInitialized = false;
        }
    }
}
