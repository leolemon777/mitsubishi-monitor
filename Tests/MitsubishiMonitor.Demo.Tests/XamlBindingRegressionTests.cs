using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace MitsubishiMonitor.Demo.Tests
{
    public class XamlBindingRegressionTests
    {
        [Fact]
        public void RunTextBindings_DeclareOneWayOrOneTimeMode()
        {
            var repositoryRoot = GetRepositoryRoot();
            var offenders = new List<string>();

            foreach (var path in Directory.EnumerateFiles(repositoryRoot, "*.xaml", SearchOption.AllDirectories)
                         .Where(path => !IsGeneratedOrPackagedPath(path)))
            {
                var document = XDocument.Load(path, LoadOptions.SetLineInfo);
                foreach (var run in document.Descendants().Where(element => element.Name.LocalName == "Run"))
                {
                    var text = run.Attribute("Text")?.Value ?? "";
                    if (!text.TrimStart().StartsWith("{Binding", StringComparison.Ordinal) ||
                        text.Contains("Mode=OneWay", StringComparison.Ordinal) ||
                        text.Contains("Mode=OneTime", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var lineInfo = (IXmlLineInfo)run;
                    offenders.Add($"{Path.GetRelativePath(repositoryRoot, path)}:{lineInfo.LineNumber}");
                }
            }

            Assert.True(
                offenders.Count == 0,
                "Run.Text 默认绑定模式可能尝试写回只读属性并导致窗口启动崩溃。请显式设置 Mode=OneWay/OneTime：" +
                string.Join(", ", offenders));
        }

        private static bool IsGeneratedOrPackagedPath(string path)
        {
            var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRepositoryRoot([CallerFilePath] string sourceFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "..", ".."));
    }
}
