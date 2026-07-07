using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using IWshRuntimeLibrary;
using Newtonsoft.Json.Linq;
using Microsoft.VisualBasic;

namespace Desktop_Frames
{
    public class PortalFramemanager
    {
        // New field for the active filter
        private string _currentFilter = null;
        private int _sortMode = 0; // 0=Name, 1=Date Modified, 2=Type, 3=Size
        private bool _sortAsc = true; // icon-view sort direction (asc = A-Z / oldest / smallest)
        private TextBlock _sortHeader; // icon-view "Sorted by ..." heading (null for non-portal)


        private readonly dynamic _frame;
        private readonly WrapPanel _wpcont;
        private readonly FileSystemWatcher _watcher;
        private string _targetFolderPath;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _debounceTimer;
        private int _navigationGeneration = 0; // Tracks active navigation to prevent thread collisions
        // O(1) dedup of paths currently shown in the icon WrapPanel — avoids an O(n^2) scan when
        // populating large folders (which could hang/crash the app).
        private readonly HashSet<string> _uiPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxIconViewItems = 5000; // safety cap so a huge folder can't exhaust memory

        // --- Details View state ---
        private readonly ListView _detailsView;              // null-safe: only set for portal frames
        private ScrollViewer _iconScrollViewer;              // the icon view host (parent of _wpcont)
        private readonly ObservableCollection<PortalRow> _rows = new ObservableCollection<PortalRow>();
        private string _viewMode = "Icons";                  // "Icons" | "Details"
        private GridViewColumn[] _columns;
        private GridView _gridView;                           // the details GridView (for themed headers)
        private DispatcherTimer _columnSaveTimer;
        private static readonly double[] DefaultColumnWidths = { 200, 130, 130, 90 };

        // Themed header/scrollbar chrome, rebuilt only when the frame's text colour changes.
        private System.Windows.Media.Color _chromeColor;
        private bool _chromeApplied;
        private ResourceDictionary _chromeResources;

        // --- Details sort/group state ---
        private System.ComponentModel.ICollectionView _rowsView;
        private string _groupMode = "None";      // None | Name | Type | Date | Size
        private string _sortColumn = null;        // null | Name | Date | Type | Size
        private bool _sortAscending = true;
        // Column display labels and their matching sort keys (index-aligned with _columns).
        private static readonly string[] _colLabels = { "Name", "Date modified", "Type", "Size" };
        private static readonly string[] _colKeys = { "Name", "Date", "Type", "Size" };

        /// <summary>Row model backing the Details ListView.</summary>
        public class PortalRow
        {
            public ImageSource Icon { get; set; }
            public string Name { get; set; }
            public string DateModified { get; set; }   // display string
            public string Type { get; set; }
            public string SizeText { get; set; }        // display string
            public string FilePath { get; set; }
            public bool IsFolder { get; set; }
            public DateTime DateValue { get; set; }     // typed key for sort/group
            public long SizeValue { get; set; }         // typed key for sort/group (-1 for folders)
        }


        // --- FILTERING ENGINE START ---

        /// <summary>
        /// Updates the current filter and refreshes the visibility of all items.
        /// Publicly called by Framemanager when the user types in the filter bar.
        /// </summary>
        public void ApplyFilter(string filterText)
        {
            _currentFilter = filterText;
            _dispatcher.Invoke(() =>
            {
                foreach (StackPanel sp in _wpcont.Children.OfType<StackPanel>())
                {
                    if (sp.Tag != null)
                    {
                        // Safely retrieve path from anonymous type or object
                        string path = sp.Tag.GetType().GetProperty("FilePath")?.GetValue(sp.Tag)?.ToString();
                        if (!string.IsNullOrEmpty(path))
                        {
                            sp.Visibility = ShouldShowItem(path) ? Visibility.Visible : Visibility.Collapsed;
                        }
                    }
                }

                if (_viewMode == "Details") SyncDetailsView();
            });
        }



        /// <summary>
        /// Determines if a file should be visible based on the current filter.
        /// Supports "Smart Match" if NoWildcardsOnPortalFilter is enabled.
        /// </summary>
        private bool ShouldShowItem(string filePath)
        {
            if (string.IsNullOrWhiteSpace(_currentFilter)) return true;

            string fileName = System.IO.Path.GetFileName(filePath);
            var terms = _currentFilter.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(t => t.Trim())
                                      .ToList();

            bool hasIncludeRules = terms.Any(t => !t.StartsWith(">"));
            bool matchesInclude = !hasIncludeRules;
            bool matchesExclude = false;

            foreach (var term in terms)
            {
                if (string.IsNullOrEmpty(term)) continue;

                string pattern = term;
                bool isExclude = false;

                // 1. Identify Exclusion
                if (pattern.StartsWith(">"))
                {
                    isExclude = true;
                    pattern = pattern.Substring(1); // Remove '>' prefix
                }

                // 2. Apply Smart Wildcards (Hidden Option)
                // Logic: If user wants "No Wildcards", we treat text as "Contains".
                // We only auto-wrap if the user hasn't typed wildcards themselves.
                if (SettingsManager.NoWildcardsOnPortalFilter)
                {
                    if (!pattern.Contains("*") && !pattern.Contains("?"))
                    {
                        pattern = "*" + pattern + "*";
                    }
                }

                // 3. Match
                if (isExclude)
                {
                    if (IsMatch(fileName, pattern))
                    {
                        matchesExclude = true;
                        break; // Hard fail
                    }
                }
                else
                {
                    if (IsMatch(fileName, pattern))
                    {
                        matchesInclude = true;
                    }
                }
            }

            return !matchesExclude && matchesInclude;
        }




        /// <summary>
        /// Simple glob matching (* and ?)
        /// </summary>
        private bool IsMatch(string text, string pattern)
        {
            // Use VB's Like operator or simple Regex. 
            // For a dependency-free C# solution, we convert glob to regex.
            try
            {
                string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                                      .Replace(@"\*", ".*")
                                      .Replace(@"\?", ".") + "$";
                return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }
        // --- FILTERING ENGINE END ---


        // --- SORTING ENGINE START ---
        public string CycleSortMode()
        {
            _sortMode++;
            if (_sortMode > 3) _sortMode = 0;

            // Save state using the existing updater
            Framemanager.UpdateFrameProperty(_frame, "SortMode", _sortMode.ToString(), "Updated portal sort mode");

            string[] modeNames = { "Name", "Date Modified", "Type", "Size" };
            string activeMode = modeNames[_sortMode];

            LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.General, $"Portal frame sorted by: {activeMode}");

            SortContents();

            return activeMode;
        }

        /// <summary>Current icon-view sort mode (0=Name, 1=Date, 2=Type, 3=Size).</summary>
        public int GetSortMode() => _sortMode;

        /// <summary>Current icon-view sort direction (true = ascending).</summary>
        public bool GetSortAscending() => _sortAsc;

        /// <summary>Sets the icon-view sort mode explicitly (from the Sort-by menu) and re-sorts.</summary>
        public void SetSortMode(int mode)
        {
            _sortMode = Math.Max(0, Math.Min(3, mode));
            Framemanager.UpdateFrameProperty(_frame, "SortMode", _sortMode.ToString(), "Updated portal sort mode");
            SortContents();
        }

