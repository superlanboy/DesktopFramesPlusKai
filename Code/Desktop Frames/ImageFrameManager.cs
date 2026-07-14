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
    /// Image frames (ItemsType == "Image"): a container that holds one or more images added by paste,
    /// drag-drop, or (Phase 2) region screenshot, shown as a thumbnail flow. Content is locked by
    /// default (ContentLocked); unlocking allows add/remove. Images are stored as files in a managed
    /// per-frame asset folder and referenced from the frame's Items array.
    /// </summary>
    public static class ImageFramemanager
    {
        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" };

        private const int ThumbPx = 96;

        // ---- Asset storage --------------------------------------------------

        /// <summary>Per-frame image asset folder under the current profile (created on demand).</summary>
        public static string GetAssetDir(string frameId)
        {
            string dir = Path.Combine(ProfileManager.CurrentProfileDir, "ImageFrames", frameId ?? "unknown");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>Deletes a frame's whole asset folder (call when the frame itself is deleted).</summary>
        public static void DeleteAssetDir(string frameId)
        {
            try
            {
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
            try
            {
                if (frame is IDictionary<string, object> ed) ed["ContentLocked"] = locked ? "true" : "false";
                else if (frame is JObject jo) jo["ContentLocked"] = locked ? "true" : "false";
                FrameDataManager.SaveFrameData();
            }
            catch { }
        }

        // ---- Items helpers --------------------------------------------------

        private static JArray GetItems(dynamic frame)
        {
            try
            {
                var it = frame.Items;
                if (it is JArray ja) return ja;
                if (it is JToken jt && jt.Type == JTokenType.Array) return (JArray)jt;
            }
            catch { }
            var arr = new JArray();
            try
            {
                if (frame is IDictionary<string, object> ed) ed["Items"] = arr;
                else if (frame is JObject jo) jo["Items"] = arr;
            }
            catch { }
            return arr;
        }

        /// <summary>Resolves an item's on-disk path (copied files are stored as a bare filename).</summary>
        private static string ResolvePath(string frameId, JToken item)
        {
            string file = item?["Filename"]?.ToString() ?? "";
            bool linked = (item?["Linked"]?.ToString() ?? "false").ToLower() == "true";
            if (linked || Path.IsPathRooted(file)) return file;
            return Path.Combine(GetAssetDir(frameId), file);
        }

        private static bool IsImageFile(string path)
        {
            try { return ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()); }
            catch { return false; }
        }

        // ---- Rendering ------------------------------------------------------

        /// <summary>Rebuilds the thumbnail flow for an image frame from its persisted Items.</summary>
        public static void PopulateImages(dynamic frame, WrapPanel wpcont)
        {
            wpcont.Children.Clear();
            string frameId = frame.Id?.ToString();
            // NB: type as JArray explicitly — GetItems is called with a dynamic arg, so 'var' would be
            // 'dynamic' and LINQ extension methods (OfType) can't bind dynamically → RuntimeBinderException.
            JArray items = GetItems(frame);
            foreach (var item in items.OfType<JObject>().ToList())
            {
                string path = ResolvePath(frameId, item);
                if (!File.Exists(path)) continue; // skip missing (orphan handling)
                var thumb = BuildThumb(frame, wpcont, item);
                if (thumb != null) wpcont.Children.Add(thumb);
            }
        }

        private static FrameworkElement BuildThumb(dynamic frame, WrapPanel wpcont, JObject item)
        {
            string frameId = frame.Id?.ToString();
            string path = ResolvePath(frameId, item);
            BitmapImage src;
            try { src = LoadThumbnail(path, ThumbPx); }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"thumb load: {ex.Message}"); return null; }

            var img = new Image { Source = src, Width = ThumbPx, Height = ThumbPx, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var border = new Border
            {
                Margin = new Thickness(4),
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Child = img,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = item["DisplayName"]?.ToString() ?? Path.GetFileName(path)
            };
            border.Tag = item;

            // Double-click opens the image full size in the default viewer.
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"open image: {ex.Message}"); }
                    e.Handled = true;
                }
            };

            border.ContextMenu = BuildThumbMenu(frame, wpcont, border, item, path);
            return border;
        }

        private static ContextMenu BuildThumbMenu(dynamic frame, WrapPanel wpcont, Border thumb, JObject item, string path)
        {
            var cm = new ContextMenu();

            var miOpen = new MenuItem { Header = "Open" };
            miOpen.Click += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { } };
            cm.Items.Add(miOpen);

            var miCopy = new MenuItem { Header = "Copy image" };
            miCopy.Click += (s, e) =>
            {
                try { Clipboard.SetImage(LoadFull(path)); }
                catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"copy image: {ex.Message}"); }
            };
            cm.Items.Add(miCopy);

            var miSave = new MenuItem { Header = "Save as..." };
            miSave.Click += (s, e) => SaveAs(path);
            cm.Items.Add(miSave);

            cm.Items.Add(new Separator());

            var miDelete = new MenuItem { Header = "Delete" };
            miDelete.Click += (s, e) => RemoveItem(frame, wpcont, thumb, item);
            cm.Items.Add(miDelete);

            // Delete is only meaningful when unlocked.
            cm.Opened += (s, e) => { miDelete.IsEnabled = !IsLocked(frame); };

            DarkMenuTheme.Apply(cm);
            return cm;
        }

        // ---- Adding ---------------------------------------------------------

        /// <summary>Saves a bitmap (e.g. a clipboard paste or screenshot) into the frame and shows it.</summary>
        public static void AddBitmap(dynamic frame, WrapPanel wpcont, BitmapSource bmp, string displayName = null)
        {
            if (bmp == null) return;
            string frameId = frame.Id?.ToString();
            try
            {
                string file = $"img_{DateTimeStamp()}_{Guid.NewGuid().ToString("N").Substring(0, 6)}.png";
                string full = Path.Combine(GetAssetDir(frameId), file);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = new FileStream(full, FileMode.Create)) encoder.Save(fs);

                AppendItem(frame, wpcont, file, displayName ?? "Pasted image", linked: false);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"AddBitmap: {ex.Message}"); }
        }

        /// <summary>Adds an image file, honouring the global copy/reference/ask setting.</summary>
        public static void AddFile(dynamic frame, WrapPanel wpcont, string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath) || !IsImageFile(sourcePath)) return;
            string frameId = frame.Id?.ToString();

            string mode = (SettingsManager.ImageDropMode ?? "Copy").ToLower();
            if (mode == "ask")
            {
                var r = MessageBox.Show(
                    $"Add \"{Path.GetFileName(sourcePath)}\":\n\nYes = copy into the frame (portable)\nNo = link to the original file\nCancel = skip",
                    "Add image", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                mode = r == MessageBoxResult.No ? "reference" : "copy";
            }

            try
            {
                if (mode == "reference")
                {
                    AppendItem(frame, wpcont, sourcePath, Path.GetFileNameWithoutExtension(sourcePath), linked: true);
                }
                else // copy
                {
                    string file = $"img_{DateTimeStamp()}_{Guid.NewGuid().ToString("N").Substring(0, 6)}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
                    string full = Path.Combine(GetAssetDir(frameId), file);
                    File.Copy(sourcePath, full, true);
                    AppendItem(frame, wpcont, file, Path.GetFileNameWithoutExtension(sourcePath), linked: false);
                }
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"AddFile: {ex.Message}"); }
        }

        private static void AppendItem(dynamic frame, WrapPanel wpcont, string filename, string displayName, bool linked)
        {
            JArray items = GetItems(frame);
            var item = new JObject
            {
                ["Filename"] = filename,
                ["DisplayName"] = displayName,
                ["Linked"] = linked ? "true" : "false",
                ["IsImage"] = "true"
            };
            items.Add(item);
            FrameDataManager.SaveFrameData();

            var thumb = BuildThumb(frame, wpcont, item);
            if (thumb != null) wpcont.Children.Add(thumb);
        }

        private static void RemoveItem(dynamic frame, WrapPanel wpcont, Border thumb, JObject item)
        {
            if (IsLocked(frame)) return;
            string frameId = frame.Id?.ToString();
            try
            {
                bool linked = (item["Linked"]?.ToString() ?? "false").ToLower() == "true";
                string path = ResolvePath(frameId, item);
                item.Remove();               // drop from the Items array
                FrameDataManager.SaveFrameData();
                wpcont.Children.Remove(thumb);
                if (!linked && File.Exists(path)) { try { File.Delete(path); } catch { } } // delete copied asset
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.General, $"RemoveItem: {ex.Message}"); }
        }

        // ---- Paste & Drop ---------------------------------------------------

        /// <summary>Pastes an image (bitmap or image file list) from the clipboard into the frame.</summary>
        public static void HandlePaste(dynamic frame, WrapPanel wpcont)
        {
            if (LockedGuard(frame)) return;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    AddBitmap(frame, wpcont, Clipboard.GetImage());
                    return;
                }
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string f in Clipboard.GetFileDropList()) if (IsImageFile(f)) AddFile(frame, wpcont, f);
                }
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"HandlePaste: {ex.Message}"); }
        }

        public static bool ClipboardHasImage()
        {
            try { return Clipboard.ContainsImage() || (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(IsImageFile)); }
            catch { return false; }
        }

        /// <summary>Routes a drop onto an image frame: image files (copy/reference) and raw bitmaps.</summary>
        public static void HandleDrop(dynamic frame, WrapPanel wpcont, DragEventArgs e)
        {
            if (LockedGuard(frame)) return;
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    foreach (var f in files) if (IsImageFile(f)) AddFile(frame, wpcont, f);
                    return;
                }
                if (e.Data.GetDataPresent(DataFormats.Bitmap))
                {
                    if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp) AddBitmap(frame, wpcont, bmp, "Dropped image");
                }
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"HandleDrop: {ex.Message}"); }
        }

        private static bool LockedGuard(dynamic frame)
        {
            if (!IsLocked(frame)) return false;
            MessageBoxesManager.ShowOKOnlyMessageBoxForm(
                "This image frame is locked.\nRight-click the frame and choose \"Unlock images\" to make changes.",
                "Image frame locked");
            return true;
        }

        // ---- Utilities ------------------------------------------------------

        private static BitmapImage LoadThumbnail(string path, int px)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file locked
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.DecodePixelWidth = px;                    // decode at thumbnail size (memory-safe)
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

        private static void SaveAs(string sourcePath)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = Path.GetFileName(sourcePath),
                    Filter = "Image|*" + Path.GetExtension(sourcePath) + "|All files|*.*"
                };
                if (dlg.ShowDialog() == true) File.Copy(sourcePath, dlg.FileName, true);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"SaveAs: {ex.Message}"); }
        }

        // Date.Now is fine here (runtime action, not a workflow script).
        private static string DateTimeStamp() => DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
    }
}
