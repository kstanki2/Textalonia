using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class DocxPackageReadBudgetTests
{
    [Fact]
    public async Task Repeated_xml_access_counts_each_verified_part_once()
    {
        using var original = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(new FlowDocument([new Paragraph("Verified once")]), original);

        using var expanded = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(original.ToArray()), ZipArchiveMode.Read))
        using (var target = new ZipArchive(expanded, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var output = target.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                var commentLength = entry.FullName switch
                {
                    "word/document.xml" or "word/styles.xml" => 30 * 1024 * 1024,
                    "word/settings.xml" => 10 * 1024 * 1024,
                    _ => 0
                };
                if (commentLength == 0)
                {
                    input.CopyTo(output);
                    continue;
                }

                var xml = XDocument.Load(input);
                xml.Root!.Add(new XComment(new string('x', commentLength)));
                using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                xml.Save(writer, SaveOptions.DisableFormatting);
            }
        }

        expanded.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(expanded);
        Assert.Equal("Verified once", loaded.PlainText);
    }
}
