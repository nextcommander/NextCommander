using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NextCommander.Platform.Windows;

/// <summary>
/// Explorer's context menu of files and folders (IContextMenu): shown at a screen point, the chosen command run by the
/// shell itself (open with, send to, share, properties, the menu entries of installed programs ...). Owner-drawn and
/// late-filled submenus ("Send to", "Open with") get the window messages through a temporary window subclass.
/// The shell interfaces are called through their vtables (Avalonia turns the runtime's built-in COM interop off).
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class ShellContextMenu
{
    private const uint CmfNormal = 0x0;
    private const uint CmfExplore = 0x4;
    private const uint CmfExtendedVerbs = 0x100;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int FirstCommand = 1;
    private const int LastCommand = 0x7FFF;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint WmDrawItem = 0x002B;
    private const uint WmMeasureItem = 0x002C;
    private const uint WmMenuChar = 0x0120;
    private const uint WmMenuSelect = 0x011F;

    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid IidContextMenu2 = new("000214F4-0000-0000-C000-000000000046");
    private static readonly Guid IidContextMenu3 = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");

    /// <summary>The <paramref name="slot"/>th function of a COM object's vtable.</summary>
    private static IntPtr Method(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

    /// <summary>
    /// Shows the menu of <paramref name="paths"/> (all in one folder) at a screen point and runs what was chosen.
    /// False when the shell could not make a menu for them.
    /// </summary>
    /// <remarks>
    /// <paramref name="verbIcon"/>: an icon (HBITMAP, 32-bit premultiplied DIB) for the verb of an item without one of its
    /// own ("copy", "cut", "delete", "rename", "properties" ...), or zero; the bitmaps are deleted when the menu closes.
    /// </remarks>
    /// <param name="extras">The application's own items, put at the top of the menu (with their icons from <paramref name="verbIcon"/> by command).</param>
    public static bool Show(IntPtr owner, IReadOnlyList<string> paths, int x, int y, bool extended, Func<string, IntPtr>? verbIcon = null,
        IReadOnlyList<SystemMenuItem>? extras = null) =>
        Show(owner, paths, x, y, extended, verbIcon, probe: null, extras);

    /// <summary>Builds the menu without showing it and returns how many items the shell put in it (tests, diagnostics); -1 when none was made.</summary>
    public static int CountItems(IReadOnlyList<string> paths) => Probe(paths, verbIcon: null).Items;

    /// <summary>
    /// Builds the menu without showing it: how many items it has, and the verbs of the items that got an icon from
    /// <paramref name="verbIcon"/> (tests, diagnostics). Items -1 when no menu was made.
    /// </summary>
    public static (int Items, IReadOnlyList<string> VerbsWithIcon) Probe(IReadOnlyList<string> paths, Func<string, IntPtr>? verbIcon) =>
        Probe(paths, verbIcon, extras: null).Probe;

    /// <summary>As the other Probe, with the application's items: also the texts of the first items of the menu.</summary>
    public static ((int Items, IReadOnlyList<string> VerbsWithIcon) Probe, IReadOnlyList<string> FirstTexts) Probe(IReadOnlyList<string> paths,
        Func<string, IntPtr>? verbIcon, IReadOnlyList<SystemMenuItem>? extras)
    {
        var texts = new List<string>();
        var count = -1;
        var verbs = new List<string>();
        Show(IntPtr.Zero, paths, 0, 0, extended: false, verbIcon is null ? null : verb =>
        {
            var bitmap = verbIcon(verb);
            if (bitmap != IntPtr.Zero)
            {
                verbs.Add(verb);
            }
            return bitmap;
        }, probe: menu =>
        {
            count = GetMenuItemCount(menu);
            for (var i = 0; i < Math.Min(count, 4); i++)
            {
                texts.Add(MenuText(menu, i));
            }
        }, extras);
        return ((count, verbs), texts);
    }

    /// <summary>First command id of the application's own items (above the shell's range).</summary>
    private const int FirstExtraCommand = 0x8000;

    private static bool Show(IntPtr owner, IReadOnlyList<string> paths, int x, int y, bool extended, Func<string, IntPtr>? verbIcon, Action<IntPtr>? probe,
        IReadOnlyList<SystemMenuItem>? extras)
    {
        if (paths.Count == 0)
        {
            return false;
        }
        var bitmaps = new List<IntPtr>();
        var fullPidls = new List<IntPtr>();
        IntPtr folder = IntPtr.Zero, contextMenu = IntPtr.Zero, contextMenu2 = IntPtr.Zero, contextMenu3 = IntPtr.Zero, menu = IntPtr.Zero;
        SubclassProc? subclass = null;
        try
        {
            var children = new IntPtr[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                if (SHParseDisplayName(paths[i], IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                {
                    return false;
                }
                fullPidls.Add(pidl);
                var iid = IidShellFolder;
                if (SHBindToParent(pidl, ref iid, out var parent, out var child) != 0)
                {
                    return false;
                }
                if (i == 0)
                {
                    folder = parent;
                }
                else
                {
                    Marshal.Release(parent); // the items share the first one's folder
                }
                children[i] = child; // points into the full pidl: not freed on its own
            }

            // IShellFolder::GetUIObjectOf (slot 10)
            var iidMenu = IidContextMenu;
            IntPtr result;
            fixed (IntPtr* childPidls = children)
            {
                var getUiObjectOf = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, Guid*, IntPtr, IntPtr*, int>)Method(folder, 10);
                if (getUiObjectOf(folder, owner, (uint)children.Length, childPidls, &iidMenu, IntPtr.Zero, &result) != 0 || result == IntPtr.Zero)
                {
                    return false;
                }
            }
            contextMenu = result;
            var iid2 = IidContextMenu2;
            var iid3 = IidContextMenu3;
            if (Marshal.QueryInterface(contextMenu, in iid3, out var cm3) == 0)
            {
                contextMenu3 = cm3;
            }
            if (Marshal.QueryInterface(contextMenu, in iid2, out var cm2) == 0)
            {
                contextMenu2 = cm2;
            }

            // IContextMenu::QueryContextMenu (slot 3)
            menu = CreatePopupMenu();
            var queryContextMenu = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, int>)Method(contextMenu, 3);
            if (queryContextMenu(contextMenu, menu, 0, FirstCommand, LastCommand, CmfNormal | CmfExplore | (extended ? CmfExtendedVerbs : 0)) < 0)
            {
                return false;
            }
            if (verbIcon is not null)
            {
                AddVerbIcons(contextMenu, menu, verbIcon, bitmaps);
            }
            if (extras is { Count: > 0 })
            {
                AddExtras(menu, extras, verbIcon, bitmaps);
            }

            if (probe is not null)
            {
                probe(menu);
                return true;
            }

            // submenus filled when opened and owner-drawn items need the window's menu messages
            if (contextMenu3 != IntPtr.Zero || contextMenu2 != IntPtr.Zero)
            {
                var handler3 = contextMenu3;
                var handler2 = contextMenu2;
                subclass = (hwnd, message, wParam, lParam, _, _) =>
                {
                    if (message is WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar or WmMenuSelect)
                    {
                        if (handler3 != IntPtr.Zero)
                        {
                            IntPtr handled;
                            var handleMenuMsg2 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr*, int>)Method(handler3, 7);
                            if (handleMenuMsg2(handler3, message, wParam, lParam, &handled) == 0)
                            {
                                return handled;
                            }
                        }
                        else
                        {
                            var handleMenuMsg = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Method(handler2, 6);
                            if (handleMenuMsg(handler2, message, wParam, lParam) == 0)
                            {
                                return IntPtr.Zero;
                            }
                        }
                    }
                    return DefSubclassProc(hwnd, message, wParam, lParam);
                };
                SetWindowSubclass(owner, subclass, 0x4E43, IntPtr.Zero);
            }

            SetForegroundWindow(owner); // a popup menu of a window that is not in front would not close on outside clicks
            var command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, x, y, owner, IntPtr.Zero);
            if (extras is not null && command >= FirstExtraCommand && command - FirstExtraCommand < extras.Count)
            {
                extras[command - FirstExtraCommand].Run(); // the application's own item
            }
            else if (command >= FirstCommand && command < FirstExtraCommand)
            {
                // IContextMenu::InvokeCommand (slot 4)
                var info = new CMINVOKECOMMANDINFO
                {
                    cbSize = sizeof(CMINVOKECOMMANDINFO),
                    hwnd = owner,
                    lpVerb = command - FirstCommand,
                    nShow = 1, // SW_SHOWNORMAL
                };
                var invokeCommand = (delegate* unmanaged[Stdcall]<IntPtr, CMINVOKECOMMANDINFO*, int>)Method(contextMenu, 4);
                invokeCommand(contextMenu, &info);
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning("System context menu failed: " + ex.Message);
            return false;
        }
        finally
        {
            if (subclass is not null)
            {
                RemoveWindowSubclass(owner, subclass, 0x4E43);
                GC.KeepAlive(subclass);
            }
            if (menu != IntPtr.Zero)
            {
                DestroyMenu(menu);
            }
            foreach (var bitmap in bitmaps)
            {
                DeleteObject(bitmap); // the menu does not own the item bitmaps
            }
            foreach (var pointer in new[] { contextMenu3, contextMenu2, contextMenu, folder })
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.Release(pointer);
                }
            }
            foreach (var pidl in fullPidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private const uint MiimId = 0x0002;
    private const uint MiimBitmap = 0x0080;
    private const uint MiimFtype = 0x0100;
    private const uint MftSeparator = 0x0800;
    private const uint MftOwnerDraw = 0x0100;
    private const uint GcsVerbW = 0x0004;

    /// <summary>
    /// Gives the items of the shell's own verbs (cut, copy, delete, rename, properties ...) an icon where they have none:
    /// the verb comes from IContextMenu::GetCommandString (slot 5).
    /// </summary>
    private static void AddVerbIcons(IntPtr contextMenu, IntPtr menu, Func<string, IntPtr> verbIcon, List<IntPtr> bitmaps)
    {
        var getCommandString = (delegate* unmanaged[Stdcall]<IntPtr, UIntPtr, uint, uint*, char*, uint, int>)Method(contextMenu, 5);
        var buffer = stackalloc char[260];
        var count = GetMenuItemCount(menu);
        for (var i = 0; i < count; i++)
        {
            var info = new MENUITEMINFOW { cbSize = (uint)sizeof(MENUITEMINFOW), fMask = MiimId | MiimBitmap | MiimFtype };
            if (!GetMenuItemInfoW(menu, (uint)i, true, &info) || (info.fType & (MftSeparator | MftOwnerDraw)) != 0 || info.hbmpItem != IntPtr.Zero
                || info.wID < FirstCommand || info.wID > LastCommand)
            {
                continue;
            }
            buffer[0] = '\0';
            if (getCommandString(contextMenu, (UIntPtr)(info.wID - FirstCommand), GcsVerbW, null, buffer, 260) != 0 || buffer[0] == '\0')
            {
                continue; // an item of an extension without a verb name
            }
            var verb = new string(buffer).ToLowerInvariant();
            var bitmap = verbIcon(verb);
            if (bitmap == IntPtr.Zero)
            {
                continue;
            }
            bitmaps.Add(bitmap);
            var set = new MENUITEMINFOW { cbSize = (uint)sizeof(MENUITEMINFOW), fMask = MiimBitmap, hbmpItem = bitmap };
            SetMenuItemInfoW(menu, (uint)i, true, &set);
        }
    }

    private const uint MiimString = 0x0040;

    /// <summary>The application's items at the top of the menu, then a separator.</summary>
    private static void AddExtras(IntPtr menu, IReadOnlyList<SystemMenuItem> extras, Func<string, IntPtr>? verbIcon, List<IntPtr> bitmaps)
    {
        for (var i = 0; i < extras.Count; i++)
        {
            var bitmap = extras[i].Command is { } command && verbIcon is not null ? verbIcon(command) : IntPtr.Zero;
            if (bitmap != IntPtr.Zero)
            {
                bitmaps.Add(bitmap);
            }
            fixed (char* text = extras[i].Text)
            {
                var info = new MENUITEMINFOW
                {
                    cbSize = (uint)sizeof(MENUITEMINFOW),
                    fMask = MiimId | MiimString | MiimFtype | (bitmap != IntPtr.Zero ? MiimBitmap : 0),
                    wID = (uint)(FirstExtraCommand + i),
                    dwTypeData = (IntPtr)text,
                    cch = (uint)extras[i].Text.Length,
                    hbmpItem = bitmap,
                };
                InsertMenuItemW(menu, (uint)i, true, &info);
            }
        }
        var separator = new MENUITEMINFOW { cbSize = (uint)sizeof(MENUITEMINFOW), fMask = MiimFtype, fType = MftSeparator };
        InsertMenuItemW(menu, (uint)extras.Count, true, &separator);
    }

    /// <summary>The text of a menu item (tests).</summary>
    private static string MenuText(IntPtr menu, int index)
    {
        var buffer = stackalloc char[256];
        var info = new MENUITEMINFOW { cbSize = (uint)sizeof(MENUITEMINFOW), fMask = MiimString, dwTypeData = (IntPtr)buffer, cch = 255 };
        return GetMenuItemInfoW(menu, (uint)index, true, &info) ? new string(buffer, 0, (int)info.cch) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData);

#pragma warning disable SYSLIB1054 // delegate marshalling for the subclass procedure: classic interop
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr folder, out IntPtr childPidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenuItemW(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, MENUITEMINFOW* info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemInfoW(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, MENUITEMINFOW* info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuItemInfoW(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, MENUITEMINFOW* info);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
#pragma warning restore SYSLIB1054
}
