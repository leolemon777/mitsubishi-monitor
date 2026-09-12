using System;
using System.Threading;
using System.Threading.Tasks;

namespace MitsubishiMonitor.Demo.Services
{
    internal static class DatabaseStartupRecovery
    {
        internal static async Task RunAsync(Func<CancellationToken, Task> initialize,
            Action ready, Action<Exception> unavailable, CancellationToken cancellationToken,
            Func<int, TimeSpan> retryDelay = null)
        {
            var attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await initialize(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    ready();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    unavailable(ex);
                }
                attempt = Math.Min(attempt + 1, 30);
                var delay = retryDelay?.Invoke(attempt) ?? TimeSpan.FromSeconds(
                    Math.Min(60, 3 * (1 << Math.Min(attempt - 1, 5))));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
