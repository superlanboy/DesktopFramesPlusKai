using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices; // Added for DeleteObject
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.VisualStyles;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging; // Added for ImageSource
using IWshRuntimeLibrary;

namespace Desktop_Frames
{
    public static class Utility
    {

        public static Effect CreateIconEffect(IconVisibilityEffect effectType, string frameColor = null)
        {
            switch (effectType)
            {
                case IconVisibilityEffect.Glow:
                    return new DropShadowEffect
                    {
                        Color = Colors.White,
                        Direction = 0,
                        ShadowDepth = 0,
                        BlurRadius = 8,
                        Opacity = 0.6
                    };

                case IconVisibilityEffect.Shadow:
                    return new DropShadowEffect
                    {
                        Color = Colors.Black,
                        Direction = 315,
                        ShadowDepth = 3,
                        BlurRadius = 5,
                        Opacity = 0.7
                    };

                case IconVisibilityEffect.Outline:
                    return new DropShadowEffect
                    {
                        Color = Colors.White,
                        Direction = 0,
                        ShadowDepth = 0,
                        BlurRadius = 2,
                        Opacity = 0.8
                    };

                case IconVisibilityEffect.StrongShadow:
                    return new DropShadowEffect
                    {
                        Color = Colors.Black,
                        Direction = 315,
                        ShadowDepth = 5,
                        BlurRadius = 10,
                        Opacity = 0.9
                    };

                case IconVisibilityEffect.ColoredGlow:
                    var glowColor = string.IsNullOrEmpty(frameColor)
                        ? Colors.White
                        : GetColorFromName(frameColor);
                    return new DropShadowEffect
                    {
                        Color = glowColor,
                        Direction = 0,
                        ShadowDepth = 0,
                        BlurRadius = 6,
                        Opacity = 0.5
                    };

                //case IconVisibilityEffect.AngelGlow:
                //    // This would require a different approach with Border/Ellipse
                //    //  return null; // Handle separately if needed
                //    // Brighten effect - makes icons appear brighter/more visible
                //    return new System.Windows.Media.Effects.DropShadowEffect
                //    {
                //        Color = System.Windows.Media.Colors.Blue,
                //        Direction = 0,
                //        ShadowDepth = 0,
                //        BlurRadius = 36,
                //        Opacity = 1.0
                //    };
                case Desktop_Frames.IconVisibilityEffect.AngelGlow:
                    return new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = System.Windows.Media.Colors.Snow,
                        Direction = 0,
                        ShadowDepth = 0,
                        BlurRadius = 5,
                        Opacity = 1.0
                    };



                case IconVisibilityEffect.None:
                default:
                    return null;
            }
        }

        // TABS FEATURE: Generate tab color scheme based on frame color
        public static (Color activeTab, Color inactiveTab, Color hoverTab, Color borderColor) GenerateTabColorScheme(string frameColorName)
        {
            var baseColor = GetColorFromName(frameColorName);

            // Convert to HSV for better color manipulation
            var hsv = RgbToHsv(baseColor);

            Color activeTab, inactiveTab, hoverTab, borderColor;

            // Handle special cases for very light or very dark colors
            if (hsv.Value < 0.3) // Very dark colors
            {
                // For dark colors, brighten significantly for active tab
                activeTab = HsvToRgb(hsv.Hue, hsv.Saturation * 0.8, Math.Min(hsv.Value + 0.4, 1.0));
                inactiveTab = HsvToRgb(hsv.Hue, hsv.Saturation * 0.3, Math.Min(hsv.Value + 0.6, 0.9));
                hoverTab = HsvToRgb(hsv.Hue, hsv.Saturation * 0.5, Math.Min(hsv.Value + 0.5, 0.95));
                borderColor = HsvToRgb(hsv.Hue, hsv.Saturation * 0.6, Math.Min(hsv.Value + 0.3, 0.8));
            }
            else if (hsv.Value > 0.8 && hsv.Saturation < 0.3) // Very light colors (like White, Beige)
            {
                // For light colors, use deeper variations
                activeTab = HsvToRgb(hsv.Hue, Math.Min(hsv.Saturation + 0.3, 0.6), hsv.Value * 0.7);
                inactiveTab = HsvToRgb(hsv.Hue, Math.Min(hsv.Saturation + 0.1, 0.2), hsv.Value * 0.95);
                hoverTab = HsvToRgb(hsv.Hue, Math.Min(hsv.Saturation + 0.2, 0.4), hsv.Value * 0.8);
                borderColor = HsvToRgb(hsv.Hue, Math.Min(hsv.Saturation + 0.4, 0.7), hsv.Value * 0.6);
            }
            else // Normal colors
            {
                // Active tab: slightly brighter and more saturated
                activeTab = HsvToRgb(hsv.Hue, Math.Min(hsv.Saturation + 0.1, 1.0), Math.Min(hsv.Value + 0.2, 1.0));

                // Inactive tab: much lighter and less saturated
                inactiveTab = HsvToRgb(hsv.Hue, hsv.Saturation * 0.3, Math.Min(hsv.Value + 0.4, 0.95));

                // Hover tab: between active and inactive
                hoverTab = HsvToRgb(hsv.Hue, hsv.Saturation * 0.7, Math.Min(hsv.Value + 0.3, 0.9));

                // Border: slightly darker than active
                borderColor = HsvToRgb(hsv.Hue, Math.Min(hsv.Saturation + 0.2, 1.0), hsv.Value * 0.8);
            }

            return (activeTab, inactiveTab, hoverTab, borderColor);
        }

