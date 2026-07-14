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
    /// like any frame — the image scales to fit. Content is locked by default (ContentLocked) to prevent
    /// accidental changes; unlocking lets you paste / drop / set / clear the image.
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
                if (!string.IsNullOrEmpty(frameId)) _hosts.Remove(frameId);
                string dir = Path.Combine(ProfileManager.CurrentProfileDir, "ImageFrames", frameId ?? "unknown");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"DeleteAssetDir: {ex.Message}"); }
        }

        // ---- Lock -----------------------------------------------------------

        public static bool IsLocked(dynamic frame)
        {
            try { return (frame.ContentLocked?.ToString() ?? "true").ToLower() != "false"; } catch { return true; }
        }

        public static void SetLocked(dynamic frame, bool locked)
        {
            SetKey(frame, "ContentLocked", locked ? "true" : "false");
            FrameDataManager.SaveFrameData();
            Refresh(frame); // update placeholder hint
        }

        // ---- Single-image state --------------------------------------------

        private static string GetFile(dynamic frame) { try { return frame.ImageFile?.ToString() ?? ""; } catch { return ""; } }
        private static bool GetLinked(dynamic frame) { try { return (frame.ImageLinked?.ToString() ?? "false").ToLower() == "true"; } catch { return false; } }

        public static bool HasImage(dynamic frame) => !string.IsNullOrEmpty(GetFile(frame));

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

        // ---- Content element ------------------------------------------------

        /// <summary>Builds the image frame's content (single Image + placeholder) and hosts it in the DockPanel.</summary>
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

            grid.Children.Add(placeholder);
            grid.Children.Add(img);
            dp.Children.Add(grid);

            if (!string.IsNullOrEmpty(frameId)) _hosts[frameId] = grid;
            LoadInto(frame, grid);
            return grid;
        }

        private static void LoadInto(dynamic frame, Grid grid)
        {
            if (grid == null) return;
            var placeholder = grid.Children.OfType<TextBlock>().FirstOrDefault();
            var img = grid.Children.OfType<Image>().FirstOrDefault();
            if (img == null) return;

            string path = ResolvePath(frame);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { img.Source = LoadDisplayImage(path); img.Visibility = Visibility.Visible; if (placeholder != null) placeholder.Visibility = Visibility.Collapsed; return; }
                catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"image load: {ex.Message}"); }
            }

            img.Source = null;
            img.Visibility = Visibility.Collapsed;
            if (placeholder != null)
            {
                placeholder.Text = IsLocked(frame) ? "No image\n(unlock the frame to add one)" : "Drag or paste an image here";
                placeholder.Visibility = Visibility.Visible;
            }
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

        private static BitmapImage LoadDisplayImage(string path)
        {
            int decodeW = 0; // 0 = decode at native size
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    int nativeW = dec.Frames[0].PixelWidth;
                    if (nativeW > DisplayCapWidth) decodeW = DisplayCapWidth; // only downscale huge images
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
