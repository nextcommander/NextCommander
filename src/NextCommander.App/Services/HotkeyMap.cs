using Avalonia.Input;
using NextCommander.Core.Config;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>Key gesture → command line. Settings may override the defaults.</summary>
public sealed class HotkeyMap
{
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Files
        ["F2"] = "nc_RereadSource",
        ["Ctrl+R"] = "nc_RereadSource",
        ["F3"] = "nc_List",
        ["F4"] = "nc_Edit",
        ["Shift+F4"] = "nc_EditNewFile",
        ["Ctrl+Q"] = "nc_QuickView",
        ["Alt+F7"] = "nc_SearchFor",
        ["Alt+Shift+F7"] = "nc_SearchStandalone",
        ["Ctrl+M"] = "nc_MultiRenameFiles",
        ["Ctrl+Alt+S"] = "nc_Config",
        ["Shift+Enter"] = "nc_OpenWith",
        ["Ctrl+F"] = "nc_RemoteConnection",
        ["Ctrl+Shift+F"] = "nc_FtpDisconnect",
        ["Alt+F5"] = "nc_PackFiles",
        ["Alt+F9"] = "nc_UnpackFiles",
        ["Alt+Shift+F9"] = "nc_TestArchive",
        ["Ctrl+PageDown"] = "nc_OpenArchive",
        ["F5"] = "nc_Copy",
        ["F6"] = "nc_RenMov",
        ["Shift+F6"] = "nc_RenameOnly",
        ["F7"] = "nc_MkDir",
        ["Shift+F7"] = "nc_MkDirInTarget",
        ["F8"] = "nc_Delete",
        ["Delete"] = "nc_Delete",
        ["Shift+F8"] = "nc_DeletePermanent",
        ["Shift+Delete"] = "nc_DeletePermanent",
        ["Alt+F4"] = "nc_Exit",
        ["Ctrl+C"] = "nc_CopyToClipboard",
        ["Ctrl+X"] = "nc_CutToClipboard",
        ["Ctrl+V"] = "nc_PasteFromClipboard",

        // Views
        ["Ctrl+F1"] = "nc_SrcShort",
        ["Ctrl+F2"] = "nc_SrcLong",
        ["Ctrl+Shift+F1"] = "nc_SrcThumbs",
        ["Ctrl+F3"] = "nc_SrcByName",
        ["Ctrl+F4"] = "nc_SrcByExt",
        ["Ctrl+F5"] = "nc_SrcByDateTime",
        ["Ctrl+F6"] = "nc_SrcBySize",
        ["Ctrl+F7"] = "nc_SrcUnsorted",
        ["Ctrl+Shift+F2"] = "nc_SrcComments",
        ["Shift+F1"] = "nc_SrcCustomViewMenu",
        ["Ctrl+F10"] = "nc_SrcAllFiles",
        ["Ctrl+F11"] = "nc_SrcExecs",
        ["Ctrl+F12"] = "nc_SrcUserSpec",
        ["Ctrl+N"] = "nc_FtpNewConnection",
        ["Ctrl+B"] = "nc_DirBranch",
        ["Ctrl+Shift+B"] = "nc_DirBranchSel",
        ["Ctrl+F8"] = "nc_ShowTreePane",
        ["Alt+F10"] = "nc_DirTree",
        ["Ctrl+H"] = "nc_SwitchHidSys",
        ["Ctrl+Shift+D"] = "nc_SwitchDarkmode",

        // Navigation
        ["Alt+F1"] = "nc_LeftOpenDrives",
        ["Alt+F2"] = "nc_RightOpenDrives",
        ["Ctrl+U"] = "nc_Exchange",
        ["Ctrl+Left"] = "nc_TransferLeft",
        ["Ctrl+Right"] = "nc_TransferRight",
        ["Alt+Left"] = "nc_GoBack",
        ["Alt+Right"] = "nc_GoForward",
        ["Alt+Down"] = "nc_DirectoryHistory",
        ["Alt+Shift+Down"] = "nc_FrequentDirectories",
        ["Ctrl+D"] = "nc_DirectoryHotlist",
        ["Ctrl+PageUp"] = "nc_GoToParent",
        ["Ctrl+Back"] = "nc_GoToRoot",
        ["Ctrl+L"] = "nc_GetFileSpace",
        ["Alt+D"] = "nc_EditPath",
        ["Alt+Enter"] = "nc_Properties",
        ["Ctrl+Z"] = "nc_Undo",
        ["Ctrl+Alt+Z"] = "nc_EditComment",
        ["Alt+Add"] = "nc_SelectCurrentExtension",
        ["Divide"] = "nc_RestoreSelection",
        ["Shift+F2"] = "nc_CompareDirs",
        ["Ctrl+S"] = "nc_ShowQuickFilter",
        ["Ctrl+Y"] = "nc_ClearQuickFilter",

