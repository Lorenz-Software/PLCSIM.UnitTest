using CommandLine;
using System.Collections.Generic;

namespace PLCSIM.UnitTest.CommandLine.Options
{
    [Verb("whitelist", HelpText = "Whitelist app in TIA Portal Openess firewall (Administrator only)")]
    class WhitelistAppOptions : BaseOptions
    {
        [Option(shortName: 'v', longName: "versions", Required = true, HelpText = "TIA Portal versions")]
        public IEnumerable<string> Versions { get; set; }
    }
}
