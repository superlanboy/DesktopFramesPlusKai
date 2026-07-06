using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Desktop_Frames
{
    /// <summary>
    /// The FIRST right-click of a session is jittery because two subsystems initialise lazily:
    ///   1. WPF's Popup/ContextMenu machinery — affects the icon-frame right-click menus.
    ///   2. The native shell context-menu handlers — affects the Portal Details view; the first
    ///      QueryContextMenu LoadLibrary's every registered shell extension into the process.
    /// This warms both shortly after startup (on idle) so the user's first real right-click is fast.
    /// Everything is best-effort and fully guarded — a failed warm just means the old lazy cost.
    /// </summary>
    public static class PreWarmManager
    {
        private static bool _done;

        /// <summary>Runs once. Call on the UI thread (the WPF warm needs it).</summary>
        public static void Run()
        {
            if (_done) return;
            _done = true;

            // (1) WPF popup subsystem — runs here on the UI thread.
            try { WarmWpfPopup(); } catch { }

            // (2) Shell handlers — heavy DLL loads; run off the UI thread on an STA worker so it
            //     never blocks the UI (that would just move the jitter to startup).
            var t = new Thread(WarmShellHandlers) { IsBackground = true, Name = "ShellMenuPrewarm" };
            try { t.SetApartmentState(ApartmentState.STA); } catch { }
            t.Start();
        }

        /// <summary>
        /// Open + immediately close a throwaway ContextMenu far off-screen so WPF builds the popup
        /// HWND and loads menu theme resources now, not on the first user right-click.
        /// </summary>
        private static void WarmWpfPopup()
        {
            var cm = new ContextMenu
            {
                Placement = PlacementMode.Absolute,
                HorizontalOffset = -32000,
                VerticalOffset = -32000
            };
            cm.Items.Add(new MenuItem { Header = "warm" });
            cm.Items.Add(new Separator());
            cm.Items.Add(new MenuItem { Header = "warm" });
            cm.Opened += (s, e) => cm.IsOpen = false; // close the instant it's realised
            cm.IsOpen = true;
        }

        private static void WarmShellHandlers()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(exe)) return;

                // Warm a file (covers "*" and per-extension handlers) and a folder (Directory/
                // Folder handlers) — together that's the bulk of what a Details view will show.
                ShellContextMenu.PreWarm(exe);
                string dir = System.IO.Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) ShellContextMenu.PreWarm(dir);
            }
            catch { }
        }
    }
}
