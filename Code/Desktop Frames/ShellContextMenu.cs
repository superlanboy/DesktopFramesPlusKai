using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Desktop_Frames
{
    /// <summary>
    /// Shows the native Windows Explorer shell context menu for a single file/folder — including
    /// third-party shell extensions (TortoiseSVN, 7-Zip, etc.), "Open with", "Send to", Cut/Copy/
    /// Paste, Properties. Used by the Portal Details view.
    /// </summary>
    public static class ShellContextMenu
    {
        #region COM interfaces
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
        private interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214e4-0000-0000-c000-000000000046")]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, System.Text.StringBuilder commandString, int cchMax);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214f4-0000-0000-c000-000000000046")]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, System.Text.StringBuilder commandString, int cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719")]
        private interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, System.Text.StringBuilder commandString, int cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }
        #endregion

        [StructLayout(LayoutKind.Sequential)]
        private struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public int fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
            public IntPtr lpVerbW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
            public POINT ptInvoke;
        }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

        [DllImport("shell32.dll")] private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);
        [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
        [DllImport("shell32.dll")] private static extern void ILFree(IntPtr pidl);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")] private static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        // Dark mode for classic menus — undocumented uxtheme ordinals (widely used); all guarded.
        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)] private static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
        [DllImport("uxtheme.dll", EntryPoint = "#133")] private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint MF_POPUP = 0x0010;
        private const uint MF_GRAYED = 0x0001;
        private const uint MF_BYPOSITION = 0x0400;
        private const int CUSTOM_BASE = 0x8000;   // ids for our simplified top-level items
        private static bool _darkModeInit;

        private static Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        private static Guid IID_IContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");

        private const int idCmdFirst = 1;
        private const int idCmdLast = 0x7FFF;
        private const uint CMF_EXPLORE = 0x00000004;
        private const uint CMF_EXTENDEDVERBS = 0x00000100;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const int CMIC_MASK_UNICODE = 0x00004000;
        private const int SW_SHOWNORMAL = 1;

        private const int WM_INITMENUPOPUP = 0x0117;
        private const int WM_DRAWITEM = 0x002B;
        private const int WM_MEASUREITEM = 0x002C;
        private const int WM_MENUCHAR = 0x0120;

        // Active IContextMenu2/3 while a menu is up, so the owner-window hook can forward messages.
        private static IContextMenu2 _com2;
        private static IContextMenu3 _com3;

        /// <summary>
        /// Shows the native shell context menu for <paramref name="path"/> at the given screen point.
        /// <paramref name="extended"/> = true shows the "extended" (Shift) verbs.
        /// </summary>
        public static void ShowForPath(string path, IntPtr ownerHwnd, HwndSource ownerSource, int screenX, int screenY, bool extended)
        {
            if (string.IsNullOrEmpty(path)) return;

            IntPtr pidl = IntPtr.Zero, parentPtr = IntPtr.Zero, childPidl = IntPtr.Zero, ctxPtr = IntPtr.Zero, hMenu = IntPtr.Zero, hSub = IntPtr.Zero;
            IShellFolder parent = null;
            IContextMenu com1 = null;
            HwndSourceHook hook = null;

            try
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;

                Guid gFolder = IID_IShellFolder;
                if (SHBindToParent(pidl, ref gFolder, out parentPtr, out childPidl) != 0 || parentPtr == IntPtr.Zero) return;
                parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);

                Guid gCtx = IID_IContextMenu;
                IntPtr[] apidl = { childPidl };
                if (parent.GetUIObjectOf(ownerHwnd, 1, apidl, ref gCtx, IntPtr.Zero, out ctxPtr) != 0 || ctxPtr == IntPtr.Zero) return;
                com1 = (IContextMenu)Marshal.GetObjectForIUnknown(ctxPtr);

                try { _com3 = (IContextMenu3)com1; } catch { _com3 = null; }
                if (_com3 == null) { try { _com2 = (IContextMenu2)com1; } catch { _com2 = null; } }

                hMenu = CreatePopupMenu();

                // Simplified top level: the most common operations (our own ids, CUSTOM_BASE range).
                AppendMenu(hMenu, MF_STRING, (UIntPtr)(CUSTOM_BASE + 1), "Open");
                AppendMenu(hMenu, MF_STRING, (UIntPtr)(CUSTOM_BASE + 2), "Edit");
                AppendMenu(hMenu, MF_STRING, (UIntPtr)(CUSTOM_BASE + 3), "Open with...");
                AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(hMenu, MF_STRING, (UIntPtr)(CUSTOM_BASE + 4), "Cut");
                AppendMenu(hMenu, MF_STRING, (UIntPtr)(CUSTOM_BASE + 5), "Copy");
                AppendMenu(hMenu, MF_STRING, (UIntPtr)(CUSTOM_BASE + 6), "Paste");
                AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(hMenu, MF_STRING, (UIntPtr)(CUSTOM_BASE + 7), "Properties");
                AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);

                // Full native Explorer menu, tucked into a submenu. This is populated LAZILY: the
                // QueryContextMenu below is the slow step — it makes every registered shell extension
                // run its per-file logic (TortoiseSVN status, cloud sync state, ...), file-specific
                // I/O the pre-warm can't cache. Most right-clicks never open this submenu, so we defer
                // the query until the user actually opens it (see the WM_INITMENUPOPUP hook).
                hSub = CreatePopupMenu();
                uint qFlags = CMF_EXPLORE | (extended ? CMF_EXTENDEDVERBS : 0);
                bool subPopulated = false;
                if (ownerSource != null)
                {
                    // Placeholder keeps the submenu arrow and makes Windows fire WM_INITMENUPOPUP so
                    // we can fill it on demand.
                    AppendMenu(hSub, MF_STRING | MF_GRAYED, (UIntPtr)(CUSTOM_BASE + 99), "Loading…");
                }
                else
                {
                    // No message hook available (can't lazy-populate) → fall back to eager fill.
                    com1.QueryContextMenu(hSub, 0, idCmdFirst, idCmdLast, qFlags);
                    subPopulated = true;
                }
                AppendMenu(hMenu, MF_POPUP, (UIntPtr)(ulong)hSub.ToInt64(), "All Windows options");

                // Follow the OS light/dark setting for the (classic) menu.
                ApplyDarkMenus(ownerHwnd);

                // Hook the owner window's messages to (a) lazily fill "All Windows options" the first
                // time it opens and (b) forward menu messages to IContextMenu2/3 so submenus
                // (Open with / Send to) and owner-drawn extension items render and work correctly.
                if (ownerSource != null)
                {
                    hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                    {
                        if (msg == WM_INITMENUPOPUP && wp == hSub && !subPopulated)
                        {
                            subPopulated = true;
                            try
                            {
                                RemoveMenu(hSub, 0, MF_BYPOSITION); // drop the "Loading…" placeholder
                                com1.QueryContextMenu(hSub, 0, idCmdFirst, idCmdLast, qFlags);
                                if (IsSystemDark()) FlushMenuThemes(); // dark-theme the just-added items
                            }
                            catch { }
                        }

                        if (_com3 != null && (msg == WM_INITMENUPOPUP || msg == WM_DRAWITEM || msg == WM_MEASUREITEM || msg == WM_MENUCHAR))
                        {
                            if (_com3.HandleMenuMsg2((uint)msg, wp, lp, out IntPtr res) == 0) { handled = true; return res; }
                        }
                        else if (_com2 != null && (msg == WM_INITMENUPOPUP || msg == WM_DRAWITEM || msg == WM_MEASUREITEM))
                        {
                            if (_com2.HandleMenuMsg((uint)msg, wp, lp) == 0) { handled = true; return IntPtr.Zero; }
                        }
                        return IntPtr.Zero;
                    };
                    ownerSource.AddHook(hook);
                }

                uint cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, ownerHwnd, IntPtr.Zero);

                if (hook != null && ownerSource != null) { ownerSource.RemoveHook(hook); hook = null; }

                if (cmd >= CUSTOM_BASE)
                {
                    // A top-level verb was picked without ever opening "All Windows options", so
                    // com1 was never queried. Build its verb table now (into a scratch menu) before
                    // invoking — the cost lands on click, not on menu display, so no display jitter.
                    if (!subPopulated)
                    {
                        IntPtr scratch = CreatePopupMenu();
                        try { com1.QueryContextMenu(scratch, 0, idCmdFirst, idCmdLast, qFlags); } catch { }
                        finally { if (scratch != IntPtr.Zero) DestroyMenu(scratch); }
                        subPopulated = true;
                    }

                    // One of our simplified top-level items -> invoke the corresponding shell verb.
                    switch (cmd - CUSTOM_BASE)
                    {
                        case 1: InvokeShellVerb(com1, "open", ownerHwnd); break;
                        case 2: InvokeShellVerb(com1, "edit", ownerHwnd); break;
                        case 3: InvokeShellVerb(com1, "openas", ownerHwnd); break;
                        case 4: InvokeShellVerb(com1, "cut", ownerHwnd); break;
                        case 5: InvokeShellVerb(com1, "copy", ownerHwnd); break;
                        case 6: InvokeVerbForPath(System.IO.Path.GetDirectoryName(path), "paste", ownerHwnd); break;
                        case 7: InvokeShellVerb(com1, "properties", ownerHwnd); break;
                    }
                }
                else if (cmd >= idCmdFirst)
                {
                    // An item from the full "All Windows options" submenu.
                    var ici = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                        fMask = CMIC_MASK_UNICODE,
                        hwnd = ownerHwnd,
                        lpVerb = (IntPtr)(cmd - idCmdFirst),
                        lpVerbW = (IntPtr)(cmd - idCmdFirst),
                        nShow = SW_SHOWNORMAL
                    };
                    com1.InvokeCommand(ref ici);
                }
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Error, LogManager.LogCategory.UI, $"ShellContextMenu error: {ex.Message}");
            }
            finally
            {
                if (hook != null && ownerSource != null) ownerSource.RemoveHook(hook);
                _com2 = null; _com3 = null;
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
                if (com1 != null) Marshal.ReleaseComObject(com1);
                if (parent != null) Marshal.ReleaseComObject(parent);
                if (ctxPtr != IntPtr.Zero) Marshal.Release(ctxPtr);
                if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
                if (pidl != IntPtr.Zero) ILFree(pidl);
            }
        }

        /// <summary>
        /// Loads the registered shell context-menu handlers for <paramref name="path"/> into the
        /// process WITHOUT showing a menu. The first QueryContextMenu of the session LoadLibrary's
        /// every shell extension (TortoiseSVN, 7-Zip, cloud sync, AV, ...) — that's the lag on the
        /// first Details-view right-click. Calling this at idle after startup pays the cost up front.
        /// Must be called on an STA thread.
        /// </summary>
        public static void PreWarm(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            IntPtr pidl = IntPtr.Zero, parentPtr = IntPtr.Zero, childPidl = IntPtr.Zero, ctxPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
            IShellFolder parent = null;
            IContextMenu com1 = null;
            try
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;

                Guid gFolder = IID_IShellFolder;
                if (SHBindToParent(pidl, ref gFolder, out parentPtr, out childPidl) != 0 || parentPtr == IntPtr.Zero) return;
                parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);

                Guid gCtx = IID_IContextMenu;
                IntPtr[] apidl = { childPidl };
                if (parent.GetUIObjectOf(IntPtr.Zero, 1, apidl, ref gCtx, IntPtr.Zero, out ctxPtr) != 0 || ctxPtr == IntPtr.Zero) return;
                com1 = (IContextMenu)Marshal.GetObjectForIUnknown(ctxPtr);

                hMenu = CreatePopupMenu();
                // The expensive step: forces every registered handler DLL to load into the process.
                com1.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, CMF_EXPLORE);
            }
            catch { }
            finally
            {
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
                if (com1 != null) Marshal.ReleaseComObject(com1);
                if (parent != null) Marshal.ReleaseComObject(parent);
                if (ctxPtr != IntPtr.Zero) Marshal.Release(ctxPtr);
                if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
                if (pidl != IntPtr.Zero) ILFree(pidl);
            }
        }

        private static void InvokeShellVerb(IContextMenu cm, string verb, IntPtr owner)
        {
            IntPtr pVerb = Marshal.StringToHGlobalAnsi(verb);
            try
            {
                var ici = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                    hwnd = owner,
                    lpVerb = pVerb,
                    nShow = SW_SHOWNORMAL
                };
                cm.InvokeCommand(ref ici);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"ShellContextMenu verb '{verb}' failed: {ex.Message}"); }
            finally { Marshal.FreeHGlobal(pVerb); }
        }

        /// <summary>Builds the shell menu for an arbitrary path and invokes a named verb (e.g. "paste"
        /// on the containing folder).</summary>
        private static void InvokeVerbForPath(string path, string verb, IntPtr owner)
        {
            if (string.IsNullOrEmpty(path)) return;
            IntPtr pidl = IntPtr.Zero, parentPtr = IntPtr.Zero, childPidl = IntPtr.Zero, ctxPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
            IShellFolder parent = null; IContextMenu com1 = null;
            try
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
                Guid gFolder = IID_IShellFolder;
                if (SHBindToParent(pidl, ref gFolder, out parentPtr, out childPidl) != 0 || parentPtr == IntPtr.Zero) return;
                parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                Guid gCtx = IID_IContextMenu;
                IntPtr[] apidl = { childPidl };
                if (parent.GetUIObjectOf(owner, 1, apidl, ref gCtx, IntPtr.Zero, out ctxPtr) != 0 || ctxPtr == IntPtr.Zero) return;
                com1 = (IContextMenu)Marshal.GetObjectForIUnknown(ctxPtr);
                hMenu = CreatePopupMenu();
                com1.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, 0);
                InvokeShellVerb(com1, verb, owner);
            }
            catch (Exception ex) { LogManager.Log(LogManager.LogLevel.Warn, LogManager.LogCategory.UI, $"ShellContextMenu path verb '{verb}' failed: {ex.Message}"); }
            finally
            {
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
                if (com1 != null) Marshal.ReleaseComObject(com1);
                if (parent != null) Marshal.ReleaseComObject(parent);
                if (ctxPtr != IntPtr.Zero) Marshal.Release(ctxPtr);
                if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
                if (pidl != IntPtr.Zero) ILFree(pidl);
            }
        }

        private static void ApplyDarkMenus(IntPtr ownerHwnd)
        {
            try
            {
                if (!IsSystemDark()) return; // respect the OS light/dark setting
                if (!_darkModeInit) { SetPreferredAppMode(1 /* AllowDark */); _darkModeInit = true; }
                AllowDarkModeForWindow(ownerHwnd, true);
                FlushMenuThemes();
            }
            catch { }
        }

        /// <summary>True when Windows is set to dark mode for apps (HKCU AppsUseLightTheme == 0).</summary>
        public static bool IsSystemDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k?.GetValue("AppsUseLightTheme") is int i) return i == 0;
                }
            }
            catch { }
            return false;
        }
    }
}
