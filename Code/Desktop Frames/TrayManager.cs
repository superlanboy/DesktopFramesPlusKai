using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using WinFormsMouseEventArgs = System.Windows.Forms.MouseEventArgs;


namespace Desktop_Frames
{
    public class TrayManager : IDisposable
    {
        private NotifyIcon _trayIcon;
      
        private bool _disposed;
        public static bool IsStartWithWindows { get; private set; }

        private const string RUN_KEY_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "Desktop Frames +"; // --- FIX: Ensures new registry entries use the correct name ---

        private static readonly List<HiddenFrame> HiddenFrames = new List<HiddenFrame>();
    
        private ToolStripMenuItem _showHiddenFramesItem;

        private ToolStripMenuItem _profilesMenuItem;
        public static TrayManager Instance { get; private set; } // Singleton instance

        private bool _areFramesTempHidden = false;
    
        private List<NonActivatingWindow> _tempHiddenFrames = new List<NonActivatingWindow>();

        private bool Showintray = SettingsManager.ShowInTray;

        private const int WM_NCLBUTTONDOWN = 0xA1;

        private const int HT_CAPTION = 0x2;

        private ToolStripMenuItem _automationMenuItem; //
        private ToolStripMenuItem _autoOrganizeMenuItem; // NEW

        private class HiddenFrame
        {
            public string Title { get; set; }
            public NonActivatingWindow Window { get; set; }
        }

        public void UpdateAutomationMenuCheck(bool isChecked)
        {
            if (_automationMenuItem != null)
            {
                // This prevents infinite loops by checking the value first
                if (_automationMenuItem.Checked != isChecked)
                {
                    _automationMenuItem.Checked = isChecked;
                }
            }
        }

        public void UpdateAutoOrganizeMenuCheck(bool isChecked)
        {
            if (_autoOrganizeMenuItem != null)
            {
                if (_autoOrganizeMenuItem.Checked != isChecked)
                {
                    _autoOrganizeMenuItem.Checked = isChecked;
                }
            }
        }

        public TrayManager()
        {
            // 1. AUTO-MIGRATION: Check if we need to move from Shortcut to Registry
            PerformStartupMigration();
           
            // 2. Check status using the NEW logic (Registry check + Shortcut fallback)
            IsStartWithWindows = CheckIfStartWithWindowsEnabled();


            // 3. Start Remote Info System (Runs 25s later)
            // DISABLED (fork): this phoned home to the UPSTREAM repo to check for a newer version,
            // which is irrelevant for this fork. Skipping avoids a startup network call.
            // RemoteInfoManager.Initialize();

            Instance = this; // Set singleton instance
        }

        private void OnTrayIconDoubleClick(object sender, EventArgs e)
        {
			// 1. If the frame are officially hidden (either by timer or tray), wake them up!
			if (Framemanager._areFramesAutoHidden)
            {
                Framemanager.WakeUpFrames();
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, "Tray Double-Click: Woke up frames.");
            }
            // 2. Otherwise, they are visible, so force them into the official hidden state.
            else
            {
                Framemanager.ForceHideFrames();
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, "Tray Double-Click: Forced frames to hide.");
            }

