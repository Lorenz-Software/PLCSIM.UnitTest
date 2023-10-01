using CommandLine;
using System.Collections.Generic;

namespace PLCSIM.UnitTest.CommandLine.Options
{
    [Verb("allow", HelpText = "Allow permanent application access to TIA Portal Openess (Administrator only)")]
    class AllowAppOptions : BaseOptions
    {
        [Option(shortName: 'v', longName: "versions", Required = true, HelpText = "TIA Portal versions")]
        public IEnumerable<string> Versions { get; set; }
    }
}
