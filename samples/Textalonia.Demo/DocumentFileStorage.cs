using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Demo;

internal static class DocumentFileStorage
{
    internal static async Task<DocumentSaveResult> SaveAsync(FlowDocument document, IDocumentFormat format,
        Func<Task<Stream>> openWriteAsync)
    {
        // Finish conversion before opening a destination that may replace an existing file.
        using var buffer = new MemoryStream();
        var result = await format.SaveWithReportAsync(document, buffer);
        buffer.Position = 0;
        await using (var stream = await openWriteAsync())
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
                stream.SetLength(0);
            }
            await buffer.CopyToAsync(stream);
            await stream.FlushAsync();
        }
        // The host may mark the snapshot saved only after flushing and closing succeed.
        return result;
    }
}
