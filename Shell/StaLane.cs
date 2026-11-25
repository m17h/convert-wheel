using System.Collections.Concurrent;

namespace ConvertWheel.Shell;

/// <summary>
/// Runs calls into other processes one at a time on an STA thread. A call that takes too long is given up on
/// and its thread left behind for a fresh one. Only the newest waiting call runs; older ones are dropped.
/// </summary>
internal sealed class StaLane(string name) : IDisposable
{
    /// <summary>Stop asking once this many threads are stuck in a hung program; they return when it does.</summary>
    const int MaxStuck = 3;

    sealed record Work(Action Run, Action Cancel);

    sealed class Worker
    {
        public readonly BlockingCollection<Work> Queue = new(4);
        public bool Retired;
    }

    readonly object _gate = new();
    Worker? _worker;
    Work? _waiting;
    int _stuck;
    bool _disposed;

    /// <summary>Threads currently stuck in a call that was given up on.</summary>
    public int Stuck { get { lock (_gate) return _stuck; } }

    Worker StartWorker()
    {
        var worker = new Worker();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var work in worker.Queue.GetConsumingEnumerable())
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_waiting, work)) _waiting = null;
                        if (worker.Retired) { work.Cancel(); continue; }
                    }
                    work.Run();
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (worker.Retired && !_disposed) _stuck--;
                    worker.Queue.Dispose();
                }
            }
        })
        { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return worker;
    }

    /// <summary>The result, or null if it failed, was superseded, or took longer than <paramref name="timeout"/>.</summary>
    public async Task<T?> Invoke<T>(Func<T?> func, TimeSpan timeout) where T : class
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(() =>
        {
            if (tcs.Task.IsCompleted) return;
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { Log.Write(ex); tcs.TrySetResult(null); }
        }, () => tcs.TrySetResult(null));
        Worker worker;
        lock (_gate)
        {
            if (_disposed || _stuck >= MaxStuck) return null;
            worker = _worker ??= StartWorker();
            _waiting?.Cancel(); // Superseded requests need not enter Explorer at all.
            _waiting = work;
            if (!worker.Queue.TryAdd(work))
            {
                _waiting = null;
                work.Cancel();
            }
        }
        try { return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (!tcs.Task.IsCompleted && ReferenceEquals(_worker, worker))
                {
                    Retire(worker);
                    _stuck++;
                    _worker = null; // The next request gets a fresh STA, not the hung provider.
                }
            }
            work.Cancel();
            return null;
        }
    }

    void Retire(Worker worker)
    {
        worker.Retired = true;
        worker.Queue.CompleteAdding();
        while (worker.Queue.TryTake(out var work)) work.Cancel();
        _waiting = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_worker != null) Retire(_worker);
            _worker = null;
        }
    }
}
