using System.Text;
using Textalonia.Demo;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public sealed class DemoStorageTests
{
    [Fact]
    public async Task Conversion_failure_does_not_open_or_truncate_destination()
    {
        var opened = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => DocumentFileStorage.SaveAsync(
            FlowDocument.FromText("new content"), new FailingFormat(), () =>
            {
                opened = true;
                return Task.FromResult<Stream>(new MemoryStream());
            }));
        Assert.False(opened);
    }

    [Fact]
    public async Task Successful_save_replaces_old_bytes_and_closes_destination()
    {
        var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes("old content that is longer"));
        await DocumentFileStorage.SaveAsync(FlowDocument.FromText("new"), DocumentFormats.PlainText,
            () => Task.FromResult<Stream>(stream));
        Assert.Equal("new", Encoding.UTF8.GetString(stream.ToArray()));
        Assert.False(stream.CanWrite);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Flush_and_close_failures_are_reported_before_save_completes(bool failOnFlush)
    {
        var stream = new FailingStream(failOnFlush);
        await Assert.ThrowsAsync<IOException>(() => DocumentFileStorage.SaveAsync(
            FlowDocument.FromText("new"), DocumentFormats.Json, () => Task.FromResult<Stream>(stream)));
        Assert.True(stream.Closed);
    }

    private sealed class FailingFormat : TextDocumentFormat
    {
        public override string Name => "Failing format";
        public override IReadOnlyList<string> Extensions => [".fail"];
        public override FlowDocument Parse(string text) => throw new NotSupportedException();
        public override string Serialize(FlowDocument document) => throw new InvalidDataException("Conversion failed.");
    }

    private sealed class FailingStream(bool failOnFlush) : MemoryStream
    {
        public bool Closed { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken) => failOnFlush
            ? Task.FromException(new IOException("Flush failed.")) : Task.CompletedTask;
        public override ValueTask DisposeAsync()
        {
            Closed = true;
            base.Dispose();
            return failOnFlush ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("Close failed."));
        }
    }
}
