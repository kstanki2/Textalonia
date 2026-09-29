using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class LibreOfficeBinaryDocumentFormatTests
{
    private static string? Executable => Environment.GetEnvironmentVariable("TEXTALONIA_LIBREOFFICE_PATH") is { } path && File.Exists(path)
        ? path : null;

    [Fact]
    public void Binary_formats_require_an_explicit_available_provider()
    {
        Assert.Throws<NotSupportedException>(() => DocumentFormats.ForPath("legacy.doc"));
        Assert.Throws<NotSupportedException>(() => DocumentFormats.ForPath("legacy.dot"));
        Assert.Throws<FileNotFoundException>(() => new LibreOfficeBinaryDocumentFormat(new()
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "nonexistent-libreoffice-" + Guid.NewGuid().ToString("N"))
        }));
    }

    [Theory]
    [InlineData(BinaryWordFormat.Document, ".doc")]
    [InlineData(BinaryWordFormat.Template, ".dot")]
    public async Task Optional_provider_writes_real_binary_files_and_reports_unverified_fidelity(BinaryWordFormat kind, string extension)
    {
        if (Executable is not { } executable) return;
        var format = new LibreOfficeBinaryDocumentFormat(new() { ExecutablePath = executable, Format = kind });
        Assert.Equal(extension, Assert.Single(format.Extensions));
        var document = FlowDocument.FromText("Binary provider example");
        using var output = new MemoryStream();
        var saved = await format.SaveWithReportAsync(document, output);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "binary.provider-conversion-unverified");
        Assert.True(output.ToArray().AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }));
        Assert.True(output.CanWrite);

        output.Position = 0;
        var loaded = await format.LoadWithReportAsync(output);
        Assert.Equal(document.PlainText, loaded.Document.PlainText.TrimEnd());
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "binary.provider-conversion-unverified");
        Assert.True(output.CanRead);
    }

    [Fact]
    public async Task Strict_binary_export_rejects_before_writing_destination()
    {
        if (Executable is not { } executable) return;
        var format = new LibreOfficeBinaryDocumentFormat(new() { ExecutablePath = executable });
        using var destination = new MemoryStream(Encoding.UTF8.GetBytes("untouched"), writable: true);
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => format.SaveWithReportAsync(
            FlowDocument.FromText("example"), destination, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, d => d.Code == "binary.provider-conversion-unverified");
        Assert.Equal("untouched", Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task Binary_import_rejects_a_renamed_docx_package_without_running_the_provider()
    {
        if (Executable is not { } executable) return;
        var format = new LibreOfficeBinaryDocumentFormat(new() { ExecutablePath = executable });
        using var docx = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(FlowDocument.FromText("not binary"), docx);
        docx.Position = 0;
        await Assert.ThrowsAsync<FormatException>(() => format.LoadAsync(docx));
    }
}
