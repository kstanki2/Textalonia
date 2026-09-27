using System.Globalization;
using Textalonia.Controls;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

internal static class MailMergeExample
{
    internal static async Task VerifyAsync(TextaloniaEditor editor)
    {
        editor.Document = FlowDocument.FromText("Dear ");
        editor.Session.Select(5, 5);
        editor.InsertMergeField("Name");
        editor.Session.InsertText(", total ");
        editor.InsertMergeField("Balance", "N2");
        var template = editor.Document;
        var record = new Dictionary<string, object?> { ["Name"] = "Ada", ["Balance"] = 1234.5m };
        var options = new MailMergeOptions { Culture = CultureInfo.GetCultureInfo("en-US") };
        var preview = MailMergeProcessor.Preview(template, record, options);
        var merged = MailMergeProcessor.Merge(template, record, options);
        if (preview.PlainText != "Dear Ada, total 1,234.50" || merged.PlainText != preview.PlainText ||
            merged.Text.Contains('\uFFFC') || MailMergeProcessor.GetFieldNames(preview).Length != 2 ||
            !template.PlainText.Contains("«Name»"))
            throw new InvalidOperationException("Packaged mail-merge preview/generation contract failed.");
        var batch = MailMergeProcessor.MergeMany(template, new[] { record, record }, options).ToArray();
        if (batch.Length != 2 || batch[0].PlainText != merged.PlainText)
            throw new InvalidOperationException("Packaged batch mail merge failed.");
        var native = DocumentFormats.Json.Serialize(template);
        if (DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(native)) != native)
            throw new InvalidOperationException("Packaged field persistence failed.");
        using var output = new MemoryStream();
        await DocumentFormats.Docx.SaveWithReportAsync(merged, output, new() { Mode = ConversionMode.Strict });
        output.Position = 0;
        if ((await DocumentFormats.Docx.LoadWithReportAsync(output, new() { Mode = ConversionMode.Strict })).Document.PlainText != merged.PlainText)
            throw new InvalidOperationException("Packaged merged DOCX export failed.");
    }
}
