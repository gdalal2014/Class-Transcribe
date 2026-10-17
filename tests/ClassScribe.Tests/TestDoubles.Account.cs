using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>
/// The reader of the account's credits as a test plays it: it answers each read with the next reading queued, and repeats
/// the last once the queue is empty (nothing asked, until a test queues something); it counts the reads, tells the test
/// of each as it begins, can hold a read until the test lets it go, and can fail in the way the real reader never does.
/// A read made with a cancelled token is a cancellation, as the real reader's is.
/// </summary>
public sealed class FakeElevenLabsAccount : IElevenLabsAccount
{
    private readonly Lock _gate = new();
    private readonly Queue<AccountReading> _queued = new();
    private AccountReading _last = AccountReading.Off;
    private int _reads;

    /// <summary>How many reads have begun.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>When set, every read throws it once it has begun.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, a read waits for it before it answers, as a slow line would make it.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Called as each read begins, with the read's number from 1, so a test can look at what had happened by then.</summary>
    public Action<int>? OnRead { get; set; }

    /// <summary>Queues <paramref name="readings"/> as the answers to the next reads, in order. Returns this, so that a test queues in one line.</summary>
    public FakeElevenLabsAccount Answer(params AccountReading[] readings)
    {
        lock (_gate)
        {
            foreach (var reading in readings) _queued.Enqueue(reading);
        }

        return this;
    }

    /// <summary>A reading that succeeded: <paramref name="used"/> credits of <paramref name="limit"/> this period, with the reset and the overage when given.</summary>
    public static AccountReading Read(long used, long limit = 100_000, DateTimeOffset? reset = null, decimal? overage = null, string? currency = null) =>
        new(AccountReadingKind.Read, used, limit, reset, overage, currency);

    /// <inheritdoc />
    public async Task<AccountReading> ReadAsync(CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _reads);
        OnRead?.Invoke(number);
        if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is { } failure) throw failure;

        lock (_gate) return _queued.Count > 0 ? _last = _queued.Dequeue() : _last;
    }
}
