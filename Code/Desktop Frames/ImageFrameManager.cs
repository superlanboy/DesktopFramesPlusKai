using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Desktop_Frames
{
    /// <summary>
    /// Image frames (ItemsType == "Image"): a frame that displays a SINGLE image filling its content
    /// area (like a Note frame is a single text box). You move it by dragging the frame, and resize it
    /// like any frame — the image scales to fit. Starts unlocked so an image can be set straight away;
    /// the content lock (ContentLocked) can then prevent accidental changes.
    /// The image is stored either copied into a per-frame asset folder or linked to the original file.
    /// </summary>
    public static class ImageFramemanager
    {
        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" };

        private const int DisplayCapWidth = 2560; // downscale only very large images (memory-safe)

        // frameId -> the content Grid (Image + placeholder), so we can refresh after add/clear.
        private static readonly Dictionary<string, Grid> _hosts = new Dictionary<string, Grid>();

        // ---- Asset storage --------------------------------------------------

        public static string GetAssetDir(string frameId)
        {
            string dir = Path.Combine(ProfileManager.CurrentProfileDir, "ImageFrames", frameId ?? "unknown");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static void DeleteAssetDir(string frameId)
        {
            try
            {
                if (!string.IsNullOrEmpty(frameId))
                {
                    _hosts.Remove(frameId);
                    if (_watchers.TryGetValue(frameId, out var w)) // release the file watcher with the frame
                    {
                        try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                        _watchers.Remove(frameId);
                    }
                }
                string dir = Path.Combine(ProfileManager.CurrentProfileDir, "ImageFrames", frameId ?? "unknown");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"DeleteAssetDir: {ex.Message}"); }
        }

        // ---- Lock -----------------------------------------------------------

        // Content lock is shared across frame types — delegate to the central helper.
        public static bool IsLocked(dynamic frame) => Framemanager.IsContentLocked(frame);
        public static void SetLocked(dynamic frame, bool locked) => Framemanager.SetContentLocked(frame, locked);

        // ---- Single-image state --------------------------------------------

        private static string GetFile(dynamic frame) { try { return frame.ImageFile?.ToString() ?? ""; } catch { return ""; } }
        private static bool GetLinked(dynamic frame) { try { return (frame.ImageLinked?.ToString() ?? "false").ToLower() == "true"; } catch { return false; } }

        public static bool HasImage(dynamic frame) => !string.IsNullOrEmpty(GetFile(frame));

        // Original source path of the current image (kept for display — copied assets get renamed to a
        // timestamp file, losing the user-meaningful name). Empty for pasted clipboard images.
        private static string GetSource(dynamic frame) { try { return frame.ImageSource?.ToString() ?? ""; } catch { return ""; } }

        /// <summary>Title-bar display info: the image's file name and a full path for the tooltip.
        /// Prefers the original source path (copied assets are renamed on disk); falls back to the stored
        /// file. Returns (null, null) when no image is set.</summary>
        public static (string Name, string FullPath) GetDisplayFileInfo(dynamic frame)
        {
            try
            {
                if (!HasImage(frame)) return (null, null);
                string src = GetSource(frame);
                if (!string.IsNullOrEmpty(src)) return (Path.GetFileName(src), src);
                string p = ResolvePath(frame);
                if (string.IsNullOrEmpty(p)) return (null, null);
                return (Path.GetFileName(p), p);
            }
            catch { return (null, null); }
        }

        private static string ResolvePath(dynamic frame)
        {
            string file = GetFile(frame);
            if (string.IsNullOrEmpty(file)) return null;
            if (GetLinked(frame) || Path.IsPathRooted(file)) return file;
            return Path.Combine(GetAssetDir(frame.Id?.ToString()), file);
        }

        private static void SetKey(dynamic frame, string key, string value)
        {
            try
            {
                if (frame is IDictionary<string, object> ed) ed[key] = value;
                else if (frame is JObject jo) jo[key] = value;
            }
            catch { }
        }

        /// <summary>Deletes the current copied asset file (no-op for linked images).</summary>
        private static void DeleteCurrentCopy(dynamic frame)
        {
            try
            {
                if (HasImage(frame) && !GetLinked(frame))
                {
                    string p = ResolvePath(frame);
                    if (p != null && File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }

        private static bool IsImageFile(string path)
        {
            try { return ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()); }
            catch { return false; }
        }

        // ---- Zoom state -------------------------------------------------------

        // Frame key "ImageZoom": "" (or missing) = fit-to-frame; otherwise an integer percent of the
        // decoded image's natural size (clamped 10-800).
        private const int ZoomMin = 10, ZoomMax = 800;

        private static int GetZoom(dynamic frame) // 0 = fit
        {
            try
            {
                string z = frame.ImageZoom?.ToString();
                if (int.TryParse(z, out int p) && p >= ZoomMin && p <= ZoomMax) return p;
            }
            catch { }
            return 0;
        }

        private static void SetZoom(dynamic frame, int percent) // 0 = fit
        {
            SetKey(frame, "ImageZoom", percent <= 0 ? "" : percent.ToString());
            FrameDataManager.SaveFrameData();
            string frameId = frame.Id?.ToString();
            if (!string.IsNullOrEmpty(frameId) && _hosts.TryGetValue(frameId, out var grid))
            {
                // Fit mode decodes memory-lean (frame-sized); zooming needs the full-cap decode for sharp
                // detail — re-decode only when the current bitmap was capped below what's now needed.
                var img = FindImage(grid);
                if (img?.Tag is int used && used != 0 && DesiredDecodeWidth(frame, grid) > used)
                {
                    LoadInto(frame, grid); // reloads at the new target, then applies zoom + toolbar
                    return;
                }
                ApplyZoom(frame, grid);
                UpdateToolbar(frame, grid);
            }
        }

        private static void StepZoom(dynamic frame, bool zoomIn, Grid grid)
        {
            int cur = GetZoom(frame);
            if (cur == 0) cur = EffectiveFitPercent(grid); // leave "fit" from its actual on-screen scale
            double next = zoomIn ? cur * 1.25 : cur / 1.25;
            SetZoom(frame, Math.Clamp((int)Math.Round(next), ZoomMin, ZoomMax));
        }

        /// <summary>The percent the image is effectively shown at while in fit mode (for smooth zoom steps).</summary>
        private static int EffectiveFitPercent(Grid grid)
        {
            try
            {
                var img = FindImage(grid);
                if (img?.Source is BitmapSource bs && bs.Width > 0 && img.ActualWidth > 0)
                    return Math.Clamp((int)Math.Round(img.ActualWidth / bs.Width * 100), ZoomMin, ZoomMax);
            }
            catch { }
            return 100;
        }

        // ---- Content element ------------------------------------------------

        private static Image FindImage(Grid grid) =>
            (grid?.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content) as Image;

        /// <summary>Builds the image frame's content — single Image in a pannable ScrollViewer, a
        /// placeholder, and a hover toolbar (zoom / copy / save / replace / edit + file caption).
        /// The toolbar only appears when the frame has an image and is NOT content-locked.</summary>
        public static FrameworkElement CreateImageContent(dynamic frame, DockPanel dp)
        {
            string frameId = frame.Id?.ToString();

            var grid = new Grid { Margin = new Thickness(2) };
            var placeholder = new TextBlock
            {
                Text = "Drag or paste an image here",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Gray,
                Opacity = 0.7,
                FontSize = 12,
                IsHitTestVisible = false
            };
            var img = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = System.Windows.Media.Brushes.Transparent,
                Focusable = false,
                Content = img
            };

            grid.Children.Add(placeholder);
            grid.Children.Add(scroller);
            grid.Children.Add(BuildToolbar(frameId, grid));
            dp.Children.Add(grid); // attach to the frame (DockPanel's last child fills the content area)

            // Pan when zoomed past fit: drag inside the image scrolls it (frame moves via title bar only).
            bool panning = false; Point panStart = default; double panH = 0, panV = 0;
            scroller.PreviewMouseLeftButtonDown += (s, e) =>
            {
                dynamic live = LiveFrame(frameId);
                if (live == null || GetZoom(live) == 0) return;
                panning = true; panStart = e.GetPosition(scroller);
                panH = scroller.HorizontalOffset; panV = scroller.VerticalOffset;
                scroller.CaptureMouse(); scroller.Cursor = System.Windows.Input.Cursors.SizeAll;
                e.Handled = true;
            };
            scroller.PreviewMouseMove += (s, e) =>
            {
                if (!panning) return;
                var p = e.GetPosition(scroller);
                scroller.ScrollToHorizontalOffset(panH - (p.X - panStart.X));
                scroller.ScrollToVerticalOffset(panV - (p.Y - panStart.Y));
            };
            scroller.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!panning) return;
                panning = false; scroller.ReleaseMouseCapture(); scroller.Cursor = null;
            };

            // Ctrl + mouse wheel zooms (regular wheel is left alone).
            scroller.PreviewMouseWheel += (s, e) =>
            {
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
                dynamic live = LiveFrame(frameId);
                if (live == null || !HasImage(live) || IsLocked(live)) return;
                StepZoom(live, e.Delta > 0, grid);
                e.Handled = true;
            };

            // Hover shows the toolbar (only when it's applicable — flag maintained by UpdateToolbar).
            grid.MouseEnter += (s, e) => FadeToolbar(grid, true);
            grid.MouseLeave += (s, e) => FadeToolbar(grid, false);

            // Fit mode decodes to roughly the frame's size to save memory — if the frame is later
            // enlarged well past what was decoded, re-decode sharper (debounced so resize-drag doesn't
            // thrash the decoder).
            var resizeReload = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            resizeReload.Tick += (s, e) =>
            {
                resizeReload.Stop();
                dynamic live = LiveFrame(frameId);
                if (live == null) return;
                var im = FindImage(grid);
                if (im?.Source == null || !(im.Tag is int used) || used == 0) return; // full-native never needs more
                if (DesiredDecodeWidth(live, grid) > used * 1.25) LoadInto(live, grid);
            };
            grid.SizeChanged += (s, e) => { resizeReload.Stop(); resizeReload.Start(); };

            if (!string.IsNullOrEmpty(frameId)) _hosts[frameId] = grid;
            LoadInto(frame, grid);
            return grid;
        }

        private static dynamic LiveFrame(string frameId) =>
            string.IsNullOrEmpty(frameId) ? null : FrameDataManager.FrameData.FirstOrDefault(f => f.Id?.ToString() == frameId);

        private static void LoadInto(dynamic frame, Grid grid)
        {
            if (grid == null) return;
            var placeholder = grid.Children.OfType<TextBlock>().FirstOrDefault();
            var scroller = grid.Children.OfType<ScrollViewer>().FirstOrDefault();
            var img = FindImage(grid);
            if (img == null) return;

            string path = ResolvePath(frame);
            bool loaded = false;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    int target = DesiredDecodeWidth(frame, grid);
                    var bmp = LoadDisplayImage(path, target);
                    img.Source = bmp;
                    // Remember the decode cap actually applied (0 = full native, never needs re-decode) so
                    // resize/zoom can tell when a sharper re-decode is warranted.
                    img.Tag = bmp.PixelWidth >= target ? target : 0;
                    img.Visibility = Visibility.Visible;
                    if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;
                    loaded = true;
                }
                catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"image load: {ex.Message}"); }
            }

            if (!loaded)
            {
                img.Source = null;
                img.Visibility = Visibility.Collapsed;
                if (placeholder != null)
                {
                    placeholder.Text = IsLocked(frame) ? "No image\n(unlock the frame to add one)" : "Drag or paste an image here";
                    placeholder.Visibility = Visibility.Visible;
                }
            }

            ApplyZoom(frame, grid);
            UpdateToolbar(frame, grid);
            WatchFile(frame.Id?.ToString(), loaded ? path : null); // auto-refresh after external edits
        }

        /// <summary>Applies the frame's zoom mode: fit (Uniform, no scrolling) or N% (natural size scaled,
        /// pannable).</summary>
        private static void ApplyZoom(dynamic frame, Grid grid)
        {
            var scroller = grid?.Children.OfType<ScrollViewer>().FirstOrDefault();
            var img = FindImage(grid);
            if (scroller == null || img == null) return;

            int zoom = GetZoom(frame);
            if (zoom == 0)
            {
                // Disabled = the child is measured at the viewport size, so Uniform fits the frame.
                scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                img.Stretch = Stretch.Uniform;
                img.LayoutTransform = null;
            }
            else
            {
                // Hidden = scrollable (pannable) without visible bars.
                scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
                scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                img.Stretch = Stretch.None;
                double s = zoom / 100.0;
                img.LayoutTransform = new ScaleTransform(s, s);
            }
        }

        // ---- Hover toolbar ---------------------------------------------------

        private const string TbAvailable = "on"; // toolbar.Tag flag: eligible to fade in on hover

        private static Border BuildToolbar(string frameId, Grid grid)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            TextBlock Btn(string glyph, string tip, Action<dynamic> onClick)
            {
                var b = new TextBlock
                {
                    Text = glyph,
                    FontFamily = Framemanager.GlyphIconFont,
                    FontSize = 13,
                    Foreground = System.Windows.Media.Brushes.White,
                    Opacity = 0.75,
                    Margin = new Thickness(6, 3, 6, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = tip
                };
                b.MouseEnter += (s, e) => b.Opacity = 1.0;
                b.MouseLeave += (s, e) => b.Opacity = 0.75;
                b.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    dynamic live = LiveFrame(frameId);
                    if (live != null) onClick(live);
                };
                return b;
            }

            TextBlock Sep() => new TextBlock
            {
                Text = "|",
                Foreground = System.Windows.Media.Brushes.White,
                Opacity = 0.25,
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            // File caption: name shown, full path in the tooltip, click reveals in Explorer.
            var caption = new TextBlock
            {
                Name = "ImgCaption",
                Foreground = System.Windows.Media.Brushes.White,
                Opacity = 0.85,
                FontSize = 11,
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            caption.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                try
                {
                    dynamic live = LiveFrame(frameId);
                    string p = live != null ? GetDisplayFileInfo(live).FullPath : null;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
                }
                catch { }
            };

            var zoomLabel = new TextBlock
            {
                Name = "ImgZoomLabel",
                Text = "Fit",
                Foreground = System.Windows.Media.Brushes.White,
                Opacity = 0.85,
                FontSize = 11,
                MinWidth = 34,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Current zoom (Ctrl + mouse wheel also zooms)"
            };

            panel.Children.Add(Btn("", "Zoom out", f => StepZoom(f, false, grid)));
            panel.Children.Add(zoomLabel);
            panel.Children.Add(Btn("", "Zoom in", f => StepZoom(f, true, grid)));
            panel.Children.Add(Btn("", "Fit to frame", f => SetZoom(f, 0)));
            panel.Children.Add(Sep());
            panel.Children.Add(caption);
            panel.Children.Add(Sep());
            panel.Children.Add(Btn("", "Copy image to clipboard", f => CopyToClipboard(f)));
            panel.Children.Add(Btn("", "Save a copy as...", f => SaveAs(f)));
            panel.Children.Add(Btn("", "Replace image from file...", f =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico|All files|*.*" };
                if (dlg.ShowDialog() == true) SetFromFile(f, dlg.FileName);
            }));
            panel.Children.Add(Btn("", "Open in image editor (frame refreshes when you save)", f => OpenInEditor(f)));
            panel.Children.Add(Sep());
            panel.Children.Add(Btn("", "Clear image", f => ClearImage(f))); // separated from the everyday buttons

            var bar = new Border
            {
                Name = "ImgToolbar",
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x1E, 0x1F, 0x22)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 8),
                Opacity = 0.0,
                Visibility = Visibility.Collapsed,
                Child = panel
            };
            return bar;
        }

        /// <summary>Syncs the toolbar with the frame: availability (image present AND unlocked), the
        /// file caption, and the zoom label.</summary>
        private static void UpdateToolbar(dynamic frame, Grid grid)
        {
            var bar = grid?.Children.OfType<Border>().FirstOrDefault(b => b.Name == "ImgToolbar");
            if (bar == null) return;

            bool available = HasImage(frame) && !IsLocked(frame);
            bar.Tag = available ? TbAvailable : null;
            if (!available)
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                bar.Opacity = 0.0;
                bar.Visibility = Visibility.Collapsed;
            }
            else
            {
                bar.Visibility = Visibility.Visible; // opacity 0 until hover fades it in
            }

            if (bar.Child is StackPanel panel)
            {
                var caption = panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "ImgCaption");
                if (caption != null)
                {
                    // dynamic arg ⇒ dynamic return: cast to the concrete tuple before deconstructing.
                    (string Name, string FullPath) info = GetDisplayFileInfo(frame);
                    caption.Text = info.Name ?? "(pasted image)";
                    caption.ToolTip = info.FullPath;
                }
                var zl = panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "ImgZoomLabel");
                if (zl != null)
                {
                    int z = GetZoom(frame);
                    zl.Text = z == 0 ? "Fit" : $"{z}%";
                }
            }
        }

        private static void FadeToolbar(Grid grid, bool show)
        {
            var bar = grid?.Children.OfType<Border>().FirstOrDefault(b => b.Name == "ImgToolbar");
            if (bar == null || bar.Tag as string != TbAvailable) return;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = show ? 1.0 : 0.0,
                Duration = TimeSpan.FromMilliseconds(show ? 150 : 300)
            };
            bar.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static void OpenInEditor(dynamic frame)
        {
            try
            {
                string p = ResolvePath(frame);
                if (p == null || !File.Exists(p)) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { Verb = "edit", UseShellExecute = true });
                }
                catch
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mspaint.exe", $"\"{p}\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"OpenInEditor: {ex.Message}"); }
        }

        // ---- External-change watcher ----------------------------------------

        // frameId -> watcher on the current image file, so saving from an external editor refreshes the frame.
        private static readonly Dictionary<string, FileSystemWatcher> _watchers = new();

        private static void WatchFile(string frameId, string path)
        {
            if (string.IsNullOrEmpty(frameId)) return;
            if (_watchers.TryGetValue(frameId, out var old))
            {
                try { old.EnableRaisingEvents = false; old.Dispose(); } catch { }
                _watchers.Remove(frameId);
            }
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var w = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
                {
                    // Editors save in different ways: in-place write (Changed), or safe-save via a temp
                    // file that's renamed over the original (Created/Renamed). Watch for all of them.
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
                };

                // Debounce: editors raise several events per save (and the file can still be locked
                // mid-write) — reload once, shortly after the burst ends, retrying while the file is busy.
                System.Windows.Threading.DispatcherTimer reload = null;
                int attempts = 0;
                void Reload()
                {
                    dynamic live = LiveFrame(frameId);
                    if (live == null) { reload.Stop(); return; } // (dynamic condition can't share an out-var branch)
                    if (!_hosts.TryGetValue(frameId, out var g)) { reload.Stop(); return; }
                    var img = FindImage(g);
                    string p = ResolvePath(live);
                    if (img == null || p == null || !File.Exists(p)) { reload.Stop(); return; }
                    try
                    {
                        img.Source = LoadDisplayImage(p, DesiredDecodeWidth(live, g)); // IgnoreImageCache picks up the new bits
                        reload.Stop();
                    }
                    catch
                    {
                        // Still locked by the editor — retry a few times, then give up quietly.
                        if (++attempts >= 5) reload.Stop();
                    }
                }
                void Poke()
                {
                    try
                    {
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (reload == null)
                            {
                                reload = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                                reload.Tick += (ts, te) => Reload();
                            }
                            attempts = 0;
                            reload.Stop();
                            reload.Start(); // restart the debounce window on every event in the burst
                        }));
                    }
                    catch { }
                }
                w.Changed += (s, e) => Poke();
                w.Created += (s, e) => Poke();
                w.Renamed += (s, e) => Poke();
                w.EnableRaisingEvents = true;
                _watchers[frameId] = w;
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"WatchFile: {ex.Message}"); }
        }

        /// <summary>Reloads the displayed image for a frame (after set/clear).</summary>
        public static void Refresh(dynamic frame)
        {
            string frameId = frame.Id?.ToString();
            if (!string.IsNullOrEmpty(frameId) && _hosts.TryGetValue(frameId, out var grid)) LoadInto(frame, grid);
        }

        // ---- Setting the image ---------------------------------------------

        public static void SetFromBitmap(dynamic frame, BitmapSource bmp)
        {
            if (bmp == null) return;
            try
            {
                string file = $"image_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";
                string full = Path.Combine(GetAssetDir(frame.Id?.ToString()), file);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = new FileStream(full, FileMode.Create)) encoder.Save(fs);

                DeleteCurrentCopy(frame);
                SetKey(frame, "ImageFile", file);
                SetKey(frame, "ImageLinked", "false");
                SetKey(frame, "ImageSource", ""); // pasted — no source file name to show
                FrameDataManager.SaveFrameData();
                Refresh(frame);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"SetFromBitmap: {ex.Message}"); }
        }

        public static void SetFromFile(dynamic frame, string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath) || !IsImageFile(sourcePath)) return;

            string mode = (SettingsManager.ImageDropMode ?? "Copy").ToLower();
            if (mode == "ask")
            {
                var r = MessageBox.Show(
                    $"Add \"{Path.GetFileName(sourcePath)}\":\n\nYes = copy into the frame (portable)\nNo = link to the original file\nCancel = skip",
                    "Set image", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                mode = r == MessageBoxResult.No ? "reference" : "copy";
            }

            try
            {
                DeleteCurrentCopy(frame);
                if (mode == "reference")
                {
                    SetKey(frame, "ImageFile", sourcePath);
                    SetKey(frame, "ImageLinked", "true");
                }
                else // copy
                {
                    string file = $"image_{DateTime.Now:yyyyMMdd_HHmmssfff}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
                    string full = Path.Combine(GetAssetDir(frame.Id?.ToString()), file);
                    File.Copy(sourcePath, full, true);
                    SetKey(frame, "ImageFile", file);
                    SetKey(frame, "ImageLinked", "false");
                }
                SetKey(frame, "ImageSource", sourcePath); // remember the user-meaningful name/path for display
                FrameDataManager.SaveFrameData();
                Refresh(frame);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"SetFromFile: {ex.Message}"); }
        }

        public static void ClearImage(dynamic frame)
        {
            if (LockedGuard(frame)) return;
            DeleteCurrentCopy(frame);
            SetKey(frame, "ImageFile", "");
            SetKey(frame, "ImageLinked", "false");
            SetKey(frame, "ImageSource", "");
            FrameDataManager.SaveFrameData();
            Refresh(frame);
        }

        public static void CopyToClipboard(dynamic frame)
        {
            try { string p = ResolvePath(frame); if (p != null && File.Exists(p)) Clipboard.SetImage(LoadFull(p)); }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"copy image: {ex.Message}"); }
        }

        public static void SaveAs(dynamic frame)
        {
            try
            {
                string p = ResolvePath(frame);
                if (p == null || !File.Exists(p)) return;
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(p), Filter = "Image|*" + Path.GetExtension(p) + "|All files|*.*" };
                if (dlg.ShowDialog() == true) File.Copy(p, dlg.FileName, true);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"SaveAs: {ex.Message}"); }
        }

        // ---- Paste & Drop ---------------------------------------------------

        public static void HandlePaste(dynamic frame)
        {
            if (LockedGuard(frame)) return;
            try
            {
                if (Clipboard.ContainsImage()) { SetFromBitmap(frame, Clipboard.GetImage()); return; }
                if (Clipboard.ContainsFileDropList())
                {
                    string f = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile);
                    if (f != null) SetFromFile(frame, f);
                }
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"HandlePaste: {ex.Message}"); }
        }

        public static bool ClipboardHasImage()
        {
            try { return Clipboard.ContainsImage() || (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(IsImageFile)); }
            catch { return false; }
        }

        /// <summary>Routes a drop onto an image frame — the first image file, or a raw bitmap, becomes THE image.</summary>
        public static void HandleDrop(dynamic frame, DragEventArgs e)
        {
            if (LockedGuard(frame)) return;
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string f = ((string[])e.Data.GetData(DataFormats.FileDrop)).FirstOrDefault(IsImageFile);
                    if (f != null) SetFromFile(frame, f);
                    return;
                }
                if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                    SetFromBitmap(frame, bmp);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"HandleDrop: {ex.Message}"); }
        }

        private static bool LockedGuard(dynamic frame)
        {
            if (!IsLocked(frame)) return false;
            MessageBoxesManager.ShowOKOnlyMessageBoxForm(
                "This image frame is locked.\nRight-click the frame and uncheck \"Lock image\" to make changes.",
                "Image frame locked");
            return true;
        }

        // ---- Loading --------------------------------------------------------

        /// <summary>The decode width the frame actually needs right now. Fit mode decodes to roughly the
        /// frame's size (2x headroom for DPI scaling and moderate enlarging) instead of the global cap —
        /// a 4K screenshot in a 600px frame drops from ~14 MB of bitmap to ~2-3 MB. Zoom mode decodes at
        /// the cap so zoomed-in detail stays sharp.</summary>
        private static int DesiredDecodeWidth(dynamic frame, Grid grid)
        {
            if (GetZoom(frame) != 0) return DisplayCapWidth;
            double fw = 0;
            try { fw = grid?.ActualWidth ?? 0; } catch { }
            if (fw < 50) { try { fw = Convert.ToDouble(frame.Width?.ToString()); } catch { } }
            if (fw < 50) fw = 800;
            return (int)Math.Clamp(fw * 2.0, 640, DisplayCapWidth);
        }

        private static BitmapImage LoadDisplayImage(string path, int maxWidth = 0)
        {
            if (maxWidth <= 0) maxWidth = DisplayCapWidth;
            int decodeW = 0; // 0 = decode at native size
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    int nativeW = dec.Frames[0].PixelWidth;
                    if (nativeW > maxWidth) decodeW = maxWidth; // only downscale larger-than-needed images
                }
            }
            catch { }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;      // don't keep the file locked
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodeW > 0) bi.DecodePixelWidth = decodeW;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            if (bi.CanFreeze) bi.Freeze();
            return bi;
        }

        private static BitmapImage LoadFull(string path)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            if (bi.CanFreeze) bi.Freeze();
            return bi;
        }
    }
}