            UpdateTrayIcon();
        }


        /// <summary>
        /// Handles single click on tray icon - checks for special key combination CTRL+ALT+SHIFT
        /// </summary>
        private void OnTrayIconClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            try
            {
                // Only handle left click
                if (e.Button != MouseButtons.Left) return;

                // Check if CTRL+ALT+SHIFT are all pressed
                bool isCtrlPressed = (System.Windows.Forms.Control.ModifierKeys & Keys.Control) == Keys.Control;
                bool isAltPressed = (System.Windows.Forms.Control.ModifierKeys & Keys.Alt) == Keys.Alt;
                bool isShiftPressed = (System.Windows.Forms.Control.ModifierKeys & Keys.Shift) == Keys.Shift;

                if (isCtrlPressed && isAltPressed && isShiftPressed)
                {
                    LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.General,
                        "TrayIcon: CTRL+ALT+SHIFT+Click detected - Exporting registry values");

                    // Execute the registry export function
                    bool success = RegistryHelper.ExportProgramManagementValues();

                    if (success)
                    {
                        // Show notification that export was successful
                        _trayIcon.BalloonTipTitle = "Desktop Frames Plus";
                        _trayIcon.BalloonTipText = "Registry values exported successfully to program folder.";
                        _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                        _trayIcon.ShowBalloonTip(3000); // Show for 3 seconds

                        LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.General,
                            "TrayIcon: Registry values export completed successfully");
                    }
                    else
                    {
                        // Show error notification
                        _trayIcon.BalloonTipTitle = "Desktop Frames Plus - Error";
                        _trayIcon.BalloonTipText = "Failed to export registry values. Check log for details.";
                        _trayIcon.BalloonTipIcon = ToolTipIcon.Error;
                        _trayIcon.ShowBalloonTip(3000);

                        LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General,
                            "TrayIcon: Registry values export failed");
                    }
                }
                else
                {
                    // Log debug info about key states (only if at least one modifier is pressed)
                    if (isCtrlPressed || isAltPressed || isShiftPressed)
                    {
                        LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI,
                            $"TrayIcon: Single click with modifiers - Ctrl:{isCtrlPressed}, Alt:{isAltPressed}, Shift:{isShiftPressed}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General,
                    $"TrayIcon: Error in click handler: {ex.Message}");
            }
        }

        private string GetFocusFrameHotkeyString()
        {
            try
            {
                string mod = SettingsManager.FocusFrameModifier ?? "";
                int key = SettingsManager.FocusFrameKey;

                if (string.IsNullOrWhiteSpace(mod) && key == 0) return "Not Set";

                List<string> parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(mod))
                {
                    // Clean up the string to match standard UI format
                    string formattedMod = mod.Replace("Control", "Ctrl").Replace(", ", "+");
                    parts.Add(formattedMod);
                }

                if (key != 0)
                {
                    // FIX: Use System.Windows.Forms.Keys because it perfectly maps to Win32 Virtual Key codes
                    string keyStr = ((System.Windows.Forms.Keys)key).ToString();

                    // Clean up default enum names (converts "D1" to "1")
                    if (keyStr.StartsWith("D") && keyStr.Length == 2 && char.IsDigit(keyStr[1]))
                        keyStr = keyStr.Substring(1);

                    parts.Add(keyStr);
                }

                return string.Join("+", parts);
            }
            catch
            {
                return "Ctrl+Alt+Z"; // Safe fallback
            }
        }

        public void InitializeTray()
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;

            // Dispose old icon if re-initializing to prevent ghosting
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }

            _trayIcon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(exePath),
                Visible = true,
                Text = $"Desktop Frames ({ProfileManager.CurrentProfileName})"
            };

            _trayIcon.DoubleClick += OnTrayIconDoubleClick;
            _trayIcon.MouseClick += OnTrayIconClick;

            // Explicitly detach and clear any existing context menu to prevent duplication
            if (_trayIcon.ContextMenuStrip != null)
            {
                var oldMenu = _trayIcon.ContextMenuStrip;
                _trayIcon.ContextMenuStrip = null;
                oldMenu.Dispose();
            }

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("About...", null, (s, e) => AboutFormManager.ShowAboutForm());
            trayMenu.Items.Add("Options...", null, (s, e) => OptionsFormManager.ShowOptionsForm());
            trayMenu.Items.Add(new ToolStripSeparator());

            // Profiles Submenu
            _profilesMenuItem = new ToolStripMenuItem("Profiles");
            trayMenu.Items.Add(_profilesMenuItem);

            // Standalone Automation Toggle with explicit Save
            _automationMenuItem = new ToolStripMenuItem("Enable Profile Automation") { CheckOnClick = true };
            _automationMenuItem.Checked = SettingsManager.EnableProfileAutomation;
            _automationMenuItem.Click += (s, e) => {
                SettingsManager.EnableProfileAutomation = _automationMenuItem.Checked;
                try { SettingsManager.SaveSettings(); } catch { }
                if (SettingsManager.EnableProfileAutomation) AutomationManager.Start();
            };
            trayMenu.Items.Add(_automationMenuItem);

            trayMenu.Items.Add(new ToolStripSeparator());

            // --- SMART DESKTOP OPTIONS ---
            trayMenu.Items.Add("Smart Desktop Rules...", null, (s, e) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    new AutoOrganizeForm().ShowDialog();
                }));
            });

            _autoOrganizeMenuItem = new ToolStripMenuItem("Enable Auto-Organize") { CheckOnClick = true };
            _autoOrganizeMenuItem.Checked = SettingsManager.EnableAutoOrganize;
            _autoOrganizeMenuItem.Click += (s, e) =>
            {
                SettingsManager.EnableAutoOrganize = _autoOrganizeMenuItem.Checked;
                try { SettingsManager.SaveSettings(); } catch { }

                if (SettingsManager.EnableAutoOrganize)
                    AutoOrganizeManager.Start();
                else
                    AutoOrganizeManager.Stop();
            };
            trayMenu.Items.Add(_autoOrganizeMenuItem);

            trayMenu.Items.Add(new ToolStripSeparator());
            // --- END SMART DESKTOP OPTIONS ---

            trayMenu.Items.Add("Reload All Frames", null, async (s, e) => { await reloadallFrames(); });

            trayMenu.Items.Add(new ToolStripSeparator());

            _showHiddenFramesItem = new ToolStripMenuItem("Show Hidden Frames") { Enabled = false };
            trayMenu.Items.Add(_showHiddenFramesItem);

            var focusFrameItem = (ToolStripMenuItem)trayMenu.Items.Add($"Focus Frame... ({GetFocusFrameHotkeyString()})", null, (s, e) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    FrameFocusFormManager focusManager = new FrameFocusFormManager();
                    focusManager.ShowDialog();
                }));
            });

            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());

            // Reflect the current settings each time the menu opens (no rebuild/restart needed):
            // hide "Focus Frame..." when the focus-frame feature is disabled, and refresh its hotkey label.
            trayMenu.Opening += (s, e) =>
            {
                focusFrameItem.Visible = SettingsManager.EnableFocusFrameHotkey;
                if (focusFrameItem.Visible) focusFrameItem.Text = $"Focus Frame... ({GetFocusFrameHotkeyString()})";
            };

            _trayIcon.ContextMenuStrip = trayMenu;

            UpdateProfilesMenu();
            UpdateHiddenFramesMenu();
            UpdateTrayIcon();
        }


        public static async Task reloadallFrames()
        {
            var waitWindow = new System.Windows.Window
            {
                Title = "Desktop Frames +",
                Width = 300,
                Height = 150,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                WindowStyle = System.Windows.WindowStyle.None,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250)),
                AllowsTransparency = true,
                Topmost = true
            };

            var mainBorder = new System.Windows.Controls.Border
            {
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new System.Windows.CornerRadius(8),
                Margin = new System.Windows.Thickness(8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    Direction = 270,
                    ShadowDepth = 4,
                    Opacity = 0.15,
                    BlurRadius = 8
                }
            };

            var waitStack = new System.Windows.Controls.StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Orientation = System.Windows.Controls.Orientation.Vertical
            };

            var titleText = new System.Windows.Controls.TextBlock
            {
                Text = "Desktop Frames +",
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 16,
                FontWeight = System.Windows.FontWeights.Medium,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 33, 36)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 10)
            };
            waitStack.Children.Add(titleText);

            var logoImage = new System.Windows.Controls.Image
            {
                Width = 32,
                Height = 32,
                Margin = new System.Windows.Thickness(0, 0, 0, 10),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            // FIX 1: Properly dispose the extracted GDI Icon to prevent memory leaks
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceStream = assembly.GetManifestResourceStream("Desktop_Frames.Resources.logo1.png");
                if (resourceStream != null)
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = resourceStream;
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // Important for stream closing
                    bitmap.EndInit();
                    bitmap.Freeze(); // Make it efficient
                    logoImage.Source = bitmap;
                    resourceStream.Dispose(); // Close stream
                }
                else
                {
                    string exePath = Assembly.GetEntryAssembly().Location;
                    using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (icon != null)
                            logoImage.Source = icon.ToImageSource();
                    }
                }
            }
            catch
            {
                // Fallback
                try
                {
                    string exePath = Assembly.GetEntryAssembly().Location;
                    using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (icon != null) logoImage.Source = icon.ToImageSource();
                    }
                }
                catch { }
            }
            waitStack.Children.Add(logoImage);

            var waitText = new System.Windows.Controls.TextBlock
            {
                Text = "Reloading all frames, please wait...",
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(95, 99, 104)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            waitStack.Children.Add(waitText);

            mainBorder.Child = waitStack;
            waitWindow.Content = mainBorder;
            waitWindow.Show();

            try
            {
                await Task.Run(async () =>
                {
                    // Allow UI to render the wait window
                    await Task.Delay(100);

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // 1. Close all windows
                        var windows = System.Windows.Application.Current.Windows.OfType<NonActivatingWindow>().ToList();
                        foreach (var frame in windows)
                        {
                            frame.Close();
                        }

                        // 2. Reload Logic (This calls Framemanager)
                        Framemanager.ReloadFrames();

                        // FIX 2: Force Garbage Collection
                        // Since we just closed heavy WPF windows, we force a collection to release 
                        // the memory immediately before loading new ones.
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBoxesManager.ShowOKOnlyMessageBoxFormStatic($"An error occurred while reloading frames: {ex.Message}", "Error");
            }
            finally
            {
                waitWindow.Close();
                // Ensure the wait window itself is collected
                waitWindow = null;
                GC.Collect();
            }
        }


        public static void AddHiddenFrame(NonActivatingWindow frame)
        {
            if (frame == null || string.IsNullOrEmpty(frame.Title)) return;
			frame.Dispatcher.Invoke(() =>
            {
				frame.Visibility = Visibility.Hidden;
            });

            if (!HiddenFrames.Any(f => f.Title == frame.Title))
            {
                HiddenFrames.Add(new HiddenFrame { Title = frame.Title, Window = frame });
				frame.Visibility = System.Windows.Visibility.Hidden;
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, $"Added frame '{frame.Title}' to hidden list");
                Instance?.UpdateHiddenFramesMenu();
                Instance?.UpdateTrayIcon();
            }
        }

        public static void ShowHiddenFrame(string title)
        {
            var HiddenFrame = HiddenFrames.FirstOrDefault(f => f.Title == title);
            if (HiddenFrame == null) return;

            try
            {
                var w = HiddenFrame.Window;
                if (w == null)
                {
                    // Stale entry (frame was deleted) — just drop it, don't try to show it.
                    LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, $"Hidden frame '{title}' no longer exists; removing stale entry.");
                    return;
                }

                w.Dispatcher.Invoke(() =>
                {
                    w.Visibility = Visibility.Visible;
                    w.Activate();
                    w.Show();
                });

                var FrameData = Framemanager.GetFrameData().FirstOrDefault(f => f.Title == title);
                if (FrameData != null)
                {
                    Framemanager.UpdateFrameProperty(FrameData, "IsHidden", "false", $"Showed frame '{title}'");
                }
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, $"Showed frame '{title}'");
            }
            catch (Exception ex)
            {
                // e.g. the window was closed/deleted — remove the stale entry instead of crashing.
                LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"ShowHiddenFrame '{title}' failed; removing stale entry: {ex.Message}");
            }
            finally
            {
                HiddenFrames.Remove(HiddenFrame);
                Instance?.UpdateHiddenFramesMenu();
                Instance?.UpdateTrayIcon();
            }
        }

        /// <summary>Removes a frame from the hidden list (called when a frame is deleted) so it
        /// doesn't linger in the "Show Hidden Frames" menu.</summary>
        public static void RemoveHiddenFrame(NonActivatingWindow win, string title = null)
        {
            int removed = HiddenFrames.RemoveAll(f => (win != null && f.Window == win) || (title != null && f.Title == title));
            if (removed > 0)
            {
                Instance?.UpdateHiddenFramesMenu();
                Instance?.UpdateTrayIcon();
            }
        }

        public void UpdateHiddenFramesMenu()
        {
            if (_showHiddenFramesItem == null) return;

            _showHiddenFramesItem.DropDownItems.Clear();
            _showHiddenFramesItem.Enabled = HiddenFrames.Count > 0;

            foreach (var frame in HiddenFrames)
            {
                var menuItem = new ToolStripMenuItem(frame.Title);
                menuItem.Click += (s, e) => ShowHiddenFrame(frame.Title);
                _showHiddenFramesItem.DropDownItems.Add(menuItem);
            }
        }


        public void UpdateProfilesMenu()
        {
            if (_profilesMenuItem == null) return;

            _profilesMenuItem.DropDownItems.Clear();
            string currentProfile = ProfileManager.CurrentProfileName;

            // Get sorted list of profiles
            var profiles = ProfileManager.GetProfiles();

            // 1. List Existing Profiles
            foreach (var profile in profiles)
            {
                // Format: "Default [0]" or "Work [1]"
                string label = $"{profile.Name} [{profile.Id}]";
                var item = new ToolStripMenuItem(label);

                if (string.Equals(profile.Name, currentProfile, StringComparison.OrdinalIgnoreCase))
                {
                    item.Checked = true;
                    item.Enabled = false; // Disable clicking the active one
                }
                else
                {
                    item.Click += (s, e) =>
                    {
                        ProfileManager.SwitchToProfile(profile.Name);
                        // Update the 'Home' profile so automation reverts to this manual choice later
                        ProfileManager.SetManualBaseProfile(profile.Name);
                        _trayIcon.Text = $"Desktop Frames ({profile.Name})";
                        UpdateProfilesMenu();
                    };
                }
                _profilesMenuItem.DropDownItems.Add(item);
            }

            _profilesMenuItem.DropDownItems.Add(new ToolStripSeparator());

            // 2. Quick Action: Create New Profile (Keep this for speed)
            var createItem = new ToolStripMenuItem("Create New Profile...");
            createItem.Click += (s, e) =>
            {
                string newName = Microsoft.VisualBasic.Interaction.InputBox("Enter name for new profile:", "New Profile");

                if (!string.IsNullOrWhiteSpace(newName))
                {
                    if (ProfileManager.CreateProfile(newName))
                    {
                        UpdateProfilesMenu();
                        MessageBoxesManager.ShowOKOnlyMessageBoxForm($"Profile '{newName}' created successfully.", "Success");
                    }
                    else
                    {
                        MessageBoxesManager.ShowOKOnlyMessageBoxForm("Failed to create profile. Name invalid or already exists.", "Error");
                    }
                }
            };
            _profilesMenuItem.DropDownItems.Add(createItem);

            // 3. Full UI: Manage Profiles (The new form)
            var manageItem = new ToolStripMenuItem("Manage Profiles...");
            manageItem.Click += (s, e) =>
            {
                // Open the new Manager Window
                var form = new ProfileManagerForm();
                form.ShowDialog();

                // Refresh menu immediately after closing the manager
                // This ensures renames/reorders/deletes are reflected in the tray instantly
                UpdateProfilesMenu();
            };
            _profilesMenuItem.DropDownItems.Add(manageItem);
        }


        // --- NEW METHODS START ---

        // 1. The Public Toggle Method (Called by Options Form)
      
        public void ToggleStartWithWindows(bool enable)
        {
            try
            {
                // A. Update the Registry (The new reliable way)
                SetRegistryStartup(enable);

                // ====================================================================
                // [LEGACY "FENCES" MIGRATION - DO NOT REMOVE]
                // Retained to safely scrub older installations of trademarked terms.
                // ====================================================================
                // B. AGGRESSIVE CLEANUP: Clean any lingering shortcuts to enforce registry-only startup.
                string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string legacyFencesShortcut = Path.Combine(startupPath, "Desktop Fences.lnk");
                string legacyFramesShortcut = Path.Combine(startupPath, "Desktop Frames +.lnk");

                if (File.Exists(legacyFencesShortcut))
                {
                    File.Delete(legacyFencesShortcut);
                    LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, "TrayManager: Legacy 'Frames' shortcut removed.");
                }
                if (File.Exists(legacyFramesShortcut))
                {
                    File.Delete(legacyFramesShortcut);
                    LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, "TrayManager: Legacy 'Frames' shortcut removed.");
                }

                IsStartWithWindows = enable;
                LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.Settings, $"Start with Windows set to: {enable}");
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"Failed to toggle Start with Windows: {ex.Message}");
                throw;
            }
        }
        // 2. Migration Logic: Runs once on startup
        private void PerformStartupMigration()
        {
            // If we already flagged this as done in RegistryHelper, stop here.
            if (RegistryHelper.IsStartupMigrated()) return;

            // ====================================================================
            // [LEGACY "FENCES" MIGRATION - DO NOT REMOVE]
            // Retained to safely scrub older installations of trademarked terms.
            // ====================================================================
            string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupPath, "Desktop Fences.lnk");

            // If the old shortcut exists, it means the user WANTED start-up enabled.
            // We must transfer that intent to the Registry.
            if (File.Exists(shortcutPath))
            {
                try
                {
                    SetRegistryStartup(true); // Create Registry Key
                    File.Delete(shortcutPath); // Delete Old Shortcut
                    LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.General, "TrayManager: Migrated startup from Shortcut to Registry.");
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"TrayManager: Migration Error: {ex.Message}");
                }
            }

            // Mark as migrated so we don't run this logic again
            RegistryHelper.SetStartupMigrated();
        }

        // 3. Helper to write/delete the Registry Key
        private void SetRegistryStartup(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY_PATH, true))
            {
                if (key == null) return;

                if (enable)
                {
                    // We wrap the path in quotes to be safe against spaces in path
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                    key.SetValue(APP_NAME, $"\"{exePath}\"");
                }
                else
                {
                    // If disabling, remove the value
                    key.DeleteValue(APP_NAME, false);
                }
            }
        }

        // 4. Status Checker (Replaces IsInStartupFolder)
        private bool CheckIfStartWithWindowsEnabled()
        {
            // First, check if the Registry Key exists
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY_PATH, false))
            {
                if (key != null && key.GetValue(APP_NAME) != null)
                {
                    return true;
                }
            }

            // ====================================================================
            // [LEGACY "FENCES" MIGRATION - DO NOT REMOVE]
            // Retained to safely scrub older installations of trademarked terms.
            // ====================================================================
            // Fallback: Check if the old shortcut exists (in case migration hasn't run yet)
            string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return File.Exists(Path.Combine(startupPath, "Desktop Fences.lnk"));
        }
        // --- NEW METHODS END ---


        public void Dispose()
        {
            if (_disposed) return;
            _trayIcon?.Dispose();
            if (_lastIconHandle != IntPtr.Zero) { DestroyIcon(_lastIconHandle); _lastIconHandle = IntPtr.Zero; }
            _disposed = true;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr hIcon);
        private IntPtr _lastIconHandle = IntPtr.Zero;

        /// <summary>True when the Windows taskbar/system uses the light theme (so we draw a dark glyph).</summary>
        private static bool IsTaskbarLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k?.GetValue("SystemUsesLightTheme") is int i) return i != 0;
                }
            }
            catch { }
            return false; // default: dark taskbar
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Draws the chosen minimalist tray glyph (theme-aware monochrome) on a 32px canvas.</summary>
        private static void DrawTrayGlyph(Graphics g, string style)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = IsTaskbarLight() ? Color.FromArgb(58, 58, 58) : Color.FromArgb(240, 242, 245);
            using (var fill = new SolidBrush(c))
            using (var dim = new SolidBrush(Color.FromArgb(120, c)))
            using (var pen = new Pen(c, 3f) { Alignment = PenAlignment.Center, LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                switch (style)
                {
                    case "Stacked": // A — two offset rounded frames
                        using (var back = RoundedRect(new RectangleF(4, 4, 18, 18), 4)) g.FillPath(dim, back);
                        using (var front = RoundedRect(new RectangleF(10, 10, 18, 18), 4)) g.FillPath(fill, front);
                        break;

                    case "Grid": // B — 2x2 dots inside a rounded frame
                        using (var box = RoundedRect(new RectangleF(4, 4, 24, 24), 6)) g.DrawPath(pen, box);
                        foreach (var (dx, dy) in new[] { (9, 9), (17, 9), (9, 17), (17, 17) })
                            using (var dot = RoundedRect(new RectangleF(dx, dy, 6, 6), 1.5f)) g.FillPath(fill, dot);
                        break;

                    default: // C — nested frame
                        using (var outer = RoundedRect(new RectangleF(4, 4, 24, 24), 7)) g.DrawPath(pen, outer);
                        using (var inner = RoundedRect(new RectangleF(11, 11, 10, 10), 2.5f)) g.FillPath(fill, inner);
                        break;
                }
            }
        }

        /// <summary>Builds the tray icon: the minimalist glyph plus the orange hidden-frames count badge.</summary>
        private Icon BuildTrayIcon(int count)
        {
            var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                DrawTrayGlyph(g, SettingsManager.TrayIconStyle ?? "Nested");

                if (count > 0)
                {
                    int d = 24, x = -4, y = -1;
                    using (var circleBrush = new SolidBrush(Color.FromArgb(230, 255, 153, 53)))
                        g.FillEllipse(circleBrush, x, y, d, d);
                    using (var font = new Font("Calibri", 26, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var textBrush = new SolidBrush(Color.Navy))
                    {
                        string text = count.ToString();
                        var ts = g.MeasureString(text, font);
                        g.DrawString(text, font, textBrush, x + (d - ts.Width) / 2, y + (d - ts.Height) / 2);
                    }
                }
            }
            IntPtr h = bitmap.GetHicon();
            bitmap.Dispose();
            return Icon.FromHandle(h);
        }

        public void UpdateTrayIcon()
        {
            if (Showintray == true)
            {
                // FIX: Update the tooltip text to match the current profile
                _trayIcon.Text = $"Desktop Frames + ({ProfileManager.CurrentProfileName})";

                var newIcon = BuildTrayIcon(HiddenFrames.Count + _tempHiddenFrames.Count);
                _trayIcon.Icon = newIcon;
                // Free the previous GDI icon handle (Icon.FromHandle doesn't own it) to avoid a handle leak.
                if (_lastIconHandle != IntPtr.Zero) DestroyIcon(_lastIconHandle);
                _lastIconHandle = newIcon.Handle;
                _trayIcon.Visible = true;
            }
            else
            {
                _trayIcon.Visible = false; // Properly hide the icon
            }
        }

		/// <summary>
		/// Clears all references to hidden frames. 
		/// Call this when switching profiles or reloading frames to prevent "Zombie" windows.
		/// </summary>
		// Add inside TrayManager class
		public void ClearHiddenFrames()
        {
            HiddenFrames.Clear();
            _tempHiddenFrames.Clear();
            _areFramesTempHidden = false;
            UpdateHiddenFramesMenu();
            UpdateTrayIcon();
        }



    }
}