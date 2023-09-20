using log4net.Appender;
using log4net.Core;
using ApplicationUtilities.Logger;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace PLCSIM.UnitTest.Logger
{
    public class Log4NetNotifyAppender : AppenderSkeleton, INotifyPropertyChanged
    {
        private static string _notification;
        private event PropertyChangedEventHandler _propertyChanged;

        public event PropertyChangedEventHandler PropertyChanged
        {
            add { _propertyChanged += value; }
            remove { _propertyChanged -= value; }
        }

        public EnumLogLevel LogLevel
        {
            get
            {
                Level level = Threshold;
                if (level == Level.Emergency || level == Level.Fatal || level == Level.Critical)
                    return EnumLogLevel.Critical;
                if (level == Level.Error || level == Level.Severe)
                    return EnumLogLevel.Error;
                if (level == Level.Warn)
                    return EnumLogLevel.Warn;
                if (level == Level.Notice || level == Level.Info)
                    return EnumLogLevel.Info;
                if (level == Level.Verbose || level == Level.Fine || level == Level.Finer || level == Level.Finest)
                    return EnumLogLevel.Verbose;
                if (level == Level.Debug)
                    return EnumLogLevel.Debug;
                if (level == Level.Trace)
                    return EnumLogLevel.Trace;
                return EnumLogLevel.All;
            }
            set
            {
                switch (value)
                {
                    case EnumLogLevel.Critical:
                        Threshold = Level.Critical;
                        break;
                    case EnumLogLevel.Error:
                        Threshold = Level.Error;
                        break;
                    case EnumLogLevel.Warn:
                        Threshold = Level.Warn;
                        break;
                    case EnumLogLevel.Info:
                        Threshold = Level.Info;
                        break;
                    case EnumLogLevel.Verbose:
                        Threshold = Level.Verbose;
                        break;
                    case EnumLogLevel.Debug:
                        Threshold = Level.Debug;
                        break;
                    case EnumLogLevel.Trace:
                        Threshold = Level.Trace;
                        break;
                    case EnumLogLevel.All:
                        Threshold = Level.All;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(value));
                }
            }
        }

        /// <summary>
        /// Get or set the notification message.
        /// </summary>
        public string Notification
        {
            get
            {
                return _notification;
            }
            set
            {
                if (_notification != value)
                {
                    _notification = value;
                    OnChange();
                }
            }
        }

        /// <summary>
        /// Raise the change notification.
        /// </summary>
        private void OnChange()
        {
            PropertyChangedEventHandler handler = _propertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(string.Empty));
            }
        }

        ///// <summary>
        ///// Get a reference to the log instance.
        ///// </summary>
        public Log4NetNotifyAppender Appender
        {
            get
            {
                return ApplicationLogger.Appender;
            }

        }

        /// <summary>
        /// Append the log information to the notification.
        /// </summary>
        /// <param name="loggingEvent">The log event.</param>
        protected override void Append(LoggingEvent loggingEvent)
        {
            StringWriter writer = new StringWriter(CultureInfo.InvariantCulture);
            Layout.Format(writer, loggingEvent);
            Notification += writer.ToString();
        }

        public void Clear()
        {
            Notification = "";
        }
    }
}
