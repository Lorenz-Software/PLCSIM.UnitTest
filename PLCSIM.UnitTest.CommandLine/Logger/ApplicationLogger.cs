using ApplicationUtilities.Logger;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Repository;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace PLCSIM.UnitTest.CommandLine.Logger
{
    [Export(typeof(IApplicationLogger))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class ApplicationLogger : IApplicationLogger
    {
        private static string SEPARATOR = "----------------------------------------";
        private static readonly ILog _logger = LogManager.GetLogger(typeof(ApplicationLogger));
        private static Dictionary<EnumLogLevel, Action<string>> _actions;
        public static int FlushTimeoutInMs = 5000;

        public static ConsoleAppender Appender
        {
            get
            {
                foreach (ILoggerRepository repo in LogManager.GetAllRepositories())
                {
                    foreach (IAppender appender in repo.GetAppenders())
                    {
                        if (appender is ConsoleAppender)
                        {
                            return appender as ConsoleAppender;
                        }
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Get available logging levels.
        /// </summary>
        /// <returns>Returns collection of <see cref="EnumLogLevel"/>.</returns>
        public IEnumerable<EnumLogLevel> LogLevels
        {
            get
            {
                return Enum.GetValues(typeof(EnumLogLevel))
                    .Cast<EnumLogLevel>();
            }
        }

        public ApplicationLogger()
        {
            XmlConfigurator.Configure(new System.IO.FileInfo
                                            (System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)));
            _actions = new Dictionary<EnumLogLevel, Action<string>>();
            _actions.Add(EnumLogLevel.All, Log);
            _actions.Add(EnumLogLevel.Debug, Debug);
            _actions.Add(EnumLogLevel.Verbose, Verbose);
            _actions.Add(EnumLogLevel.Info, Info);
            _actions.Add(EnumLogLevel.Warn, Warn);
            _actions.Add(EnumLogLevel.Error, Error);
            _actions.Add(EnumLogLevel.Critical, Fatal);
        }

        public EnumLogLevel Threshold
        {
            get => getLogLevel(Appender.Threshold);
            set => Appender.Threshold = getLogLevel(value);
        }

        public void Trace(string data)
        {
            Debug(data);
        }

        public void Debug(string data)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug(data);
            }
        }

        public void Verbose(string data)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug(data);
            }
        }

        public void Info(string data)
        {
            if (_logger.IsInfoEnabled)
                _logger.Info(data);
        }

        public void Warn(string data)
        {
            if (_logger.IsWarnEnabled)
                _logger.Warn(data);
        }

        public void Error(string data)
        {
            if (_logger.IsErrorEnabled)
                _logger.Error(data);
        }

        public void Fatal(string data)
        {
            if (_logger.IsFatalEnabled)
                _logger.Fatal(data);
        }

        public void Log(string data)
        {
            Log(EnumLogLevel.All, data);
        }

        public void Log(EnumLogLevel level, string data)
        {
            if (!string.IsNullOrEmpty(data))
            {
                if (level > EnumLogLevel.All || level < EnumLogLevel.Trace)
                    throw new ArgumentOutOfRangeException("level");

                // Now call the appropriate log level message.
                _actions[level](data);
            }
        }

        public void Log(Exception ex)
        {
            Error($"Type: {ex.GetType()}");
            Error(ex.Message);
            Info(ex.StackTrace);
        }

        public void Separator()
        {
            Log(SEPARATOR);
        }

        public void Flush()
        {
            Appender.Flush(FlushTimeoutInMs);
        }

        private Level getLogLevel(EnumLogLevel logLevel)
        {
            switch (logLevel)
            {
                case EnumLogLevel.All:
                    return Level.All;
                case EnumLogLevel.Trace:
                    return Level.Trace;
                case EnumLogLevel.Debug:
                    return Level.Debug;
                case EnumLogLevel.Verbose:
                    return Level.Verbose;
                case EnumLogLevel.Info:
                    return Level.Info;
                case EnumLogLevel.Warn:
                    return Level.Warn;
                case EnumLogLevel.Error:
                    return Level.Error;
                case EnumLogLevel.Critical:
                    return Level.Critical;
                default:
                    return Level.All;
            }
        }
        private EnumLogLevel getLogLevel(Level logLevel)
        {
            if (logLevel == Level.Debug)
                return EnumLogLevel.Debug;
            if (logLevel == Level.Trace)
                return EnumLogLevel.Trace;
            if (logLevel == Level.Verbose)
                return EnumLogLevel.Verbose;
            if (logLevel == Level.Info)
                return EnumLogLevel.Info;
            if (logLevel == Level.Warn)
                return EnumLogLevel.Warn;
            if (logLevel == Level.Error)
                return EnumLogLevel.Error;
            if (logLevel == Level.Critical)
                return EnumLogLevel.Critical;
            if (logLevel == Level.Fatal)
                return EnumLogLevel.Critical;
            return EnumLogLevel.All;
        }
    }
}
