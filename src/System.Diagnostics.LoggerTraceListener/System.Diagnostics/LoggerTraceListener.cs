using System.Text;
using System.Xml;
using System.Xml.XPath;
using System.IO;
using System.Globalization;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace System.Diagnostics
{
    public class LoggerTraceListener : TraceListener
    {
        private const String TraceAsTraceSource = "Trace";
        private readonly ILoggerFactory m_loggerFactory;
        private StringBuilder strBldr = null;
        private XmlTextWriter xmlBlobWriter = null;

        public LoggerTraceListener(ILoggerFactory loggerFactory) : base()
        {
            m_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        public LoggerTraceListener(ILoggerFactory loggerFactory, String name) : base(name)
        {
            m_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        public override void Close()
        {
            if (xmlBlobWriter != null)
            {
                xmlBlobWriter.Close();
            }

            xmlBlobWriter = null;
            strBldr = null;
        }

        public override void Write(string message)
        {
            this.WriteLine(message);
        }

        public override void WriteLine(string message)
        {
            this.TraceEvent(null, TraceAsTraceSource, TraceEventType.Information, 0, message);
        }

        public override void Fail(string message, string detailMessage)
        {
            StringBuilder failMessage = new StringBuilder(message);
            if (detailMessage != null)
            {
                failMessage.Append(" ");
                failMessage.Append(detailMessage);
            }

            this.TraceEvent(null, TraceAsTraceSource, TraceEventType.Error, 0, failMessage.ToString());
        }


        public override void TraceEvent(TraceEventCache eventCache, String source, TraceEventType eventType, int id, string format, params object[] args)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, eventType, id, format, args, null, null))
            {
                return;
            }

            var dictionary = CreateScopeDictionary(eventCache);
            var message = (args != null) ? String.Format(CultureInfo.InvariantCulture, format, args) : format;

            Log(source, eventType, id, dictionary, message);
        }

        public override void TraceEvent(TraceEventCache eventCache, String source, TraceEventType eventType, int id, string message)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null))
            {
                return;
            }

            var dictionary = CreateScopeDictionary(eventCache);

            Log(source, eventType, id, dictionary, message);
        }

        public override void TraceData(TraceEventCache eventCache, String source, TraceEventType eventType, int id, object data)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, eventType, id, null, null, data, null))
            {
                return;
            }

            var dictionary = CreateScopeDictionary(eventCache);
            if (data != null)
            {
                dictionary.Add("TraceData", data);
            }

            Log(source, eventType, id, dictionary, String.Empty);
        }

        public override void TraceData(TraceEventCache eventCache, String source, TraceEventType eventType, int id, params object[] data)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, eventType, id, null, null, null, data))
            {
                return;
            }

            var dictionary = CreateScopeDictionary(eventCache);
            if (data != null)
            {
                var transformed = new Object[data.Length];
                for (var i = 0; i < data.Length; i++)
                {
                    transformed[i] = TransformData(data[i]);
                }

                dictionary.Add("TraceData", transformed);
            }

            Log(source, eventType, id, dictionary, String.Empty);
        }

        public override void TraceTransfer(TraceEventCache eventCache, String source, int id, string message, Guid relatedActivityId)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, TraceEventType.Transfer, id, message, null, null, null))
            {
                return;
            }

            var dictionary = CreateScopeDictionary(eventCache);
            dictionary.Add("RelatedActivityID", relatedActivityId.ToString("B"));

            Log(source, TraceEventType.Transfer, id, dictionary, message);
        }

        private Dictionary<String, Object> CreateScopeDictionary(TraceEventCache eventCache)
        {
            if (eventCache != null)
            {
                var result = new Dictionary<String, Object> {
                    { "TimeCreated", eventCache.DateTime.ToString("o", CultureInfo.InvariantCulture) },
                    { "CorrelationActivityID", Trace.CorrelationManager.ActivityId.ToString("B") },
                    { "ThreadID", eventCache.ThreadId }
                };

                var writeLogicalOps = (TraceOutputOptions & TraceOptions.LogicalOperationStack) != 0;
                var writeCallstack = (TraceOutputOptions & TraceOptions.Callstack) != 0;

                if (writeLogicalOps || writeCallstack)
                {
                    if (writeLogicalOps)
                    {
                        var s = eventCache.LogicalOperationStack;
                        if (s != null && 0 < s.Count)
                        {
                            var list = new List<String>();
                            foreach (Object correlationId in s)
                            {
                                list.Add(correlationId.ToString());
                            }
                        }

                        result.Add("LogicalOperationStack", eventCache.Timestamp.ToString(CultureInfo.InvariantCulture));
                    }

                    result.Add("Timestamp", eventCache.Timestamp.ToString(CultureInfo.InvariantCulture));

                    if (writeCallstack)
                    {
                        result.Add("Callstack", eventCache.Callstack);
                    }
                }

                return result;
            }
            else
            {
                return new Dictionary<String, Object> {
                    { "TimeCreated", DateTime.Now.ToString("o", CultureInfo.InvariantCulture) },
                    { "CorrelationActivityID", Guid.Empty.ToString("B") },
                    { "ThreadID", System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) }
                };
            }
        }

        private static LogLevel ToLogLevel(TraceEventType eventType)
        {
            switch (eventType)
            {
                case TraceEventType.Critical:
                {
                    return LogLevel.Critical;
                }
                case TraceEventType.Error:
                {
                    return LogLevel.Error;
                }
                case TraceEventType.Information:
                {
                    return LogLevel.Information;
                }
                case TraceEventType.Warning:
                {
                    return LogLevel.Warning;
                }
                case TraceEventType.Verbose:
                {
                    return LogLevel.Trace;
                }
                default:
                {
                    return LogLevel.Trace;
                }
            }
        }

        // Special case XPathNavigator dataitems to write out XML blob unescaped
        private Object TransformData(Object data)
        {
            if (data is XPathNavigator xmlBlob)
            {
                if (strBldr == null)
                {
                    strBldr = new StringBuilder();
                    xmlBlobWriter = new XmlTextWriter(new StringWriter(strBldr, CultureInfo.CurrentCulture));
                }
                else
                {
                    strBldr.Length = 0;
                }

                try
                {
                    // Rewind the blob to point to the root, this is needed to support multiple XMLTL in one TraceData call
                    xmlBlob.MoveToRoot();
                    xmlBlobWriter.WriteNode(xmlBlob, false);
                    return strBldr.ToString();
                }
                catch (Exception)
                {
                    // We probably only care about XmlException for ill-formed XML though 
                    return data.ToString();
                }
            }

            return data;
        }

        private void Log(String source, TraceEventType eventType, int id, Dictionary<String, Object> scopeState, string message)
        {
            var logger = m_loggerFactory.CreateLogger(source);

            using (logger.BeginScope(scopeState))
            {
                logger.Log(
                    ToLogLevel(eventType),
                    new EventId(id, eventType.ToString()),
                    message);
            }
        }
    }
}