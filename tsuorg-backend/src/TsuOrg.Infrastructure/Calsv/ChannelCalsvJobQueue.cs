using System.Threading.Channels;
using TsuOrg.Application.Common;

namespace TsuOrg.Infrastructure.Calsv;

/// <summary>
/// In-process unbounded channel queue. Swap for Hangfire/Azure Queue in production.
/// </summary>
public sealed class ChannelCalsvJobQueue : ICalsvJobQueue
{
    private readonly Channel<CalsvJob> _channel =
        Channel.CreateUnbounded<CalsvJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ValueTask EnqueueAsync(CalsvJob job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);

    public async IAsyncEnumerable<CalsvJob> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(ct))
            yield return job;
    }
}
