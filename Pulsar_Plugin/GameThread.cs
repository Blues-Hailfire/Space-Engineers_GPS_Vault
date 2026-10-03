using System;
using System.Threading.Tasks;
using Sandbox.ModAPI;

namespace GpsSyncPlugin
{
    /// <summary>
    /// Bridges a background (Task.Run) thread onto the game thread for a single
    /// synchronous read/write, then back. Needed because ModAPI collections
    /// (GPS list, voxel maps, HUD markers) are mutated by the simulation thread
    /// and aren't safe to walk from a background thread directly.
    /// </summary>
    public static class GameThread
    {
        private const int DefaultTimeoutMs = 5000;

        /// <summary>
        /// Runs <paramref name="action"/> on the game thread and returns its result.
        /// Throws TimeoutException if the game thread doesn't pick it up within
        /// <paramref name="timeoutMs"/> (e.g. session tearing down), so a caller
        /// awaiting this can never hang forever.
        /// </summary>
        public static async Task<T> RunAsync<T>(Func<T> action, int timeoutMs = DefaultTimeoutMs)
        {
            var tcs = new TaskCompletionSource<T>();
            MyAPIGateway.Utilities?.InvokeOnGameThread(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            });

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed != tcs.Task)
                throw new TimeoutException("Game thread hop timed out.");
            return await tcs.Task;
        }
    }
}
