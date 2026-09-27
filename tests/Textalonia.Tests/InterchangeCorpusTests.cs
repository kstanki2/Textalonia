using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class InterchangeCorpusTests
{
    private static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Interchange");
    public sealed record Specimen(string Id, string Profile, string Format, string File, string? Text,
        string[]? LossCodes, string[]? Semantics, bool Error);
    private static Specimen[] Specimens() => JsonSerializer.Deserialize<Specimen[]>(
        File.ReadAllText(Path.Combine(DirectoryPath, "manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    public static IEnumerable<object[]> Cases() => Specimens().Select(s => new object[] { s.Id });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Locally_authored_corpus_matches_declared_semantics_and_losses(string id)
    {
        var specimen = Specimens().Single(s => s.Id == id);
        using var stream = Open(specimen);
        IDocumentFormat format = specimen.Format switch
        {
            "html" => DocumentFormats.Html, "rtf" => DocumentFormats.Rtf,
            "docx-xml" => DocumentFormats.Docx, _ => DocumentFormats.Json
        };
        if (specimen.Error)
        {
            var exception = await Record.ExceptionAsync(() => format.LoadWithReportAsync(stream));
            Assert.True(exception is FormatException or XmlException or JsonException or InvalidDataException,
                "Malformed fixture must fail, not become a successful empty document: " + exception);
            return;
        }
        var result = await format.LoadWithReportAsync(stream);
        Assert.Equal(specimen.Text, result.Document.PlainText);
        Assert.Equal(specimen.LossCodes!.Order(StringComparer.Ordinal),
            result.Report.Diagnostics.Where(d => d.Severity != ConversionDiagnosticSeverity.Information).Select(d => d.Code).Distinct().Order(StringComparer.Ordinal));
        foreach (var semantic in specimen.Semantics!) AssertSemantic(result.Document, semantic);
        // Compare meaningful properties separately from generated identities/ZIP byte layout.
        using var roundTrip = new MemoryStream();
        await format.SaveWithReportAsync(result.Document, roundTrip);
        roundTrip.Position = 0;
        var restored = await format.LoadWithReportAsync(roundTrip);
        Assert.Equal(result.Document.PlainText, restored.Document.PlainText);
        foreach (var semantic in specimen.Semantics) AssertSemantic(restored.Document, semantic);
        if (specimen.Format == "json")
            Assert.Equal(DocumentFormats.Json.Serialize(result.Document), DocumentFormats.Json.Serialize(restored.Document));
    }

    [Fact]
    public void Corpus_provenance_cannot_be_mistaken_for_native_application_evidence()
    {
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "provenance.json")));
        Assert.Equal(JsonValueKind.Null, provenance.RootElement.GetProperty("sourceApplicationVersion").ValueKind);
        Assert.Equal("unqualified", provenance.RootElement.GetProperty("nativeApplicationQualification").GetString());
        Assert.All(Specimens(), s => Assert.True(File.Exists(Path.Combine(DirectoryPath, s.File)), s.File));
    }

    private static Stream Open(Specimen specimen)
    {
        var path = Path.Combine(DirectoryPath, specimen.File);
        if (specimen.Format != "docx-xml") return File.OpenRead(path);
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("word/document.xml").Open();
            using var xml = File.OpenRead(path);
            xml.CopyTo(entry);
        }
        stream.Position = 0;
        return stream;
    }

    private static void AssertSemantic(FlowDocument document, string semantic)
    {
        var paragraphs = new DocumentIndex(document).Paragraphs.Select(p => p.Paragraph).ToArray();
        var runs = paragraphs.SelectMany(p => p.Runs).ToArray();
        switch (semantic)
        {
            case "bold": Assert.Contains(runs, r => r.Style.EffectiveBold); break;
            case "section": Assert.Contains(document.Blocks, b => b is Section); break;
            case "hyperlink": Assert.Contains(runs, r => r.Style.Hyperlink == "https://example.test/path"); break;
            case "list-start":
                var list = paragraphs.Where(p => p.Style.List != ListKind.None).ToArray();
                Assert.Equal(2, list.Length);
                Assert.NotNull(list[0].Style.ListId);
                Assert.Equal(list[0].Style.ListId, list[1].Style.ListId);
                Assert.Equal(3, list[0].Style.ListStart);
                break;
            case "nested-table": Assert.True(HasNestedTable(document.Blocks)); break;
            case "image":
                var inline = Assert.Single(runs).Inline;
                Assert.NotNull(inline);
                var image = Assert.IsType<ImageInlinePayload>(inline.Payload);
                Assert.Equal(16, inline.Width);
                Assert.Equal(16, inline.Height);
                Assert.Equal("image/png", document.Resources[image.ResourceId].MediaType);
                Assert.True(document.Resources[image.ResourceId].Data.Length > 30);
                break;
            default: throw new InvalidOperationException("Unknown corpus semantic: " + semantic);
        }
    }

    private static bool HasNestedTable(IEnumerable<Block> blocks, bool insideTable = false)
    {
        foreach (var block in blocks)
            if (block is Section section && HasNestedTable(section.Blocks, insideTable)) return true;
            else if (block is Table table)
            {
                if (insideTable) return true;
                if (table.Rows.SelectMany(r => r).Any(c => HasNestedTable(c.Blocks, true))) return true;
            }
        return false;
    }
}
