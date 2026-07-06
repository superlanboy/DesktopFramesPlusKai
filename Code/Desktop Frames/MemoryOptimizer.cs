using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Desktop_Frames
{
    /// <summary>
    /// Keeps the reported memory footprint low for this mostly-idle desktop utility by releasing
    /// managed heap and trimming the process working set back to the OS. Trimmed pages fault back
    /// in on demand, so this reduces the Task Manager number without harming correctness.
    /// </summary>
    public static class MemoryOptimizer
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minSize, IntPtr maxSize);

        private static DispatcherTimer _timer;

        /// <summary>Collect garbage and hand unused memory back to the OS.</summary>
        public static void Trim()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                // -1 / -1 tells Windows to trim the working set to the minimum.
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        /// <summary>
        /// Trim shortly after startup (once initial rendering settles) and then periodically,
        /// so idle memory stays low.
        /// </summary>
        public static void Start(int firstDelaySeconds = 4, int periodicSeconds = 180)
        {
            if (_timer != null) return;

            var startupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(firstDelaySeconds) };
            startupTimer.Tick += (s, e) =>
            {
                startupTimer.Stop();
                Trim();
            };
            startupTimer.Start();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(periodicSeconds) };
            _timer.Tick += (s, e) => Trim();
            _timer.Start();
        }
    }
}