        /// <summary>Sets the icon-view sort direction (from the Sort-by menu) and re-sorts.</summary>
        public void SetSortAscending(bool asc)
        {
            _sortAsc = asc;
            Framemanager.UpdateFrameProperty(_frame, "SortAsc", _sortAsc ? "true" : "false", "Updated portal sort direction");
            SortContents();
        }

        private static readonly string[] _sortModeNames = { "Name", "Date modified", "Type", "Size" };

        /// <summary>Updates the icon-view "Sorted by ..." heading and shows it only in icon mode.</summary>
        private void UpdateSortHeader()
        {
            if (_sortHeader == null) return;
            _sortHeader.Text = $"Sorted by {_sortModeNames[Math.Max(0, Math.Min(3, _sortMode))]}  {(_sortAsc ? "▲" : "▼")}";
            _sortHeader.Visibility = _viewMode == "Details" ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SortContents()
        {
            _dispatcher.Invoke(() =>
            {
                UpdateSortHeader();
                var children = _wpcont.Children.OfType<StackPanel>().ToList();
                if (children.Count == 0) return;

                _wpcont.Children.Clear();

                string GetPath(StackPanel sp)
                {
                    dynamic tag = sp.Tag;
                    return tag?.GetType().GetProperty("FilePath")?.GetValue(tag)?.ToString() ?? "";
                }

                bool IsFolder(StackPanel sp)
                {
                    dynamic tag = sp.Tag;
                    return tag != null && tag.GetType().GetProperty("IsFolder")?.GetValue(tag) as bool? == true;
                }

                // Folders always first; the chosen key then sorts asc/desc within each group.
                var ordered = children.OrderByDescending(IsFolder);
                IOrderedEnumerable<StackPanel> sorted;
                switch (_sortMode)
                {
                    case 1: // Date modified
                    {
                        DateTime Key(StackPanel sp) { try { return System.IO.File.GetLastWriteTime(GetPath(sp)); } catch { return DateTime.MinValue; } }
                        sorted = _sortAsc ? ordered.ThenBy(Key) : ordered.ThenByDescending(Key);
                        break;
                    }
                    case 2: // Type
                    {
                        string Key(StackPanel sp) => System.IO.Path.GetExtension(GetPath(sp))?.ToLower() ?? "";
                        sorted = _sortAsc ? ordered.ThenBy(Key) : ordered.ThenByDescending(Key);
                        break;
                    }
                    case 3: // Size
                    {
                        long Key(StackPanel sp) { try { return IsFolder(sp) ? 0 : new System.IO.FileInfo(GetPath(sp)).Length; } catch { return 0; } }
                        sorted = _sortAsc ? ordered.ThenBy(Key) : ordered.ThenByDescending(Key);
                        break;
                    }
                    default: // 0 = Name
                    {
                        string Key(StackPanel sp) => System.IO.Path.GetFileName(GetPath(sp))?.ToLower() ?? "";
                        sorted = _sortAsc ? ordered.ThenBy(Key) : ordered.ThenByDescending(Key);
                        break;
                    }
                }

                foreach (var sp in sorted)
                {
                    _wpcont.Children.Add(sp);
                }

                if (_viewMode == "Details") SyncDetailsView();
            });
        }
        // --- SORTING ENGINE END ---


        public PortalFramemanager(dynamic frame, WrapPanel wpcont, ListView detailsView = null, TextBlock sortHeader = null)
        {
            _frame = frame;
            _wpcont = wpcont;
            _dispatcher = _wpcont.Dispatcher;
            _detailsView = detailsView;
            _sortHeader = sortHeader;
            // Icon view host = the ScrollViewer sharing the content Grid with the details ListView.
            _iconScrollViewer = (_detailsView?.Parent as Grid)?.Children.OfType<ScrollViewer>().FirstOrDefault()
                                ?? _wpcont.Parent as ScrollViewer;

            // Initialize debounce timer with longer interval for Excel temp files
            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500) // Increased for better stability
            };
            _debounceTimer.Tick += ProcessPendingEvents;

            // Extract folder path
            IDictionary<string, object> frameDict = frame is IDictionary<string, object> dict ? dict : ((JObject)frame).ToObject<IDictionary<string, object>>();
            _targetFolderPath = frameDict.ContainsKey("Path") ? frameDict["Path"]?.ToString() : null;

            // FIX: Load saved filter immediately on startup
            if (frameDict.ContainsKey("FilterString"))
            {
                _currentFilter = frameDict["FilterString"]?.ToString();
            }

            // NEW: Load saved sort mode
            if (frameDict.ContainsKey("SortMode"))
            {
                _sortMode = Convert.ToInt32(frameDict["SortMode"]?.ToString() ?? "0");
            }
            if (frameDict.ContainsKey("SortAsc"))
            {
                _sortAsc = (frameDict["SortAsc"]?.ToString() ?? "true").ToLower() != "false";
            }

            // NEW: Load saved view mode + per-frame column widths (Details view)
            if (frameDict.ContainsKey("ViewMode") && frameDict["ViewMode"]?.ToString() == "Details")
            {
                _viewMode = "Details";
            }
            double[] savedWidths = ParseColumnWidths(frameDict.ContainsKey("ColumnWidths") ? frameDict["ColumnWidths"]?.ToString() : null);

            if (frameDict.ContainsKey("DetailsGroup"))
            {
                string g = frameDict["DetailsGroup"]?.ToString();
                if (!string.IsNullOrWhiteSpace(g)) _groupMode = g;
            }
            if (frameDict.ContainsKey("DetailsSort"))
            {
                string ds = frameDict["DetailsSort"]?.ToString();
                if (!string.IsNullOrWhiteSpace(ds) && ds.Contains(":"))
                {
                    var parts = ds.Split(':');
                    _sortColumn = parts[0];
                    _sortAscending = parts.Length < 2 || parts[1] != "desc";
                }
            }

            if (_detailsView != null)
            {
                BuildDetailsView(savedWidths);
                ApplyViewModeVisibility();
            }

            if (string.IsNullOrEmpty(_targetFolderPath))
            {
                throw new Exception("No folder path defined for Portal Frame. Please recreate the frame.");
            }

            if (!Directory.Exists(_targetFolderPath))
            {
                throw new Exception($"The folder '{_targetFolderPath}' does not exist. Please update the Portal Frame settings.");
            }

            _watcher = new FileSystemWatcher(_targetFolderPath)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
                InternalBufferSize = 65536 // --- BUG FIX: Maximize buffer to survive massive I/O operations ---
            };

            // --- BUG FIX: Simplified "State Reconciler" Trigger ---
            // The watcher is now just a "ping" to tell us something changed. 
            // We listen to the Error event to specifically catch buffer overflows!
            _watcher.Created += (s, e) => TriggerSync();
            _watcher.Deleted += (s, e) => TriggerSync();
            _watcher.Renamed += (s, e) => TriggerSync();
            _watcher.Error += (s, e) => TriggerSync();

