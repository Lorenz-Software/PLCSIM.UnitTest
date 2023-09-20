using CommandLine;
using System;

namespace PLCSIM.UnitTest.CommandLine.Options
{
    [Verb("run", HelpText = "Run unit tests")]
    class UnitTestOptions : BaseOptions
    {
        private static readonly TimeSpan defaultTimeout = TimeSpan.FromMinutes(5);
        private const string defaultTimeoutStr = "(Default: 5m) ";

        [Option(shortName: 'v', longName: "version", Required = true, HelpText = "TIA version")]
        public string Version { get; set; }

        [Option(shortName: 'f', longName: "file", Required = true, HelpText = "TIA project file")]
        public string ProjectFile { get; set; }

        [Option(shortName: 'p', longName: "plc", Required = true, HelpText = "PLC name")]
        public string Plc { get; set; }

        [Option(shortName: 't', longName: "timeout", Required = false, HelpText = defaultTimeoutStr + "Timeout for unit test execution")]
        public TimeSpan Timeout { get; set; } = defaultTimeout;

        [Option(shortName: 'o', longName: "output", Required = true, HelpText = "Output file for unit test results")]
        public string OutputFile { get; set; }



    }
}
