using ApplicationUtilities.Logger;
using log4net;
using log4net.Appender;
using log4net.Config;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace PLCSIM.UnitTest.Logger
{
    [Export(typeof(IApplicationLogger))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class ApplicationLogger : IApplicationLogger
    {
        private static string SEPARATOR = "----------------------------------------";
        private static readonly ILog _logger = LogManager.GetLogger(typeof(ApplicationLogger));
        private static Dictionary<EnumLogLevel, Action<string>> _actions;
        public static int FlushTimeoutInMs = 5000;

        /// <summary>
        /// Get the appender <see cref="Log4NetNotifyAppender"/>.
        /// </summary>
        /// <returns>The instance of the <see cref="Log4NetNotifyAppender"/>, if configured.
        /// Null otherwise.</returns>
        public static Log4NetNotifyAppender Appender
        {
            get
            {
                foreach (ILog log in LogManager.GetCurrentLoggers())
                {
                    foreach (IAppender appender in log.Logger.Repository.GetAppenders())
                    {
                        if (appender is Log4NetNotifyAppender)
                        {
                            return appender as Log4NetNotifyAppender;
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
            //XmlConfigurator.Configure();
            _actions = new Dictionary<EnumLogLevel, Action<string>>();
            _actions.Add(EnumLogLevel.All, Log);
            _actions.Add(EnumLogLevel.Debug, Debug);
            _actions.Add(EnumLogLevel.Info, Info);
            _actions.Add(EnumLogLevel.Warn, Warn);
            _actions.Add(EnumLogLevel.Error, Error);
            _actions.Add(EnumLogLevel.Critical, Fatal);
            Appender.LogLevel = EnumLogLevel.Info;
        }

        public EnumLogLevel Threshold
        {
            get => Appender.LogLevel;
            set => Appender.LogLevel = value;
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
            Debug(data);
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
            Appender.Clear();
        }
    }
}
