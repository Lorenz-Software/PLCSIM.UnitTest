using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PLCSIM.UnitTest.CommandLine.Options
{
    abstract class BaseOptions
    {
        [Option(shortName: 'w', longName: "wait", Required = false, HelpText = "Wait for user confirmation before ending application.", Default = false)]
        public bool Wait { get; set; }

        [Option("verbose", Required = false, HelpText = "Set logging output to verbose.")]
        public bool Verbose { get; set; }

    }
}
