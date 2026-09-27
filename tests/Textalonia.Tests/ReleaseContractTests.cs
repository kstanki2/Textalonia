using System.Text;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ReleaseContractTests
{
    [Theory]
    [InlineData("native-basic.json")]
    [InlineData("native-rich.json")]
    [InlineData("Interchange/native-inline.json")]
    public void Native_fixtures_round_trip_and_keep_history_and_resources(string path)
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", path));
        var original = DocumentFormats.Json.Parse(fixture);
        var current = DocumentFormats.Json.Serialize(original);
        Assert.Contains("\"version\": 5", current);
        Assert.Equal(current, DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(current)));
        var session = new EditorSession(original);
        session.Select(session.Index.Length, 0);
        var selection = session.Selection;
        session.InsertText("replacement");
        session.Undo();
        Assert.Equal(selection, session.Selection);
        Assert.Equal(current, DocumentFormats.Json.Serialize(session.Document));
        Assert.Equal(original.Resources, session.Document.Resources);
    }

    [Theory]
    [InlineData(".textalonia")]
    [InlineData(".txt")]
    [InlineData(".html")]
    [InlineData(".rtf")]
    [InlineData(".docx")]
    [InlineData(".txaml")]
    [InlineData(".md")]
    public async Task Every_codec_propagates_partial_IO_failure_without_disposing_destination(string extension)
    {
        var format = DocumentFormats.ForPath("example" + extension);
        foreach (var reporting in new[] { false, true })
        {
            using var stream = new PartialFailureStream();
            await Assert.ThrowsAsync<IOException>(() => reporting
                ? format.SaveWithReportAsync(FlowDocument.FromText("example"), stream)
                : format.SaveAsync(FlowDocument.FromText("example"), stream));
            Assert.False(stream.Disposed);
            Assert.Equal(2, stream.Length);
        }
    }

    private sealed class PartialFailureStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, Math.Min(2, count));
            throw new IOException("Injected destination failure.");
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var copy = buffer[..Math.Min(2, buffer.Length)].ToArray();
            base.Write(copy, 0, copy.Length);
            throw new IOException("Injected destination failure.");
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
