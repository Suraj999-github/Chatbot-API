using System.Threading.Channels;

namespace AiApi.Web.Services.Jobs
{
    // Unbounded channel — jobs are queued and processed one at a time
    public sealed class DocumentIndexingQueue
    {
        private readonly Channel<IndexingWorkItem> _channel =
            Channel.CreateUnbounded<IndexingWorkItem>(
                new UnboundedChannelOptions { SingleReader = true });

        public ValueTask EnqueueAsync(IndexingWorkItem item) =>
            _channel.Writer.WriteAsync(item);

        public IAsyncEnumerable<IndexingWorkItem> ReadAllAsync(CancellationToken ct) =>
            _channel.Reader.ReadAllAsync(ct);
    }
    public record IndexingWorkItem(
    string JobId,
    string TempFilePath,
    string FileName,
    Dictionary<string, string> Metadata);
}
