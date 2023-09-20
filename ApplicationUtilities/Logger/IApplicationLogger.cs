using System;
using System.Collections.Generic;

namespace ApplicationUtilities.Logger
{
    public interface IApplicationLogger
    {
        IEnumerable<EnumLogLevel> LogLevels { get; }

        EnumLogLevel Threshold { get; set; }

        void Log(string data);

        void Log(EnumLogLevel level, string data);

        void Log(Exception ex);

        void Trace(string data);

        void Debug(string data);

        void Verbose(string data);

        void Info(string data);

        void Warn(string data);

        void Error(string data);

        void Fatal(string data);

        void Separator();

        void Flush();

    }
}