        // Tabs
        ["Ctrl+T"] = "nc_OpenNewTab",
        ["Ctrl+Up"] = "nc_OpenDirInNewTab",
        ["Ctrl+Shift+Up"] = "nc_OpenDirInNewTabOther",
        ["Ctrl+W"] = "nc_CloseCurrentTab",
        ["Ctrl+Shift+W"] = "nc_CloseAllTabs",
        ["Ctrl+Shift+T"] = "nc_RestoreLastClosedTab",
        ["Ctrl+Tab"] = "nc_SwitchToNextTab",
        ["Ctrl+Shift+Tab"] = "nc_SwitchToPreviousTab",
        ["Ctrl+Shift+A"] = "nc_ShowTabList",
        ["Ctrl+D1"] = "nc_SrcActivateTab1",
        ["Ctrl+D2"] = "nc_SrcActivateTab2",
        ["Ctrl+D3"] = "nc_SrcActivateTab3",
        ["Ctrl+D4"] = "nc_SrcActivateTab4",
        ["Ctrl+D5"] = "nc_SrcActivateTab5",
        ["Ctrl+D6"] = "nc_SrcActivateTab6",
        ["Ctrl+D7"] = "nc_SrcActivateTab7",
        ["Ctrl+D8"] = "nc_SrcActivateTab8",
        ["Ctrl+D9"] = "nc_SrcActivateTab9",

        // Selection
        ["Ctrl+A"] = "nc_SelectAll",
        ["Ctrl+Add"] = "nc_SelectAll",
        ["Ctrl+Subtract"] = "nc_ClearAll",
        ["Add"] = "nc_SpreadSelection",
        ["Subtract"] = "nc_ShrinkSelection",
        ["Multiply"] = "nc_ExchangeSelection",
        ["Ctrl+Shift+R"] = "nc_RestoreSelection",
        ["Alt+Shift+Enter"] = "nc_CountDirContent",

        // Command line / clipboard text
        ["Ctrl+Enter"] = "nc_AddFilenameToCmdline",
        ["Ctrl+Shift+Enter"] = "nc_AddPathAndFilenameToCmdline",
        ["Ctrl+P"] = "nc_AddPathToCmdline",
        ["Ctrl+Shift+C"] = "nc_CopyFullNamesToClip",
        ["Ctrl+Alt+C"] = "nc_CopyNamesToClip",
        ["Ctrl+Alt+D"] = "nc_CopyDetailsToClip",
        ["Ctrl+Alt+P"] = "nc_CopySrcPathToClip",
        ["Ctrl+Shift+P"] = "nc_CommandPalette",

        // Misc
        ["Ctrl+Alt+T"] = "nc_ExecuteTerminal",
        ["Ctrl+OemTilde"] = "nc_ToggleTerminal",
        ["Alt+F8"] = "nc_ShowCmdLineHistory",
        ["Ctrl+Shift+E"] = "nc_RevealInExplorer",
        ["Alt+Shift+F11"] = "nc_FocusButtonBar",
        ["Ctrl+Shift+F12"] = "nc_CommandBrowser",
    };

    private readonly AppSettings _settings;
    private List<(KeyGesture Gesture, string Text, string Command)> _bindings = [];

    public HotkeyMap(AppSettings settings)
    {
        _settings = settings;
        Rebuild();
    }

    /// <summary>All active bindings (defaults merged with overrides).</summary>
    public IReadOnlyList<(string Gesture, string Command)> Bindings => _bindings.Select(b => (b.Text, b.Command)).ToList();

    /// <summary>Re-reads the overrides from the settings.</summary>
    public void Rebuild()
    {
        var merged = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, command) in _settings.Hotkeys)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                merged.Remove(key);
            }
            else
            {
                merged[key] = command;
            }
        }
        var list = new List<(KeyGesture, string, string)>();
        foreach (var (key, command) in merged)
        {
            try
            {
                list.Add((KeyGesture.Parse(key), key, command));
            }
            catch (ArgumentException ex)
            {
                Log.Warning(ex, "Invalid hotkey {Key}", key);
            }
        }
        _bindings = list;
    }

    /// <summary>Gestures that differ from the defaults (shown as "redefined" in the command browser).</summary>
    public bool IsRedefined(string gesture) => _settings.Hotkeys.ContainsKey(gesture);

    public string? Find(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return null;
        }
        var found = Find(e.Key, e.KeyModifiers);
        // macOS: Cmd acts like Ctrl unless a Cmd binding exists.
        if (found is null && OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            found = Find(e.Key, (e.KeyModifiers & ~KeyModifiers.Meta) | KeyModifiers.Control);
        }
        return found;
    }

    private string? Find(Key key, KeyModifiers modifiers)
    {
        foreach (var (gesture, _, command) in _bindings)
        {
            if (gesture.Key == key && gesture.KeyModifiers == modifiers)
            {
                return command;
            }
        }
        return null;
    }

    public string? GestureFor(string command) =>
        _bindings.FirstOrDefault(b => string.Equals(b.Command, command, StringComparison.OrdinalIgnoreCase)).Text;

    /// <summary>Text for a key press ("Ctrl+Shift+K"), used by the hotkey capture dialog.</summary>
    public static string? Describe(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
        {
            return null;
        }
        return new KeyGesture(key, modifiers).ToString();
    }
}
