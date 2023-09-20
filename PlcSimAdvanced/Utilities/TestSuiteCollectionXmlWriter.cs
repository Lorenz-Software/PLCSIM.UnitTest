using PlcSimAdvanced.Model;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml;

namespace PlcSimAdvanced.Utilities
{
    public class TestSuiteCollectionXmlWriter
    {
        private Stream stream;
        private string prefix = null;
        private string ns = null;

        public TestSuiteCollectionXmlWriter(Stream stream)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public async Task Write(TestSuiteCollection suiteCollection)
        {
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Async = true;
            settings.Indent = true;

            using (XmlWriter writer = XmlWriter.Create(stream, settings))
            {
                await writeTestSuiteCollection(writer, suiteCollection);
                await writer.FlushAsync();
            }
        }

        private async Task writeTestSuiteCollection(XmlWriter writer, TestSuiteCollection suiteCollection)
        {
            await writer.WriteStartElementAsync(prefix, "testsuites", ns);

            await writer.WriteAttributeStringAsync(prefix, "timestamp", ns, suiteCollection.EndTime.ToString("s"));
            await writer.WriteAttributeStringAsync(prefix, "time", ns, suiteCollection.Duration.TotalSeconds.ToString());
            await writer.WriteAttributeStringAsync(prefix, "tests", ns, suiteCollection.TestCount.ToString());
            await writer.WriteAttributeStringAsync(prefix, "failures", ns, suiteCollection.Failed.ToString());
            await writer.WriteAttributeStringAsync(prefix, "errors", ns, suiteCollection.Errors.ToString());
            await writer.WriteAttributeStringAsync(prefix, "skipped", ns, suiteCollection.Skipped.ToString());

            foreach (var suite in suiteCollection.TestSuites)
                await writeTestSuite(writer, suite);

            await writer.WriteEndElementAsync();
        }

        private async Task writeTestSuite(XmlWriter writer, TestSuite suite)
        {
            await writer.WriteStartElementAsync(prefix, "testsuite", ns);

            await writer.WriteAttributeStringAsync(prefix, "name", ns, suite.Name);
            await writer.WriteAttributeStringAsync(prefix, "timestamp", ns, suite.EndTime.ToString("s"));
            await writer.WriteAttributeStringAsync(prefix, "time", ns, suite.Duration.TotalSeconds.ToString());
            await writer.WriteAttributeStringAsync(prefix, "tests", ns, suite.TestCount.ToString());
            await writer.WriteAttributeStringAsync(prefix, "failures", ns, suite.Failed.ToString());
            await writer.WriteAttributeStringAsync(prefix, "errors", ns, suite.Errors.ToString());
            await writer.WriteAttributeStringAsync(prefix, "skipped", ns, suite.Skipped.ToString());

            foreach (var test in suite.Tests)
                await writeTestCase(writer, suite.Name, test);

            await writer.WriteEndElementAsync();
        }

        private async Task writeTestCase(XmlWriter writer, string suiteName, TestCase test)
        {
            await writer.WriteStartElementAsync(prefix, "testcase", ns);

            await writer.WriteAttributeStringAsync(prefix, "name", ns, test.Name);
            await writer.WriteAttributeStringAsync(prefix, "classname", ns, suiteName);
            await writer.WriteAttributeStringAsync(prefix, "time", ns, test.Duration.TotalSeconds.ToString());

            if (test.HasError)
            {
                await writer.WriteStartElementAsync(prefix, "error", ns);
                await writer.WriteAttributeStringAsync(prefix, "message", ns, test.ErrorMessage);
                await writer.WriteEndElementAsync();
            }
            else
            {
                if (test.IsFailed)
                {
                    await writer.WriteStartElementAsync(prefix, "failure", ns);
                    await writer.WriteAttributeStringAsync(prefix, "message", ns, test.FailMessage);
                    await writer.WriteAttributeStringAsync(prefix, "type", ns, "AssertionError");
                    await writer.WriteEndElementAsync();
                }
                else
                {
                    if (test.IsSkipped)
                    {
                        await writer.WriteElementStringAsync(prefix, "skipped", ns, null);
                    }
                }
            }

            await writer.WriteEndElementAsync();
        }
    }
}
