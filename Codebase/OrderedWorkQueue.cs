using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Codebase;

// Only captured, detached data belongs in work. Completions run on the caller's thread.
internal sealed class OrderedWorkQueue
{
    private readonly object _sync = new object();
    private readonly Queue<Action> _work = new Queue<Action>();
    private readonly Queue<Action> _completed = new Queue<Action>();
    private bool _running;

    internal void Enqueue(Action work, Action<Exception> completed)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        lock (_sync)
        {
            _work.Enqueue(() => {
                Exception error = null;
                CultureInfo previous = CultureInfo.CurrentCulture;
                try { CultureInfo.CurrentCulture = culture; work(); }
                catch (Exception ex) { error = ex; }
                finally { CultureInfo.CurrentCulture = previous; }
                if (completed != null)
                    lock (_sync) _completed.Enqueue(() => completed(error));
            });
            if (_running) return;
            _running = true;
            ThreadPool.QueueUserWorkItem(_ => Run());
        }
    }

    private void Run()
    {
        while (true)
        {
            Action next;
            lock (_sync)
            {
                if (_work.Count == 0) { _running = false; Monitor.PulseAll(_sync); return; }
                next = _work.Dequeue();
            }
            next();
        }
    }

    internal void PumpCompletions()
    {
        while (true)
        {
            Action completed;
            lock (_sync)
            {
                if (_completed.Count == 0) return;
                completed = _completed.Dequeue();
            }
            completed();
        }
    }

    internal void Drain()
    {
        lock (_sync) while (_running) Monitor.Wait(_sync);
        PumpCompletions();
    }
}