        // Helper method to convert RGB to HSV
        private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            double hue = 0;
            if (delta != 0)
            {
                if (max == r) hue = ((g - b) / delta) % 6;
                else if (max == g) hue = (b - r) / delta + 2;
                else hue = (r - g) / delta + 4;
                hue *= 60;
                if (hue < 0) hue += 360;
            }

            double saturation = max == 0 ? 0 : delta / max;
            double value = max;

            return (hue, saturation, value);
        }

        // Helper method to convert HSV to RGB
        private static Color HsvToRgb(double hue, double saturation, double value)
        {
            double c = value * saturation;
            double x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
            double m = value - c;

            double r, g, b;

            if (hue >= 0 && hue < 60) { r = c; g = x; b = 0; }
            else if (hue >= 60 && hue < 120) { r = x; g = c; b = 0; }
            else if (hue >= 120 && hue < 180) { r = 0; g = c; b = x; }
            else if (hue >= 180 && hue < 240) { r = 0; g = x; b = c; }
            else if (hue >= 240 && hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255)
            );
        }

        /// <summary>
        /// Resolves the transparency (tint opacity 0-100) for a frame: a per-frame "CustomTint"
        /// override if one is set, otherwise the global default (SettingsManager.TintValue).
        /// </summary>
        public static int ResolveFrameTint(Window frame)
        {
            int def = SettingsManager.TintValue;
            try
            {
                string frameId = (frame as FrameworkElement)?.Tag?.ToString();
                if (string.IsNullOrEmpty(frameId)) return def;

                var fd = FrameDataManager.FrameData.FirstOrDefault(f => f.Id?.ToString() == frameId);
                if (fd == null) return def;

                string ct = null;
                try { ct = fd.CustomTint?.ToString(); } catch { }
                if (!string.IsNullOrWhiteSpace(ct) && int.TryParse(ct, out int v))
                    return Math.Max(0, Math.Min(100, v));
            }
            catch { }
            return def;
        }

        public static void ApplyTintAndColorToFrame(Window frame, string colorName = null, int? tintOverride = null)
        {
            var frameControl = frame.Content as Border; // Matches your structure
            if (frameControl == null) return;

            string effectiveColor = colorName ?? SettingsManager.SelectedColor; // Fallback to global

            // Per-frame tint override (Customize dialog) if provided; else resolve saved/global value.
            int tint = tintOverride ?? ResolveFrameTint(frame);

            LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.Settings,
                $"ApplyTintAndColorToFrame: Tint={tint}, Color={effectiveColor}, Opacity={tint / 100.0}");

            // Use the resolved tint to determine if/how tint is applied
            frameControl.Background = tint > 0
                ? new SolidColorBrush(GetColorFromName(effectiveColor)) { Opacity = tint / 100.0 }
                : Brushes.Transparent;

            // TABS FEATURE: Refresh tab colors when frame color changes
            if (frame is NonActivatingWindow frameWindow)
            {
                Framemanager.RefreshTabColors(frameWindow, effectiveColor);
            }
        }


        public static Color GetColorFromName(string colorName)
        {
            // --- CHAMELEON MODE HOOK ---
            // If Chameleon is enabled and this frame is using the global default color
            if (SettingsManager.EnableChameleonMode && (string.IsNullOrEmpty(colorName) || colorName == SettingsManager.SelectedColor || colorName == "Default"))
            {
                return WallpaperColorManager.CurrentWallpaperColor;
            }

            return colorName switch
            {

                "Red" => (Color)ColorConverter.ConvertFromString("#9E052E"),
                "Green" => (Color)ColorConverter.ConvertFromString("#06491A"),
                "Teal" => (Color)ColorConverter.ConvertFromString("#008080"),
                "Blue" => (Color)ColorConverter.ConvertFromString("#012162"),
                "Bismark" => (Color)ColorConverter.ConvertFromString("#49697E"),
                "White" => (Color)ColorConverter.ConvertFromString("#F1F1F6"),
                "Beige" => (Color)ColorConverter.ConvertFromString("#C8AD7E"),
                "Gray" => (Color)ColorConverter.ConvertFromString("#6E6E6E"),
                "Black" => (Color)ColorConverter.ConvertFromString("#0b0b0c"),
                "Purple" => (Color)ColorConverter.ConvertFromString("#3a0b50"),
                "Fuchsia" => (Color)ColorConverter.ConvertFromString("#5F093d"),
                "Yellow" => (Color)ColorConverter.ConvertFromString("#C1C708"),
                "Orange" => (Color)ColorConverter.ConvertFromString("#B75433"),
                _ => Colors.Transparent,
                // "Red" => (Color)ColorConverter.ConvertFromString("#c10338"),
                // "Green" => (Color)ColorConverter.ConvertFromString("#005618"),
                // "Blue" => (Color)ColorConverter.ConvertFromString("#012162"),
                //  "White" => (Color)ColorConverter.ConvertFromString("#fdfdff"),
                //  "Gray" => (Color)ColorConverter.ConvertFromString("#3d3d3f"),
                //  "Black" => (Color)ColorConverter.ConvertFromString("#0b0b0c"),
                // "Purple" => (Color)ColorConverter.ConvertFromString("#3a0b50"),
                //  "Yellow" => (Color)ColorConverter.ConvertFromString("#d8da1f"),
                //  _ => Colors.Transparent, 
            };
        }


        public static void UpdateFrameVisuals()
        {
            if (System.Windows.Application.Current == null) return;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // 1. Prepare Symbols
                string[] menuSymbols = { "♥", "☰", "≣", "𓃑" };

                int menuIdx = SettingsManager.MenuIcon;
                if (menuIdx < 0 || menuIdx >= menuSymbols.Length) menuIdx = 0;

                string menuSymbol = menuSymbols[menuIdx];
                // Pin glyph is drawn in the Segoe Fluent icon font (same as FrameManager) — must match, or
                // this refresh would clobber the title-bar pin with a wrong/stale glyph.
                string lockSymbol = Framemanager.PosLockGlyph(SettingsManager.LockIcon);
                double iconOpacity = (double)SettingsManager.MenuTintValue / 100.0;

                // Get Data List Once
                var allFrames = Framemanager.GetFrameData();

                // 2. Iterate All Open Frames
                foreach (var win in System.Windows.Application.Current.Windows.OfType<NonActivatingWindow>())
                {
                    string frameId = win.Tag?.ToString();
                    if (string.IsNullOrEmpty(frameId)) continue;

                    // --- FIX START: Robust Data Lookup ---
                    // Explicitly find the frame data without relying on risky dynamic LINQ
                    dynamic FrameData = null;
                    foreach (var f in allFrames)
                    {
                        string id = null;
                        // Handle JObject (Loaded from JSON)
                        if (f is Newtonsoft.Json.Linq.JObject j)
                            id = j["Id"]?.ToString();
                        // Handle Anonymous/Expando (Newly Created)
                        else
                        {
                            try { id = f.GetType().GetProperty("Id")?.GetValue(f)?.ToString(); } catch { }
                            if (id == null) try { id = f.Id?.ToString(); } catch { }
                        }

                        if (string.Equals(id, frameId, StringComparison.OrdinalIgnoreCase))
                        {
                            FrameData = f;
                            break;
                        }
                    }

                    // Determine Color
                    string colorToApply = SettingsManager.SelectedColor; // Default to Global

                    if (FrameData != null)
                    {
                        string customColor = null;
                        // Robust CustomColor Extraction
                        if (FrameData is Newtonsoft.Json.Linq.JObject jObj)
                            customColor = jObj["CustomColor"]?.ToString();
                        else
                        {
                            try { customColor = FrameData.CustomColor?.ToString(); } catch { }
                            if (customColor == null) try { customColor = FrameData.GetType().GetProperty("CustomColor")?.GetValue(FrameData)?.ToString(); } catch { }
                        }

                        // If valid custom color, override global
                        if (!string.IsNullOrEmpty(customColor) && customColor != "Default")
                        {
                            colorToApply = customColor;
                        }
                    }
                    // --- FIX END ---

                    // 3. Apply Visuals
                    ApplyTintAndColorToFrame(win, colorToApply);

                    // 4. Update Icons (Using Named FindChild for safety)
                    var menuIcon = FindChild<TextBlock>(win, "FrameMenuIcon");
                    if (menuIcon != null)
                    {
                        menuIcon.Text = menuSymbol;
                        menuIcon.BeginAnimation(UIElement.OpacityProperty, null);
                        menuIcon.Opacity = iconOpacity;
                    }

                    // Filter icon: refresh the glyph only — Foreground/Opacity carry the "filter active"
                    // (orange) state and are managed by the filter handlers.
                    var filterIcon = FindChild<TextBlock>(win, "FrameFilterIcon");
                    if (filterIcon != null)
                    {
                        filterIcon.FontFamily = Framemanager.GlyphIconFont;
                        filterIcon.Text = Framemanager.FilterGlyph(SettingsManager.FilterIcon);
                    }

                    var lockIcon = FindChild<TextBlock>(win, "FrameLockIcon");
                    if (lockIcon != null)
                    {
                        lockIcon.FontFamily = Framemanager.GlyphIconFont;
                        lockIcon.Text = lockSymbol;
                        lockIcon.BeginAnimation(UIElement.OpacityProperty, null);

                        // Pinned state cue must match UpdateLockState: pinned = DeepPink + full opacity,
                        // unpinned = white + muted (Menu Tint).
                        bool isLocked = false;
                        if (FrameData != null)
                        {
                            string lockedStr = null;
                            if (FrameData is Newtonsoft.Json.Linq.JObject j) lockedStr = j["IsLocked"]?.ToString();
                            else try { lockedStr = FrameData.IsLocked?.ToString(); } catch { }
                            isLocked = lockedStr?.ToLower() == "true";
                        }
                        lockIcon.Opacity = isLocked ? 1.0 : iconOpacity;
                        lockIcon.Foreground = isLocked ? System.Windows.Media.Brushes.DeepPink : System.Windows.Media.Brushes.White;
                    }

                    // Content-lock padlock: same engaged/disengaged cue, driven by ContentLocked.
                    var cLockIcon = FindChild<TextBlock>(win, "FrameContentLockIcon");
                    if (cLockIcon != null && FrameData != null)
                    {
                        bool cLocked = Framemanager.IsContentLocked(FrameData);
                        cLockIcon.BeginAnimation(UIElement.OpacityProperty, null);
                        cLockIcon.Opacity = cLocked ? 1.0 : iconOpacity;
                        cLockIcon.Foreground = cLocked ? System.Windows.Media.Brushes.DeepPink : System.Windows.Media.Brushes.White;
                    }

                    // Keep-on-top pin: same engaged/disengaged cue, driven by AlwaysOnTop.
                    var onTopIcon = FindChild<TextBlock>(win, "FrameOnTopIcon");
                    if (onTopIcon != null)
                    {
                        onTopIcon.FontFamily = Framemanager.GlyphIconFont;
                        onTopIcon.Text = Framemanager.OnTopGlyph;
                        onTopIcon.BeginAnimation(UIElement.OpacityProperty, null);

                        bool isOnTop = false;
                        if (FrameData != null)
                        {
                            string onTopStr = null;
                            if (FrameData is Newtonsoft.Json.Linq.JObject j2) onTopStr = j2["AlwaysOnTop"]?.ToString();
                            else try { onTopStr = FrameData.AlwaysOnTop?.ToString(); } catch { }
                            isOnTop = onTopStr?.ToLower() == "true";
                        }
                        onTopIcon.Opacity = isOnTop ? 1.0 : iconOpacity;
                        onTopIcon.Foreground = isOnTop ? System.Windows.Media.Brushes.DeepPink : System.Windows.Media.Brushes.White;
                    }

                    // 5. Update Note Text Contrast (if applicable)
                    try
                    {
                        string type = null;
                        if (FrameData is Newtonsoft.Json.Linq.JObject j) type = j["ItemsType"]?.ToString();
                        else try { type = FrameData.ItemsType?.ToString(); } catch { }

                        if (type == "Note" && FrameData != null)
                        {
                            var border = win.Content as Border;
                            var dockPanel = border?.Child as DockPanel;
                            var noteTextBox = dockPanel?.Children.OfType<TextBox>().FirstOrDefault();

                            if (noteTextBox != null)
                            {
                                NoteFramemanager.RefreshNoteVisuals(FrameData, noteTextBox);
                            }
                        }
                    }
                    catch { }
                }
            });
        }




        // Helper to find named elements easily
        private static T FindChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            if (parent == null) return null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild && (child as FrameworkElement)?.Name == childName)
                {
                    return typedChild;
                }

                var result = FindChild<T>(child, childName);
                if (result != null) return result;
            }
            return null;
        }



        public static bool IsExecutableFile(string filePath)
        {
            string[] executableExtensions = { ".exe", ".bat", ".cmd", ".vbs", ".ps1", ".hta", ".msi" };
            if (Path.GetExtension(filePath).ToLower() == ".lnk")
            {
                try
                {
                    WshShell shell = new WshShell();
                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(filePath);
                    filePath = shortcut.TargetPath;
                }
                catch
                {
                    return false;
                }
            }
            string extension = Path.GetExtension(filePath).ToLower();
            return executableExtensions.Contains(extension);
        }

        public static string GetShortcutTarget(string filePath)
        {
            if (Path.GetExtension(filePath).ToLower() == ".lnk")
            {
                try
                {
                    WshShell shell = new WshShell();
                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(filePath);
                    return shortcut.TargetPath;
                }
                catch
                {
                    return null;
                }
            }
            return filePath;
        }
        public static string GetShortcutArguments(string filePath)
        {
            if (System.IO.Path.GetExtension(filePath).ToLower() == ".lnk")
            {
                try
                {
                    WshShell shell = new WshShell();
                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(filePath);
                    return shortcut.Arguments;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        public static System.Drawing.Image LoadImageFromResources(string resourcePath)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException($"Resource '{resourcePath}' not found.");
                }
                return System.Drawing.Image.FromStream(stream);
            }
        }
        /// <summary>
        /// Converts a System.Drawing.Icon to a WPF ImageSource.
        /// </summary>
        /// <param name="icon">The icon to convert.</param>
        /// <returns>An ImageSource usable in WPF.</returns>
        public static System.Windows.Media.ImageSource ToImageSource(this System.Drawing.Icon icon)
        {
            using (var bitmap = icon.ToBitmap())
            {
                var hBitmap = bitmap.GetHbitmap();
                try
                {
                    var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());

                    if (img.CanFreeze)
                    {
                        img.Freeze();
                    }

                    return img;
                }
                finally
                {
                    DeleteObject(hBitmap); // Clean up the HBITMAP handle
                }
            }
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // --- NEW: Native Shell API for robust icon extraction ---
        // --- UPDATED: Strict Unicode Shell API ---
    
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;    // 32x32
        private const uint SHGFI_SMALLICON = 0x1;    // 16x16
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_LINKOVERLAY = 0x000008000; // Show shortcut overlay? Optional.

        // --- Performance: cache shell icons/type-names by extension ---
        // Most files of the same type share one icon, so we avoid a shell call per file.
        // Types whose icon is per-file (executables, shortcuts, custom icons) are never cached.
        private static readonly Dictionary<string, ImageSource> _extIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _extTypeNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _extCacheLock = new object();
        private static readonly HashSet<string> _perFileIconExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".url", ".ico", ".cur", ".ani", ".scr", ".msc", ".cpl", ".appref-ms" };

        public static ImageSource GetShellIcon(string path, bool isFolder)
        {
            // Fast path: reuse a cached icon for cacheable file types.
            string cacheExt = null;
            if (!isFolder && !string.IsNullOrEmpty(path))
            {
                string e = System.IO.Path.GetExtension(path);
                if (!string.IsNullOrEmpty(e) && !_perFileIconExts.Contains(e))
                {
                    cacheExt = e.ToLowerInvariant();
                    lock (_extCacheLock)
                    {
                        if (_extIconCache.TryGetValue(cacheExt, out var hit)) return hit;
                    }
                }
            }

            try
            {
                // 1. PATH CORRECTION
                if (!System.IO.Path.IsPathRooted(path))
                {
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    // If path is relative, assume it's in the Shortcuts folder relative to EXE
                    string checkPath = System.IO.Path.Combine(exeDir, path);
                    if (System.IO.File.Exists(checkPath)) path = checkPath;
                }

                SHFILEINFO shinfo = new SHFILEINFO();

                // 2. FLAG SELECTION
                // Critical: Do NOT use SHGFI_USEFILEATTRIBUTES for .lnk files. 
                // We want the Shell to read the file contents (the shortcut target), not just the file extension.
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;

                // 3. SPECIAL HANDLING FOR UWP SHORTCUTS
                // If it is a shortcut, we force the shell to resolve it.
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    // Passing 0 as attributes forces shell to access the file
                    SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
                }
                else if (isFolder)
                {
                    // FIX: Remove SHGFI_USEFILEATTRIBUTES to force Shell to read desktop.ini for custom icons.
                    // We pass 0 for attributes so the Shell accesses the disk.
                    SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
                }
                //else if (isFolder)
                //{
                //    // Optimization: For real folders, use attributes to avoid disk spin-up
                //    flags |= SHGFI_USEFILEATTRIBUTES;
                //    SHGetFileInfo(path, 0x00000010, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
                //}
                else
                {
                    // Standard files
                    SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
                }

                if (shinfo.hIcon == IntPtr.Zero) return null;

                var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                if (img.CanFreeze)
                {
                    img.Freeze();
                }

                DeleteObject(shinfo.hIcon);

                if (cacheExt != null && img != null)
                {
                    lock (_extCacheLock) { _extIconCache[cacheExt] = img; }
                }
                return img;
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.IconHandling, $"GetShellIcon failed for {path}: {ex.Message}");
                return null;
            }
        }

        // --- Details View helpers (Portal frame column data) ---
        private const uint SHGFI_TYPENAME = 0x400;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        /// <summary>
        /// Returns the shell "Type" description (e.g. "Text Document", "File folder") like Explorer's Type column.
        /// Uses SHGFI_USEFILEATTRIBUTES so it resolves by extension without touching the disk.
        /// </summary>
        public static string GetShellTypeName(string path, bool isFolder)
        {
            // Type name is resolved by extension (SHGFI_USEFILEATTRIBUTES), so cache it per extension.
            string typeExt = isFolder ? null : System.IO.Path.GetExtension(path)?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(typeExt))
            {
                lock (_extCacheLock)
                {
                    if (_extTypeNameCache.TryGetValue(typeExt, out var cachedType)) return cachedType;
                }
            }

            try
            {
                SHFILEINFO shinfo = new SHFILEINFO();
                uint attr = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                SHGetFileInfo(path, attr, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
                string type = shinfo.szTypeName;
                if (string.IsNullOrWhiteSpace(type))
                {
                    if (isFolder) return "File folder";
                    string ext = System.IO.Path.GetExtension(path);
                    type = string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " File";
                }

                if (!string.IsNullOrEmpty(typeExt))
                {
                    lock (_extCacheLock) { _extTypeNameCache[typeExt] = type; }
                }
                return type;
            }
            catch { return isFolder ? "File folder" : "File"; }
        }

        /// <summary>
        /// Formats a byte count the way Explorer does (KB rounded up, MB/GB with one decimal).
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes + " bytes";
            double kb = bytes / 1024.0;
            if (kb < 1024) return Math.Ceiling(kb).ToString("N0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("N1") + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("N1") + " GB";
        }


        /// <summary>
        /// Checks if a shortcut points to the virtual "Applications" folder (Store App).
        /// Uses binary inspection to find the "APPS" signature.
        /// </summary>
        public static bool IsStoreAppShortcut(string lnkPath)
        {
            try
            {
                if (!System.IO.File.Exists(lnkPath)) return false;

                // Read the first 4096 bytes. The "APPS" signature is always in the header.
                byte[] buffer = new byte[4096];
                using (var fs = new System.IO.FileStream(lnkPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    fs.Read(buffer, 0, buffer.Length);
                }

                // Convert to ASCII. The "APPS" signature is stored as standard text.
                string rawData = System.Text.Encoding.ASCII.GetString(buffer);

                // CHECK: "APPS" signature identifies shortcuts to shell:AppsFolder
                return rawData.Contains("APPS");
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.IconHandling, $"Binary check failed for {lnkPath}: {ex.Message}");
                return false;
            }
        }


    }

       
    }





