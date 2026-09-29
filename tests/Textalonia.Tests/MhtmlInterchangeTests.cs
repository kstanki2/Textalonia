using System.Collections.Immutable;
using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MhtmlInterchangeTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Built_in_mhtml_round_trips_embedded_images_with_one_mime_resource()
    {
        Assert.Same(DocumentFormats.Mhtml, DocumentFormats.ForPath("sample.mht"));
        Assert.Same(DocumentFormats.Mhtml, DocumentFormats.ForPath("sample.mhtml"));
        var first = new InlineDescriptor { AltText = "icon", Width = 20, Height = 12, Payload = new ImageInlinePayload("icon") };
        var source = new FlowDocument([new Paragraph([new RichRun("before "), new RichRun(first), new RichRun(first with { Id = Guid.NewGuid() })])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("icon", new() { MediaType = "image/png", Data = ImmutableArray.CreateRange(Png) }) };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Mhtml.SaveWithReportAsync(source, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        var wire = Encoding.ASCII.GetString(stream.ToArray());
        Assert.Contains("multipart/related", wire);
        Assert.Contains("cid:textalonia-image-1@local", Encoding.UTF8.GetString(Convert.FromBase64String(PartBase64(wire, "text/html"))));
        Assert.Equal(1, wire.Split("Content-Type: image/png", StringSplitOptions.None).Length - 1);
        stream.Position = 0;
        var loaded = await DocumentFormats.Mhtml.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(loaded.Report.Diagnostics);
        Assert.Equal(source.Text, loaded.Document.Text);
        Assert.Equal(Png, Assert.Single(loaded.Document.Resources).Value.Data.ToArray());
        Assert.Equal(2, new DocumentIndex(loaded.Document).Paragraphs.SelectMany(p => p.Paragraph.Runs).Count(r => r.Inline?.Payload is ImageInlinePayload));
        Assert.True(stream.CanRead && stream.CanWrite);
    }

    [Fact]
    public async Task Import_resolves_content_location_and_decodes_quoted_printable_windows_1252()
    {
        var source = "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=\"sample\"; type=\"text/html\"\r\n\r\n" +
            "--sample\r\nContent-Type: text/html; charset=windows-1252\r\nContent-Transfer-Encoding: quoted-printable\r\nContent-Location: https://example.test/report/page.html\r\n\r\n" +
            "<html><body><p>Caf=E9 <img src=3D\"../images/logo.png\" alt=3D\"logo\" width=3D\"10\" height=3D\"8\"></p></body></html>\r\n" +
            "--sample\r\nContent-Type: image/png\r\nContent-Transfer-Encoding: base64\r\nContent-Location: https://example.test/images/logo.png\r\n\r\n" +
            Convert.ToBase64String(Png) + "\r\n--sample--\r\n";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(source));
        var result = await DocumentFormats.Mhtml.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(result.Report.Diagnostics);
        Assert.Equal("Café logo", result.Document.PlainText);
        Assert.Equal(Png, Assert.Single(result.Document.Resources).Value.Data.ToArray());
    }

    [Fact]
    public async Task Missing_resource_reports_loss_and_strict_import_rejects_it()
    {
        const string source = "MIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>safe<img src=\"cid:missing@local\" alt=\"lost\"></p>";
        using var tolerant = new MemoryStream(Encoding.ASCII.GetBytes(source));
        var result = await DocumentFormats.Mhtml.LoadWithReportAsync(tolerant);
        Assert.Equal("safelost", result.Document.PlainText);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "mhtml.resource-missing" && d.SourceLocation == "html:img[1]");
        using var strict = new MemoryStream(Encoding.ASCII.GetBytes(source));
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Mhtml.LoadWithReportAsync(strict, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Incomplete_boundary_and_duplicate_resource_identity_are_rejected()
    {
        const string incomplete = "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=x\r\n\r\n--x\r\nContent-Type: text/html\r\n\r\n<p>x</p>";
        using var missingEnd = new MemoryStream(Encoding.ASCII.GetBytes(incomplete));
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Mhtml.LoadAsync(missingEnd));
        const string duplicated = "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=x\r\n\r\n" +
            "--x\r\nContent-Type: text/html\r\n\r\n<p>x</p>\r\n" +
            "--x\r\nContent-Type: image/png\r\nContent-ID: <same@local>\r\n\r\nA\r\n" +
            "--x\r\nContent-Type: image/png\r\nContent-ID: <same@local>\r\n\r\nB\r\n--x--\r\n";
        using var duplicate = new MemoryStream(Encoding.ASCII.GetBytes(duplicated));
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Mhtml.LoadAsync(duplicate));
    }

    private static string PartBase64(string wire, string mediaType)
    {
        var start = wire.IndexOf("Content-Type: " + mediaType, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start = wire.IndexOf("\r\n\r\n", start, StringComparison.Ordinal) + 4;
        var end = wire.IndexOf("\r\n--", start, StringComparison.Ordinal);
        return wire[start..end];
    }
}
