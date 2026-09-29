using System.Diagnostics;
using System.Text;
using Textalonia.Model;

namespace Textalonia.Serialization;

public enum BinaryWordFormat { Document, Template }

/// <summary>Settings for an explicit, out-of-process LibreOffice DOC or DOT conversion provider.</summary>
public sealed record LibreOfficeBinaryOptions
{
    public required string ExecutablePath { get; init; }
    public BinaryWordFormat Format { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public string? TemporaryDirectory { get; init; }
}

/// <summary>
/// Optional DOC/DOT interchange through a caller-installed LibreOffice executable.
/// Conversion fidelity is unverified and is always reported as a loss, so strict
/// conversion rejects the result. This format is never registered by DocumentFormats.
/// </summary>
public sealed class LibreOfficeBinaryDocumentFormat : IReportingDocumentFormat
{
    private static readonly byte[] CompoundSignature = [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];
    private readonly string _executablePath;
    private readonly string _temporaryDirectory;
    private readonly TimeSpan _timeout;
    private readonly BinaryWordFormat _format;

    public LibreOfficeBinaryDocumentFormat(LibreOfficeBinaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Format)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ExecutablePath)) throw new ArgumentException("A LibreOffice executable path is required.", nameof(options));
        _executablePath = Path.GetFullPath(options.ExecutablePath);
        if (!File.Exists(_executablePath)) throw new FileNotFoundException("LibreOffice conversion executable is unavailable.", _executablePath);
        _temporaryDirectory = Path.GetFullPath(options.TemporaryDirectory ?? Path.GetTempPath());
        _timeout = options.Timeout;
        _format = options.Format;
    }

    public string Name => _format == BinaryWordFormat.Template ? "Word 97-2003 template (LibreOffice)" : "Word 97-2003 document (LibreOffice)";
    public IReadOnlyList<string> Extensions => _format == BinaryWordFormat.Template ? [".dot"] : [".doc"];

    public async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        (await LoadWithReportAsync(stream, cancellationToken: cancellationToken)).Document;

    public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default) =>
        _ = await SaveWithReportAsync(document, stream, cancellationToken: cancellationToken);

    public async Task<DocumentLoadResult> LoadWithReportAsync(Stream stream, ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        var input = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        CheckCompoundFile(input);
        var directory = CreateOperationDirectory();
        try
        {
            var source = Path.Combine(directory, "source" + Extensions[0]);
            var output = Path.Combine(directory, "converted");
            Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(source, input, cancellationToken);
            await ConvertAsync(directory, source, output, "docx:Office Open XML Text", cancellationToken);
            var converted = Path.Combine(output, "source.docx");
            if (!File.Exists(converted)) throw new FormatException("LibreOffice did not produce a DOCX document.");
            await using var result = File.OpenRead(converted);
            var imported = await DocumentFormats.Docx.LoadWithReportAsync(result, options with { Mode = ConversionMode.Tolerant }, cancellationToken);
            var report = WithProviderNotice(imported.Report);
            RejectStrict(options, report);
            return new(imported.Document, report);
        }
        finally { DeleteOperationDirectory(directory); }
    }

    public async Task<DocumentSaveResult> SaveWithReportAsync(FlowDocument document, Stream stream,
        ConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        document.Validate();
        var directory = CreateOperationDirectory();
        try
        {
            var source = Path.Combine(directory, "source.docx");
            var output = Path.Combine(directory, "converted");
            Directory.CreateDirectory(output);
            DocumentSaveResult saved;
            await using (var docx = File.Create(source))
            {
                saved = await DocumentFormats.Docx.SaveWithReportAsync(document, docx,
                    options with { Mode = ConversionMode.Tolerant }, cancellationToken);
            }
            var filter = _format == BinaryWordFormat.Template ? "dot:MS Word 97 Vorlage" : "doc:MS Word 97";
            await ConvertAsync(directory, source, output, filter, cancellationToken);
            var converted = Path.Combine(output, "source" + Extensions[0]);
            if (!File.Exists(converted)) throw new FormatException("LibreOffice did not produce a binary Word file.");
            await using var binary = File.OpenRead(converted);
            var bytes = await DocumentFormats.ReadLimitedAsync(binary, cancellationToken);
            CheckCompoundFile(bytes);
            var report = WithProviderNotice(saved.Report);
            RejectStrict(options, report);
            await stream.WriteAsync(bytes, cancellationToken);
            return new(report);
        }
        finally { DeleteOperationDirectory(directory); }
    }

    private static ConversionReport WithProviderNotice(ConversionReport docxReport) =>
        new(docxReport.Diagnostics.Append(new ConversionDiagnostic("binary.provider-conversion-unverified",
            ConversionDiagnosticSeverity.Warning, "DOC/DOT binary conversion fidelity",
            "LibreOffice converted through DOCX; binary formatting, fields and embedded objects require external qualification.")));

    private static void RejectStrict(ConversionOptions options, ConversionReport report)
    {
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Mode == ConversionMode.Strict && report.HasLoss) throw new DocumentConversionException(report);
    }

    private static void CheckCompoundFile(byte[] data)
    {
        if (!data.AsSpan().StartsWith(CompoundSignature))
            throw new FormatException("DOC/DOT input must be an OLE compound file; renaming a DOCX package is unsupported.");
    }

    private string CreateOperationDirectory()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var directory = Path.GetFullPath(Path.Combine(_temporaryDirectory, "TextaloniaLibreOffice_" + Guid.NewGuid().ToString("N")));
        if (!IsChildOfTemporaryDirectory(directory)) throw new InvalidOperationException("Invalid temporary conversion directory.");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void DeleteOperationDirectory(string directory)
    {
        if (!IsChildOfTemporaryDirectory(directory)) throw new InvalidOperationException("Invalid temporary conversion directory.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private bool IsChildOfTemporaryDirectory(string path) =>
        path.StartsWith(_temporaryDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task ConvertAsync(string directory, string source, string output, string filter, CancellationToken cancellationToken)
    {
        var profile = Path.Combine(directory, "profile");
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add("-env:UserInstallation=" + new Uri(Path.GetFullPath(profile) + Path.DirectorySeparatorChar).AbsoluteUri);
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add("--nodefault");
        start.ArgumentList.Add("--norestore");
        start.ArgumentList.Add("--convert-to");
        start.ArgumentList.Add(filter);
        start.ArgumentList.Add("--outdir");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add(source);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("LibreOffice could not be started.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var stdout = ReadLimitedOutputAsync(process.StandardOutput, deadline.Token);
        var stderr = ReadLimitedOutputAsync(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("LibreOffice conversion exceeded its configured timeout.");
        }
        var outputText = await stdout;
        var errorText = await stderr;
        if (process.ExitCode != 0)
            throw new FormatException("LibreOffice conversion failed: " + Truncate(errorText.Length != 0 ? errorText : outputText));
    }

    private static async Task<string> ReadLimitedOutputAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) != 0)
            if (output.Length < 16384) output.Append(buffer, 0, Math.Min(count, 16384 - output.Length));
        return output.ToString();
    }

    private static string Truncate(string message) => message.Length <= 1024 ? message : message[..1024];
}
