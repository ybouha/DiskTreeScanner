using System.Runtime.ExceptionServices;

namespace DiskTreeScanner.Core.Fallback;

/// <summary>
/// Bounded pool of dedicated threads (directory listing blocks on I/O) with one work deque per
/// thread: a thread pops its own newest folder (depth first, good locality) and, when idle, steals
/// the oldest folder of another thread (shallow folders carry the most work).
/// </summary>
internal sealed class WorkStealingScheduler
{
    private readonly WorkDeque[] _queues;
    private readonly SemaphoreSlim _wake = new(0);
    private int _pending;
    private int _idle;
    private volatile bool _finished;
    private ExceptionDispatchInfo? _error;

    public WorkStealingScheduler(int threads)
    {
        _queues = new WorkDeque[Math.Max(1, threads)];
        for (int i = 0; i < _queues.Length; i++)
            _queues[i] = new WorkDeque();
    }

    public int ThreadCount => _queues.Length;

    /// <summary>Processes <paramref name="initial"/> and everything pushed meanwhile; returns when all is done.</summary>
    public void Run(int initial, Action<int, int> process, CancellationToken cancellationToken)
    {
        _pending = 1;
        _queues[0].Push(initial);
        var threads = new Thread[_queues.Length];
        for (int i = 0; i < threads.Length; i++)
        {
            int index = i;
            threads[i] = new Thread(() => Loop(index, process, cancellationToken))
            {
                IsBackground = true,
                Name = $"DiskTreeScanner directory scan {index}",
            };
            threads[i].Start();
        }
        foreach (Thread thread in threads)
            thread.Join();
        _error?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Queues an item on the calling worker's deque.</summary>
    public void Push(int worker, int item)
    {
        Interlocked.Increment(ref _pending);
        _queues[worker].Push(item);
        if (Volatile.Read(ref _idle) > 0)
            _wake.Release();
    }

    private void Loop(int worker, Action<int, int> process, CancellationToken cancellationToken)
    {
        while (!_finished)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Finish();
                return;
            }
            if (TryTake(worker, out int item))
            {
                try
                {
                    process(worker, item);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _error, ExceptionDispatchInfo.Capture(ex), null);
                    Finish();
                    return;
                }
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    Finish();
                    return;
                }
                continue;
            }
            Interlocked.Increment(ref _idle);
            _wake.Wait(5);
            Interlocked.Decrement(ref _idle);
        }
    }

    private bool TryTake(int worker, out int item)
    {
        if (_queues[worker].TryPop(out item))
            return true;
        for (int k = 1; k < _queues.Length; k++)
        {
            if (_queues[(worker + k) % _queues.Length].TrySteal(out item))
                return true;
        }
        return false;
    }

    private void Finish()
    {
        _finished = true;
        _wake.Release(_queues.Length);
    }

    private sealed class WorkDeque
    {
        private int[] _items = new int[64];
        private int _head;
        private int _tail;

        public void Push(int item)
        {
            lock (this)
            {
                if (_tail == _items.Length)
                {
                    int live = _tail - _head;
                    if (live < _items.Length / 2)
                    {
                        Array.Copy(_items, _head, _items, 0, live);
                    }
                    else
                    {
                        var bigger = new int[_items.Length * 2];
                        Array.Copy(_items, _head, bigger, 0, live);
                        _items = bigger;
                    }
                    _head = 0;
                    _tail = live;
                }
                _items[_tail++] = item;
            }
        }

        public bool TryPop(out int item)
        {
            lock (this)
            {
                if (_tail > _head)
                {
                    item = _items[--_tail];
                    if (_tail == _head)
                        _head = _tail = 0;
                    return true;
                }
            }
            item = 0;
            return false;
        }

        public bool TrySteal(out int item)
        {
            lock (this)
            {
                if (_tail > _head)
                {
                    item = _items[_head++];
                    if (_tail == _head)
                        _head = _tail = 0;
                    return true;
                }
            }
            item = 0;
            return false;
        }
    }
}
