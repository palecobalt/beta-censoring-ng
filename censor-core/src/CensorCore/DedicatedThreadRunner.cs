using System.Collections.Concurrent;

namespace CensorCore {
    /// <summary>
    /// Runs work on a fixed set of dedicated threads.
    /// </summary>
    /// <remarks>
    /// Used for GPU inference: the CUDA execution provider keeps per-thread state (including GPU memory) for
    /// every thread that runs a session, so running on thread pool threads makes GPU memory grow with the pool.
    /// </remarks>
    internal sealed class DedicatedThreadRunner {
        private readonly BlockingCollection<Action> _work = new();

        public DedicatedThreadRunner(int threadCount, string name) {
            for (var i = 0; i < threadCount; i++) {
                new Thread(() => {
                    foreach (var action in _work.GetConsumingEnumerable()) {
                        action();
                    }
                }) { IsBackground = true, Name = $"{name}-{i}" }.Start();
            }
        }

        public Task<T> Run<T>(Func<T> func) {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() => {
                try {
                    completion.SetResult(func());
                } catch (Exception e) {
                    completion.SetException(e);
                }
            });
            return completion.Task;
        }
    }
}