            InitializeFrameContents();
            //  // --- TEST CODE START ---
            //  // Hardcode a filter to prove the engine works.
            //   // This simulates a user typing "*.txt" into the filter bar.
            //   ApplyFilter("*.txt");
            //  // --- TEST CODE END ---
        }

        private void TriggerSync(bool immediate = false)
        {
            _dispatcher.InvokeAsync(() =>
            {
                _debounceTimer.Stop();
                if (immediate)
                {
                    _ = RunReconcilerAsync();
                }
                else
                {
                    _debounceTimer.Start();
                }
            });
        }

        private void ProcessPendingEvents(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            _ = RunReconcilerAsync();
        }

        private async System.Threading.Tasks.Task RunReconcilerAsync()
        {
            int myGeneration = ++_navigationGeneration;
            string targetPath = _targetFolderPath;

            try
            {
                if (!Directory.Exists(targetPath)) return;

                // 1. Read Disk & UI State (Background Thread)
                var diff = await System.Threading.Tasks.Task.Run(() =>
                {
                    // --- PERFORMANCE FIX: Use EnumerateFileSystemInfos to avoid N+1 disk I/O calls ---
                    // This fetches attributes instantly during the directory scan instead of hitting the disk for every single file.
                    var currentDiskFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var dirInfo = new DirectoryInfo(targetPath);
                        foreach (var fsi in dirInfo.EnumerateFileSystemInfos())
                        {
                            if (CoreUtilities.IsTemporaryFile(fsi.FullName)) continue;

                            // Attributes are pre-cached in 'fsi', ZERO extra disk I/O required!
                            if ((fsi.Attributes & FileAttributes.Hidden) == 0 &&
                                (fsi.Attributes & FileAttributes.System) == 0)
                            {
                                currentDiskFiles.Add(fsi.FullName);
                            }
                        }
                    }
                    catch { } // Handle access denied gracefully

                    // Snapshot of what's currently shown (O(1) set, no per-item reflection).
                    List<string> currentUIFiles = new List<string>();
                    _dispatcher.Invoke(() => { currentUIFiles = _uiPaths.ToList(); });

                    var uiSet = new HashSet<string>(currentUIFiles, StringComparer.OrdinalIgnoreCase);
                    return new
                    {
                        ToRemove = currentUIFiles.Where(p => !currentDiskFiles.Contains(p)).ToList(),
                        ToAdd = currentDiskFiles.Where(p => !uiSet.Contains(p)).ToList()
                    };
                });

                // Abort if the user navigated away while we were scanning!
                if (myGeneration != _navigationGeneration) return;

                // 2. Remove old icons instantly
                if (diff.ToRemove.Count > 0)
                {
                    _dispatcher.Invoke(() =>
                    {
                        foreach (var path in diff.ToRemove) RemoveIcon(path);
                        if (_viewMode == "Details") SyncDetailsView();
                    });
                }

                if (myGeneration != _navigationGeneration) return;

                // 3. Add new icons (SMOOTH CHUNKING)
                // Instead of locking the UI thread to load 100 icons at once, we yield to the Background priority.
                // This keeps the app responsive during massive folder loads and avoids freezing.
                if (diff.ToAdd.Count > 0)
                {
                    // Safety cap: the icon view isn't virtualized, so bound how many visuals we build.
                    var toAdd = diff.ToAdd;
                    if (_uiPaths.Count + toAdd.Count > MaxIconViewItems)
                    {
                        int room = Math.Max(0, MaxIconViewItems - _uiPaths.Count);
                        LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.General,
                            $"Portal folder has more than {MaxIconViewItems} items; showing the first {room} in icon view (use Details view for very large folders).");
                        toAdd = toAdd.GetRange(0, room);
                    }

                    // PERF: add icons in chunks per dispatcher pass instead of one round-trip
                    // per item. Cuts scheduling overhead dramatically while still yielding
                    // between chunks (Background priority) to keep the UI responsive.
                    const int CHUNK = 25;
                    for (int i = 0; i < toAdd.Count; i += CHUNK)
                    {
                        if (myGeneration != _navigationGeneration) break;

                        var batch = toAdd.GetRange(i, Math.Min(CHUNK, toAdd.Count - i));
                        await _dispatcher.InvokeAsync(() =>
                        {
                            foreach (var path in batch)
                            {
                                if (myGeneration != _navigationGeneration) break;
                                AddIcon(path);
                            }
                        }, DispatcherPriority.Background);
                    }

                    if (myGeneration == _navigationGeneration)
                    {
                        _dispatcher.Invoke(() => SortContents());
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"Portal Sync Error: {ex.Message}");
            }
        }

        private void AddIcon(string path)
        {



            // Enhanced filter during add to prevent duplicates
            FileAttributes attributes;
            bool isFolder = false;

            try
            {
                // --- PERFORMANCE FIX: 1 Disk Read instead of 4 ---
                // We grab attributes once. This immediately tells us if it exists, is a folder, and if it's hidden/system.
                attributes = System.IO.File.GetAttributes(path);
                isFolder = (attributes & FileAttributes.Directory) == FileAttributes.Directory;

                if (CoreUtilities.IsTemporaryFile(path)) return;
                if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden) return;
                if ((attributes & FileAttributes.System) == FileAttributes.System) return;

                // Already shown? O(1) check (previously an O(n) reflection scan -> O(n^2) for big folders).
                if (_uiPaths.Contains(path)) return;
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Inaccessible or missing item {path}: {ex.Message}");
                return;
            }

            dynamic icon = new System.Dynamic.ExpandoObject();
            IDictionary<string, object> iconDict = icon;
            iconDict["Filename"] = path;
            iconDict["IsFolder"] = isFolder;





            // --- RESTORED: Network Path Detection ---
            iconDict["IsNetwork"] = Framemanager.IsNetworkPath(path);


            string displayName;

            try
            {
                // FIX: Handle Extensions based on Global Setting
                if (SettingsManager.ShowPortalExtensions && !isFolder)
                {
                    // Force display name WITH extension
                    displayName = Path.GetFileName(path);
                }
                else
                {
                    if (isFolder)
                    {
                        // Folders → keep full name even if they contain dots
                        displayName = Path.GetFileName(path);
                    }
                    else
                    {
                        // Files → strip extension (default behavior)
                        displayName = Path.GetFileNameWithoutExtension(path);
                    }
                }
            }
            catch
            {
                // Fallback: act like it's a file without extension
                displayName = Path.GetFileNameWithoutExtension(path);
            }

            iconDict["DisplayName"] = displayName;

            // --- FIX: ONE CALL ONLY ---
            // We use the new signature that passes '_frame' context.
            // This applies the custom settings (Size, Color, etc.) immediately.
            Framemanager.AddIcon(icon, _wpcont, _frame);

            // Now we grab the StackPanel that was just added to attach logic
            StackPanel sp = _wpcont.Children[_wpcont.Children.Count - 1] as StackPanel;
            if (sp != null)
            {
                _uiPaths.Add(path); // track for O(1) dedup

                // FIX: Apply filter immediately upon creation
                sp.Visibility = ShouldShowItem(path) ? Visibility.Visible : Visibility.Collapsed;

                Framemanager.ClickEventAdder(sp, path, Directory.Exists(path));

                // Right-click shows the SAME native Windows shell menu as the Details view (themed in
                // dark mode, full Windows options) — not the old app-specific WPF menu. No per-icon
                // ContextMenu is allocated; the shell menu is built on demand.
                sp.PreviewMouseRightButtonUp += (s, e) =>
                {
                    ShowShellMenuForPath(path);
                    e.Handled = true; // suppress the frame's context menu for an item right-click
                };
            }
        }

        /// <summary>Shows the native Windows shell context menu for a path (shared by icon + details views).</summary>
        private void ShowShellMenuForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var win = Window.GetWindow(_wpcont);
            IntPtr hwnd = win != null ? new System.Windows.Interop.WindowInteropHelper(win).Handle : IntPtr.Zero;
            var src = hwnd != IntPtr.Zero ? System.Windows.Interop.HwndSource.FromHwnd(hwnd) : null;
            var pos = System.Windows.Forms.Cursor.Position;
            bool ext = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            ShellContextMenu.ShowForPath(path, hwnd, src, pos.X, pos.Y, ext);
        }

        /// <summary>Builds the per-item right-click menu on demand (lazy, for load performance).</summary>
        private void PopulatePortalItemMenu(ContextMenu contextMenu, string path, StackPanel sp)
        {
            // 1. Copy Item (File Object)
            MenuItem copyFileItem = new MenuItem { Header = "Copy Item" };
            copyFileItem.Click += (s, e) =>
            {
                try
                {
                    var paths = new System.Collections.Specialized.StringCollection();
                    paths.Add(path);
                    Clipboard.SetFileDropList(paths);
                    LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.UI, $"Copied item to clipboard: {path}");
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.UI, $"Error copying item: {ex.Message}");
                }
            };
            contextMenu.Items.Add(copyFileItem);

            // 2. Cut Item (File Object with Move Effect)
            MenuItem cutFileItem = new MenuItem { Header = "Cut Item" };
            cutFileItem.Click += (s, e) =>
            {
                try
                {
                    var paths = new System.Collections.Specialized.StringCollection();
                    paths.Add(path);

                    DataObject data = new DataObject();
                    data.SetFileDropList(paths);

                    // "Preferred DropEffect" = Move (2) so Explorer moves on paste
                    byte[] moveEffect = new byte[] { 2, 0, 0, 0 };
                    System.IO.MemoryStream stream = new System.IO.MemoryStream(moveEffect);
                    data.SetData("Preferred DropEffect", stream);

                    Clipboard.SetDataObject(data, true);
                    LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.UI, $"Cut item to clipboard: {path}");
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.UI, $"Error cutting item: {ex.Message}");
                }
            };
            contextMenu.Items.Add(cutFileItem);

            // 3. Rename item
            MenuItem renameItem = new MenuItem { Header = "Rename item" };
            renameItem.Click += (s, e) => RenameItem(path, sp);
            contextMenu.Items.Add(renameItem);

            // 4. Delete item
            MenuItem deleteItem = new MenuItem { Header = "Delete item" };
            deleteItem.Click += (s, e) => DeleteItem(path, sp);
            contextMenu.Items.Add(deleteItem);

            // 5. Separator
            contextMenu.Items.Add(new Separator());

            // 6. Copy path
            MenuItem copyPathItem = new MenuItem { Header = "Copy path" };
            copyPathItem.Click += (s, e) => CopyPathOrTarget(path);
            contextMenu.Items.Add(copyPathItem);
        }

        private void RenameItem(string currentPath, StackPanel sp)
        {
            try
            {
                string currentName = Path.GetFileNameWithoutExtension(currentPath);
                string extension = Path.GetExtension(currentPath);

                // Simple input dialog (you can replace with a proper dialog if you have one)
                string newName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter new name:",
                    "Rename Item",
                    currentName);

                if (string.IsNullOrEmpty(newName) || newName == currentName)
                    return;

                string newPath = Path.Combine(Path.GetDirectoryName(currentPath), newName + extension);

                // Check if target name already exists
                if (System.IO.File.Exists(newPath) || Directory.Exists(newPath))
                {
                    MessageBoxesManager.ShowOKOnlyMessageBoxForm("A file or folder with that name already exists.", "Rename Error");
                    return;
                }

                // Perform the rename
                if (Directory.Exists(currentPath))
                {
                    Directory.Move(currentPath, newPath);
                }
                else if (System.IO.File.Exists(currentPath))
                {
                    System.IO.File.Move(currentPath, newPath);
                }

                LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.General, $"Renamed {currentPath} to {newPath}");

                // The FileSystemWatcher will automatically handle UI updates
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"Failed to rename {currentPath}: {ex.Message}");
                MessageBoxesManager.ShowOKOnlyMessageBoxForm($"Failed to rename item: {ex.Message}", "Rename Error");
            }
        }

        private void InitializeFrameContents()
        {
            _dispatcher.Invoke(() => _wpcont.Children.Clear());

            // --- NAVIGATION LAG FIX ---
            // Pass 'true' to completely bypass the FileWatcher's 500ms debounce timer.
            // This guarantees the folder begins loading instantly upon click.
            TriggerSync(immediate: true);

            LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Requested immediate async initialization for {_targetFolderPath}");
        }

        private void CopyPathOrTarget(string path)
        {
            try
            {
                string pathToCopy;
                if (Path.GetExtension(path).ToLower() == ".lnk")
                {
                    // If it's a shortcut, get the target path
                    WshShell shell = new WshShell();
                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(path);
                    pathToCopy = shortcut.TargetPath;
                }
                else
                {
                    // Otherwise, copy the folder path (portal frame path)
                    pathToCopy = Path.GetDirectoryName(path); // Gets the parent directory
                }

                // Copy to clipboard
                Clipboard.SetText(pathToCopy);
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, $"Copied path to clipboard: {pathToCopy}");
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.UI, $"Failed to copy path for {path}: {ex.Message}");
                MessageBoxesManager.ShowOKOnlyMessageBoxForm($"Unable to copy path.", "Error");
            }
        }

        private void DeleteItem(string path, StackPanel sp)
        {
            bool UseRecycleBin = SettingsManager.UseRecycleBin;
            if (UseRecycleBin == true)
            {
                try
                {
                    // First, check if the item exists
                    if (!Directory.Exists(path) && !System.IO.File.Exists(path))
                    {
                        LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Item not found for deletion: {path}");
                        return;
                    }

                    // Use SHFileOperation to move to recycle bin
                    SHFILEOPSTRUCT shf = new SHFILEOPSTRUCT();
                    shf.wFunc = FO_DELETE;
                    shf.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION;
                    shf.pFrom = path + '\0' + '\0'; // Double null-terminated string

                    int result = SHFileOperation(ref shf);

                    if (result != 0)
                    {
                        throw new Exception($"Failed to move to recycle bin (error code: {result})");
                    }

                    LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.UI, $"Moved to recycle bin: {path}");

                    // Remove the icon from the UI
                    _wpcont.Children.Remove(sp);
                    _uiPaths.Remove(path);
                    LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, ($"Removed icon for {path} from UI"));
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"Failed to move item {path} to recycle bin: {ex.Message}");
                    MessageBoxesManager.ShowOKOnlyMessageBoxForm($"Unable to move item to recycle bin.", "Error");
                }
            }
            else
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        // Delete folder
                        Directory.Delete(path, true); // true for recursive deletion
                        LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Deleted folder: {path}");
                    }
                    else if (System.IO.File.Exists(path))
                    {
                        // Delete file
                        System.IO.File.Delete(path);
                        LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Deleted file: {path}");
                    }
                    else
                    {
                        LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Item not found for deletion: {path}");
                        return;
                    }

                    // Remove the icon from the UI
                    _wpcont.Children.Remove(sp);
                    _uiPaths.Remove(path);
                    LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Removed icon for {path} from UI");
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Failed to delete item {path}: {ex.Message}");
                    MessageBoxesManager.ShowOKOnlyMessageBoxForm($"Unable to delete item.", "Error");
                }
            }
        }

        // Corrected Win32 API declarations
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation([In] ref SHFILEOPSTRUCT lpFileOp);

        const uint FO_DELETE = 0x0003;
        const ushort FOF_ALLOWUNDO = 0x0040;
        const ushort FOF_NOCONFIRMATION = 0x0010;

        private void RemoveIcon(string path)
        {
            var sp = _wpcont.Children.OfType<StackPanel>().FirstOrDefault(s =>
            {
                string p = s.Tag?.GetType().GetProperty("FilePath")?.GetValue(s.Tag)?.ToString();
                return string.Equals(p, path, StringComparison.OrdinalIgnoreCase);
            });

            if (sp != null)
            {
                _wpcont.Children.Remove(sp);
                _uiPaths.Remove(path);
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Successfully removed icon for {path}");
            }
            else
            {
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"Failed to find StackPanel for {path} in RemoveIcon");
            }
        }
        // TEST: Filter for only text files (REMOVE AFTER TEST)
        // ApplyFilter("*.txt");



        /// <summary>
        /// Safely switches the monitored folder without destroying the frame window.
        /// Used for the "Dive In" navigation feature.
        /// </summary>
        public void NavigateTo(string newPath)
        {
            try
            {
                if (string.IsNullOrEmpty(newPath) || !Directory.Exists(newPath))
                {
                    LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.General, $"Cannot navigate to invalid path: {newPath}");
                    return;
                }

                // 1. Suspend Watcher to prevent event spam during switch
                bool wasEnable = _watcher.EnableRaisingEvents;
                _watcher.EnableRaisingEvents = false;

                // 2. Clear UI
                _dispatcher.Invoke(() => { _wpcont.Children.Clear(); _uiPaths.Clear(); });

                // 3. Switch Target
                _targetFolderPath = newPath;
                _watcher.Path = newPath; // FileSystemWatcher supports dynamic path changing

                // 4. Reload Content
                InitializeFrameContents();

                // 5. Resume Watcher
                _watcher.EnableRaisingEvents = wasEnable;

                LogManager.Log(LogManager.LogLevel.Info, LogManager.LogCategory.General, $"Portal Frame navigated to: {newPath}");
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"Navigation failed: {ex.Message}");
                MessageBoxesManager.ShowOKOnlyMessageBoxForm($"Could not navigate to folder.\n{ex.Message}", "Navigation Error");
            }
        }

        // ============================ DETAILS VIEW ENGINE ============================

        private double[] ParseColumnWidths(string saved)
        {
            var result = (double[])DefaultColumnWidths.Clone();
            if (string.IsNullOrWhiteSpace(saved)) return result;
            var parts = saved.Split('|');
            for (int i = 0; i < result.Length && i < parts.Length; i++)
            {
                if (double.TryParse(parts[i], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double w) && w > 20 && w < 2000)
                    result[i] = w;
            }
            return result;
        }

        private void BuildDetailsView(double[] widths)
        {
            _detailsView.ItemsSource = _rows;
            _detailsView.SelectionMode = SelectionMode.Extended;
            _detailsView.AlternationCount = 2; // zebra striping (colour applied in ApplyThemedChrome)

            GridView gv = new GridView();

            GridViewColumn nameCol = new GridViewColumn { Header = "Name", Width = widths[0], CellTemplate = BuildNameCellTemplate() };
            GridViewColumn dateCol = new GridViewColumn { Header = "Date modified", Width = widths[1], DisplayMemberBinding = new Binding("DateModified") };
            GridViewColumn typeCol = new GridViewColumn { Header = "Type", Width = widths[2], DisplayMemberBinding = new Binding("Type") };
            GridViewColumn sizeCol = new GridViewColumn { Header = "Size", Width = widths[3], DisplayMemberBinding = new Binding("SizeText") };

            gv.Columns.Add(nameCol);
            gv.Columns.Add(dateCol);
            gv.Columns.Add(typeCol);
            gv.Columns.Add(sizeCol);

            _columns = new[] { nameCol, dateCol, typeCol, sizeCol };

            // Compact, Explorer-style (left-aligned) column headers — keeps the header bar tight
            // against the title bar instead of the tall, centred default.
            var headerStyle = new Style(typeof(System.Windows.Controls.GridViewColumnHeader));
            headerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 22.0));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            headerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            gv.ColumnHeaderContainerStyle = headerStyle;

            _gridView = gv;
            _detailsView.View = gv;

            // Persist column widths on resize (debounced so we don't hammer the JSON file during a drag).
            _columnSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _columnSaveTimer.Tick += (s, e) => { _columnSaveTimer.Stop(); SaveColumnWidths(); };

            var dpd = DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
            foreach (var col in _columns)
                dpd.AddValueChanged(col, (s, e) => { _columnSaveTimer.Stop(); _columnSaveTimer.Start(); });

            _detailsView.MouseDoubleClick += DetailsView_MouseDoubleClick;
            _detailsView.ContextMenu = BuildRowContextMenu();
            // Native Windows shell menu on row right-click (Open with, SVN, copy/paste, etc.);
            // the WPF ContextMenu above only shows for right-clicks on empty list space.
            _detailsView.PreviewMouseRightButtonUp += DetailsView_RightClick;
            // Explorer-style: left-click on empty list space clears the selection.
            _detailsView.PreviewMouseLeftButtonDown += DetailsView_LeftClick;

            // Explorer-style sort + group operate over the collection view of _rows.
            _rowsView = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);

            // Group header styling (bold group name).
            GroupStyle gs = new GroupStyle();
            var headerFactory = new FrameworkElementFactory(typeof(TextBlock));
            headerFactory.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            headerFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            headerFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 6, 0, 2));
            gs.HeaderTemplate = new DataTemplate { VisualTree = headerFactory };
            _detailsView.GroupStyle.Add(gs);

            // Click a column header to sort by that column (toggles direction).
            _detailsView.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnDetailsHeaderClick));

            RefreshViewSortGroup();
        }

        private static string Hex(System.Windows.Media.Color c, byte a) =>
            $"#{a:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>
        /// Restyles the column headers and scrollbars to blend with the frame's text colour instead
        /// of the stark default light chrome. Derives every colour from <paramref name="c"/> (the
        /// frame's text colour), so it contrasts with the frame background by construction. Cached;
        /// only rebuilt when the colour actually changes.
        /// </summary>
        private void ApplyThemedChrome(System.Windows.Media.Color c)
        {
            if (_gridView == null) return;
            if (_chromeApplied && c == _chromeColor) return;
            try
            {
                string fg = Hex(c, 0xFF);
                string subtle = Hex(c, 0x33);   // header borders / separators
                string hover = Hex(c, 0x22);    // header hover
                string hdrBg = Hex(c, 0x14);    // faint header bar tint
                string thumb = Hex(c, 0x66);    // scrollbar thumb
                string stripe = Hex(c, 0x16);   // alternating-row tint
                string sel = Hex(c, 0x40);      // selected-row highlight

                string xaml = ChromeXaml
                    .Replace("%FG%", fg).Replace("%SUBTLE%", subtle).Replace("%HOVER%", hover)
                    .Replace("%HDRBG%", hdrBg).Replace("%THUMB%", thumb).Replace("%STRIPE%", stripe)
                    .Replace("%SEL%", sel);

                var dict = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);

                // Column headers.
                if (dict["ThemedHeader"] is Style hs) _gridView.ColumnHeaderContainerStyle = hs;

                // Scrollbars: swap the previous chrome dict (implicit ScrollBar style cascades to the
                // ListView's internal scrollbars).
                if (_chromeResources != null)
                    _detailsView.Resources.MergedDictionaries.Remove(_chromeResources);
                _detailsView.Resources.MergedDictionaries.Add(dict);
                _chromeResources = dict;

                _chromeColor = c;
                _chromeApplied = true;
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.UI, $"ApplyThemedChrome failed: {ex.Message}");
            }
        }

        // Themed header + slim overlay scrollbars. Colours are injected as %TOKENS% from the frame's
        // text colour. The header template keeps PART_HeaderGripper so column resizing still works.
        private const string ChromeXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <!-- Full row template so selection/hover cover the whole row uniformly (the default template left
       the selection looking half-shaded over a striped row). Zebra = odd rows tinted; selection and
       hover override it. Striping is toggled by setting the ListView's AlternationCount to 2 or 0. -->
  <Style TargetType='ListViewItem'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Foreground' Value='%FG%'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListViewItem'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' Padding='0,1,0,1' SnapsToDevicePixels='True'>
            <GridViewRowPresenter VerticalAlignment='Center' Content='{TemplateBinding Content}'
                Columns='{Binding Path=View.Columns, RelativeSource={RelativeSource AncestorType={x:Type ListView}}}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='ItemsControl.AlternationIndex' Value='1'>
              <Setter TargetName='bd' Property='Background' Value='%STRIPE%'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='%HOVER%'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='%SEL%'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='ThemedHeader' TargetType='GridViewColumnHeader'>
    <Setter Property='Foreground' Value='%FG%'/>
    <Setter Property='Background' Value='%HDRBG%'/>
    <Setter Property='Height' Value='22'/>
    <Setter Property='Padding' Value='8,0,8,0'/>
    <Setter Property='FontWeight' Value='Normal'/>
    <Setter Property='HorizontalContentAlignment' Value='Left'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='GridViewColumnHeader'>
          <Grid>
            <Border x:Name='hb' Background='{TemplateBinding Background}' BorderBrush='%SUBTLE%' BorderThickness='0,0,1,1' Padding='{TemplateBinding Padding}'>
              <ContentPresenter HorizontalAlignment='Left' VerticalAlignment='Center' RecognizesAccessKey='True'/>
            </Border>
            <Thumb x:Name='PART_HeaderGripper' HorizontalAlignment='Right' Width='8' Cursor='SizeWE'>
              <Thumb.Template>
                <ControlTemplate TargetType='Thumb'><Border Background='Transparent'/></ControlTemplate>
              </Thumb.Template>
            </Thumb>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='hb' Property='Background' Value='%HOVER%'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ScrollBar'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='10'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb MinHeight='24'>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Border CornerRadius='4' Background='%THUMB%' Margin='2,0,2,0'/>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='Height' Value='10'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
              <Grid Background='Transparent'>
                <Track x:Name='PART_Track'>
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
                  </Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
                  </Track.IncreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb MinWidth='24'>
                      <Thumb.Template>
                        <ControlTemplate TargetType='Thumb'>
                          <Border CornerRadius='4' Background='%THUMB%' Margin='0,2,0,2'/>
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>";

        private DataTemplate BuildNameCellTemplate()
        {
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var img = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
            img.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding("Icon"));
            img.SetValue(System.Windows.Controls.Image.WidthProperty, 16.0);
            img.SetValue(System.Windows.Controls.Image.HeightProperty, 16.0);
            img.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);

            var txt = new FrameworkElementFactory(typeof(TextBlock));
            txt.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            txt.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            sp.AppendChild(img);
            sp.AppendChild(txt);

            return new DataTemplate { VisualTree = sp };
        }

        private ContextMenu BuildRowContextMenu()
        {
            ContextMenu cm = new ContextMenu();

            // File operations (Open, Open with, Cut/Copy/Paste, Delete, Rename, Properties, and shell
            // extensions like TortoiseSVN) come from the NATIVE shell menu on row right-click. This WPF
            // menu only carries the view options, shown when right-clicking empty space in the list.

            // Sort by (also available by clicking a column header)
            MenuItem sortMenu = new MenuItem { Header = "Sort by" };
            foreach (var pair in new[] { ("Name", "Name"), ("Date modified", "Date"), ("Type", "Type"), ("Size", "Size") })
            {
                string k = pair.Item2;
                MenuItem mi = new MenuItem { Header = pair.Item1 };
                mi.Click += (s, e) => { _sortColumn = k; RefreshViewSortGroup(); PersistSortGroup(); };
                sortMenu.Items.Add(mi);
            }
            sortMenu.Items.Add(new Separator());
            MenuItem ascItem = new MenuItem { Header = "Ascending" };
            ascItem.Click += (s, e) => { _sortAscending = true; if (_sortColumn == null) _sortColumn = "Name"; RefreshViewSortGroup(); PersistSortGroup(); };
            MenuItem descItem = new MenuItem { Header = "Descending" };
            descItem.Click += (s, e) => { _sortAscending = false; if (_sortColumn == null) _sortColumn = "Name"; RefreshViewSortGroup(); PersistSortGroup(); };
            sortMenu.Items.Add(ascItem);
            sortMenu.Items.Add(descItem);
            cm.Items.Add(sortMenu);

            // Group by
            MenuItem groupMenu = new MenuItem { Header = "Group by" };
            foreach (var pair in new[] { ("(None)", "None"), ("Name", "Name"), ("Type", "Type"), ("Date modified", "Date"), ("Size", "Size") })
            {
                string k = pair.Item2;
                MenuItem mi = new MenuItem { Header = pair.Item1 };
                mi.Click += (s, e) => { _groupMode = k; RefreshViewSortGroup(); PersistSortGroup(); };
                groupMenu.Items.Add(mi);
            }
            cm.Items.Add(groupMenu);

            ApplyDarkMenuThemeIfNeeded(cm);
            return cm;
        }

        /// <summary>Applies a dark style to a WPF ContextMenu when Windows is in dark mode.</summary>
        private void ApplyDarkMenuThemeIfNeeded(ContextMenu cm) => DarkMenuTheme.Apply(cm);

        /// <summary>Right-click a row → native Windows shell context menu for that file/folder.</summary>
        private void DetailsView_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var row = FindAncestorListViewItem(e.OriginalSource as DependencyObject);
            if (row?.DataContext is PortalRow pr && !string.IsNullOrEmpty(pr.FilePath))
            {
                // Explorer-style: right-click SELECTS ONLY this row (replaces any prior
                // selection). Without the Clear(), Extended mode keeps ADDING each
                // right-clicked row to the selection, leaving stale grey highlights behind.
                _detailsView.SelectedItems.Clear();
                row.IsSelected = true;
                var win = Window.GetWindow(_detailsView);
                IntPtr hwnd = win != null ? new System.Windows.Interop.WindowInteropHelper(win).Handle : IntPtr.Zero;
                var src = hwnd != IntPtr.Zero ? System.Windows.Interop.HwndSource.FromHwnd(hwnd) : null;
                var pos = System.Windows.Forms.Cursor.Position;
                bool ext = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

                ShellContextMenu.ShowForPath(pr.FilePath, hwnd, src, pos.X, pos.Y, ext);
                e.Handled = true; // suppress the WPF (Sort/Group) menu for a row click
            }
        }

        /// <summary>Left-click on empty list space (not a row, not a column header) clears the selection.</summary>
        private void DetailsView_LeftClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            if (FindAncestorListViewItem(src) == null && FindAncestorHeader(src) == null)
                _detailsView.SelectedItems.Clear();
        }

        private static System.Windows.Controls.GridViewColumnHeader FindAncestorHeader(DependencyObject src)
        {
            while (src != null && !(src is System.Windows.Controls.GridViewColumnHeader))
            {
                if (src is System.Windows.Media.Visual || src is System.Windows.Media.Media3D.Visual3D)
                    src = System.Windows.Media.VisualTreeHelper.GetParent(src);
                else
                    src = LogicalTreeHelper.GetParent(src);
            }
            return src as System.Windows.Controls.GridViewColumnHeader;
        }

        private static ListViewItem FindAncestorListViewItem(DependencyObject src)
        {
            while (src != null && !(src is ListViewItem))
            {
                if (src is System.Windows.Media.Visual || src is System.Windows.Media.Media3D.Visual3D)
                    src = System.Windows.Media.VisualTreeHelper.GetParent(src);
                else
                    src = LogicalTreeHelper.GetParent(src);
            }
            return src as ListViewItem;
        }

        private void ApplyViewModeVisibility()
        {
            if (_detailsView == null) return;
            bool details = _viewMode == "Details";
            if (_iconScrollViewer != null) _iconScrollViewer.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
            _detailsView.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
            UpdateSortHeader(); // show the sort heading only in icon mode
        }

        /// <summary>Switches this portal frame between "Icons" and "Details" and persists the choice.</summary>
        public void SetViewMode(string mode)
        {
            if (_detailsView == null) return;
            _viewMode = mode == "Details" ? "Details" : "Icons";
            Framemanager.UpdateFrameProperty(_frame, "ViewMode", _viewMode, "Updated portal view mode");
            _dispatcher.Invoke(() =>
            {
                ApplyViewModeVisibility();
                if (_viewMode == "Details") SyncDetailsView();
            });
        }

        private StackPanel FindStackPanelByPath(string path)
        {
            return _wpcont.Children.OfType<StackPanel>().FirstOrDefault(sp =>
                string.Equals(sp.Tag?.GetType().GetProperty("FilePath")?.GetValue(sp.Tag)?.ToString(), path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Returns the current (possibly re-serialized) frame data, since UpdateFrameProperty
        /// replaces the JObject in FrameData and the constructor-captured _frame can go stale.</summary>
        private dynamic GetLiveFrame()
        {
            try
            {
                string id = _frame.Id?.ToString();
                if (!string.IsNullOrEmpty(id))
                {
                    var live = FrameDataManager.FrameData.FirstOrDefault(f => f.Id?.ToString() == id);
                    if (live != null) return live;
                }
            }
            catch { }
            return _frame;
        }

        /// <summary>Public hook: re-apply shared styling + refresh rows (called after Customize changes).</summary>
        public void RefreshDetails()
        {
            if (_detailsView == null || _viewMode != "Details") return;
            _dispatcher.Invoke(() => SyncDetailsView());
        }

        private void DetailsView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_detailsView.SelectedItem is PortalRow row) OpenRow(row);
        }

        private void OpenRow(PortalRow row)
        {
            try
            {
                if (row.IsFolder)
                {
                    // Open the folder in Windows Explorer (no in-frame navigation).
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.FilePath) { UseShellExecute = true });
                }
                else
                {
                    StackPanel sp = FindStackPanelByPath(row.FilePath);
                    if (sp != null)
                        Framemanager.LaunchItemFromExternal(sp, row.FilePath, false, null);
                    else
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.FilePath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.General, $"Details view open failed for {row.FilePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the Details ListView rows from the (already filtered + sorted) icon StackPanels,
        /// which remain the single source of truth. No-op unless we're in Details mode.
        /// Must be called on the UI thread.
        /// </summary>
        private void SyncDetailsView()
        {
            if (_detailsView == null || _viewMode != "Details") return;

            // Share the frame's per-icon customization with the Details view (text color + grayscale).
            dynamic liveFrame = GetLiveFrame();
            Brush textBrush = null;
            try
            {
                string tc = liveFrame.TextColor?.ToString();
                if (!string.IsNullOrWhiteSpace(tc)) textBrush = new SolidColorBrush(Utility.GetColorFromName(tc));
            }
            catch { }
            _detailsView.Foreground = textBrush ?? System.Windows.Media.Brushes.White;

            // Blend the column headers + scrollbars with the frame's text colour instead of the
            // stark default light chrome.
            ApplyThemedChrome((textBrush as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.White);

            // Zebra striping: per-frame override (DetailsStriped = On/Off) else the global default.
            bool striped = SettingsManager.PortalDetailsStriped;
            try { string ov = liveFrame.DetailsStriped?.ToString(); if (ov == "On") striped = true; else if (ov == "Off") striped = false; } catch { }
            _detailsView.AlternationCount = striped ? 2 : 0;

            bool grayscale = false;
            try { grayscale = liveFrame.GrayscaleIcons?.ToString().ToLower() == "true"; } catch { }

            _rows.Clear();
            foreach (StackPanel sp in _wpcont.Children.OfType<StackPanel>())
            {
                if (sp.Visibility != Visibility.Visible) continue; // respect the active filter
                string path = sp.Tag?.GetType().GetProperty("FilePath")?.GetValue(sp.Tag)?.ToString();
                if (string.IsNullOrEmpty(path)) continue;

                bool isFolder = false;
                try { isFolder = (System.IO.File.GetAttributes(path) & FileAttributes.Directory) == FileAttributes.Directory; }
                catch { }

                string dateStr = "";
                string sizeStr = "";
                DateTime dateVal = DateTime.MinValue;
                long sizeVal = isFolder ? -1 : 0;
                try
                {
                    if (isFolder)
                    {
                        dateVal = new DirectoryInfo(path).LastWriteTime;
                        dateStr = dateVal.ToString("g");
                    }
                    else
                    {
                        var fi = new FileInfo(path);
                        dateVal = fi.LastWriteTime;
                        dateStr = dateVal.ToString("g");
                        sizeVal = fi.Length;
                        sizeStr = Utility.FormatFileSize(sizeVal);
                    }
                }
                catch { }

                ImageSource iconSrc = Utility.GetShellIcon(path, isFolder);
                if (grayscale && iconSrc is System.Windows.Media.Imaging.BitmapSource bsrc)
                {
                    var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        bsrc, System.Windows.Media.PixelFormats.Gray8, System.Windows.Media.Imaging.BitmapPalettes.Gray256, 0);
                    if (conv.CanFreeze) conv.Freeze();
                    iconSrc = conv;
                }

                _rows.Add(new PortalRow
                {
                    Icon = iconSrc,
                    Name = Path.GetFileName(path),
                    DateModified = dateStr,
                    Type = Utility.GetShellTypeName(path, isFolder),
                    SizeText = sizeStr,
                    FilePath = path,
                    IsFolder = isFolder,
                    DateValue = dateVal,
                    SizeValue = sizeVal
                });
            }
        }

        private void SaveColumnWidths()
        {
            if (_columns == null) return;
            try
            {
                string joined = string.Join("|", _columns.Select(c =>
                {
                    double w = c.ActualWidth > 0 ? c.ActualWidth : c.Width;
                    return ((int)Math.Round(w)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }));
                Framemanager.UpdateFrameProperty(_frame, "ColumnWidths", joined, "Updated portal column widths");
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Debug, LogManager.LogCategory.General, $"SaveColumnWidths failed: {ex.Message}");
            }
        }

        /// <summary>Applies the current group + sort selections to the collection view.</summary>
        private void RefreshViewSortGroup()
        {
            if (_rowsView == null) return;
            using (_rowsView.DeferRefresh())
            {
                _rowsView.GroupDescriptions.Clear();
                _rowsView.SortDescriptions.Clear();

                string groupProp = null;
                System.Windows.Data.PropertyGroupDescription gd = null;
                switch (_groupMode)
                {
                    case "Name": gd = new System.Windows.Data.PropertyGroupDescription("Name", new FirstLetterConverter()); groupProp = "Name"; break;
                    case "Type": gd = new System.Windows.Data.PropertyGroupDescription("Type"); groupProp = "Type"; break;
                    case "Date": gd = new System.Windows.Data.PropertyGroupDescription("DateValue", new DateBucketConverter()); groupProp = "DateValue"; break;
                    case "Size": gd = new System.Windows.Data.PropertyGroupDescription("SizeValue", new SizeBucketConverter()); groupProp = "SizeValue"; break;
                }
                if (gd != null)
                {
                    _rowsView.GroupDescriptions.Add(gd);
                    // Keep groups contiguous by sorting on the group key first.
                    var gdir = (_groupMode == "Date" || _groupMode == "Size")
                        ? System.ComponentModel.ListSortDirection.Descending
                        : System.ComponentModel.ListSortDirection.Ascending;
                    _rowsView.SortDescriptions.Add(new System.ComponentModel.SortDescription(groupProp, gdir));
                }

                if (_sortColumn != null)
                {
                    string prop = _sortColumn switch { "Date" => "DateValue", "Type" => "Type", "Size" => "SizeValue", _ => "Name" };
                    _rowsView.SortDescriptions.Add(new System.ComponentModel.SortDescription("IsFolder", System.ComponentModel.ListSortDirection.Descending));
                    _rowsView.SortDescriptions.Add(new System.ComponentModel.SortDescription(prop,
                        _sortAscending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending));
                }
            }

            UpdateSortIndicators();
        }

        private void OnDetailsHeaderClick(object sender, RoutedEventArgs e)
        {
            var header = e.OriginalSource as System.Windows.Controls.GridViewColumnHeader;
            if (header?.Column == null || _columns == null) return;

            // Map by column identity (not header text, which now carries the sort glyph).
            int idx = Array.IndexOf(_columns, header.Column);
            if (idx < 0) return;
            string mapped = _colKeys[idx];

            if (_sortColumn == mapped) _sortAscending = !_sortAscending;
            else { _sortColumn = mapped; _sortAscending = true; }

            RefreshViewSortGroup();
            PersistSortGroup();
        }

        /// <summary>Shows a ▲/▼ glyph on the actively-sorted column header, Explorer-style.</summary>
        private void UpdateSortIndicators()
        {
            if (_columns == null) return;
            for (int i = 0; i < _columns.Length && i < _colLabels.Length; i++)
            {
                string label = _colLabels[i];
                if (_sortColumn == _colKeys[i])
                    label += _sortAscending ? "  ▲" : "  ▼";
                _columns[i].Header = label;
            }
        }

        private void PersistSortGroup()
        {
            try
            {
                string sortStr = _sortColumn == null ? "" : $"{_sortColumn}:{(_sortAscending ? "asc" : "desc")}";
                Framemanager.UpdateFrameProperty(_frame, "DetailsSort", sortStr, "Updated portal details sort");
                Framemanager.UpdateFrameProperty(_frame, "DetailsGroup", _groupMode, "Updated portal details group");
            }
            catch { }
        }

        // --- Group-key converters (bucket files the Explorer way) ---
        private class FirstLetterConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                string s = value?.ToString();
                if (string.IsNullOrEmpty(s)) return "#";
                char ch = char.ToUpperInvariant(s[0]);
                return char.IsLetter(ch) ? ch.ToString() : "#";
            }
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
        }

        private class DateBucketConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is DateTime dt && dt != DateTime.MinValue)
                {
                    DateTime today = DateTime.Today;
                    DateTime d = dt.Date;
                    if (d == today) return "Today";
                    if (d == today.AddDays(-1)) return "Yesterday";
                    if (d > today.AddDays(-7)) return "Earlier this week";
                    if (d > today.AddDays(-30)) return "Earlier this month";
                    if (d.Year == today.Year) return "Earlier this year";
                    return "Long ago";
                }
                return "Unknown";
            }
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
        }

        private class SizeBucketConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                long size = value is long l ? l : 0;
                if (size < 0) return "Folders";
                if (size == 0) return "0 KB";
                if (size < 16L * 1024) return "Tiny (0 - 16 KB)";
                if (size < 1L * 1024 * 1024) return "Small (16 KB - 1 MB)";
                if (size < 128L * 1024 * 1024) return "Medium (1 - 128 MB)";
                if (size < 1024L * 1024 * 1024) return "Large (128 MB - 1 GB)";
                return "Huge (> 1 GB)";
            }
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _debounceTimer?.Stop();
            _debounceTimer.Tick -= ProcessPendingEvents;
            _columnSaveTimer?.Stop();
            if (_detailsView != null)
            {
                _detailsView.MouseDoubleClick -= DetailsView_MouseDoubleClick;
                _detailsView.PreviewMouseRightButtonUp -= DetailsView_RightClick;
                _detailsView.PreviewMouseLeftButtonDown -= DetailsView_LeftClick;
                _detailsView.RemoveHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnDetailsHeaderClick));
            }
        }
    }
}