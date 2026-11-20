using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Services;

/// <summary>
/// Two queues served by one worker: a pending high-priority item (an alignment someone
/// is waiting for) is always taken before any low-priority item (background analysis).
/// </summary>
/// <typeparam name="THigh">High-priority item type.</typeparam>
/// <typeparam name="TLow">Low-priority item type.</typeparam>
public sealed class PriorityWorkQueue<THigh, TLow>
    where THigh : class
    where TLow : class
{
    // Wait mode: TryWrite returns false when full, so callers can log the drop.
    private readonly Channel<THigh> _high = Channel.CreateBounded<THigh>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

    private readonly Channel<TLow> _low = Channel.CreateUnbounded<TLow>();

    private readonly Func<TLow, string>? _lowKey;

    // Keys of low-priority items waiting in the queue, so the same video isn't queued twice.
    private readonly ConcurrentDictionary<string, byte> _pendingLow = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PriorityWorkQueue{THigh, TLow}"/> class.
    /// </summary>
    /// <param name="lowKey">Optional key for low-priority items; an item whose key is already
    /// waiting is rejected.</param>
    public PriorityWorkQueue(Func<TLow, string>? lowKey = null)
    {
        _lowKey = lowKey;
    }

    /// <summary>Gets the number of low-priority items waiting.</summary>
    public int LowCount => _low.Reader.Count;

    /// <summary>Queues a high-priority item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>False if the queue is full or completed.</returns>
    public bool TryEnqueueHigh(THigh item) => _high.Writer.TryWrite(item);

    /// <summary>Queues a low-priority item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>False if the queue is completed or an item with the same key is already waiting.</returns>
    public bool TryEnqueueLow(TLow item)
    {
        if (_lowKey is not null && !_pendingLow.TryAdd(_lowKey(item), 0))
        {
            return false;
        }

        if (_low.Writer.TryWrite(item))
        {
            return true;
        }

        if (_lowKey is not null)
        {
            _pendingLow.TryRemove(_lowKey(item), out _);
        }

        return false;
    }

    /// <summary>Stops accepting items; a waiting <see cref="DequeueAsync"/> ends with <see cref="ChannelClosedException"/>.</summary>
    public void Complete()
    {
        _high.Writer.TryComplete();
        _low.Writer.TryComplete();
    }

    /// <summary>
    /// Waits for the next item, high priority first. Exactly one of the pair is non-null.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The next item.</returns>
    public async ValueTask<(THigh? High, TLow? Low)> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_high.Reader.TryRead(out var high))
            {
                return (high, null);
            }

            if (_low.Reader.TryRead(out var low))
            {
                if (_lowKey is not null)
                {
                    _pendingLow.TryRemove(_lowKey(low), out _);
                }

                return (null, low);
            }

            // Each wait gets its own token, cancelled once either channel wakes us, so the
            // other channel's unused waiter is released instead of piling up.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var highReady = _high.Reader.WaitToReadAsync(waitCts.Token).AsTask();
            var lowReady = _low.Reader.WaitToReadAsync(waitCts.Token).AsTask();
            await Task.WhenAny(highReady, lowReady).ConfigureAwait(false);
            await waitCts.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (highReady.IsCompletedSuccessfully && !highReady.Result
                && lowReady.IsCompletedSuccessfully && !lowReady.Result)
            {
                throw new ChannelClosedException();
            }
        }
    }
}
