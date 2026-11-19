using Avalonia;
using Avalonia.Controls;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace NextCommander.App.Services;

/// <summary>
/// Colored menu icons. Glyphs are 16x16 geometries from Styles/MenuIcons.axaml (and App.axaml);
/// each command maps to a glyph, a color and an optional small badge (plus, cross, arrow…) in the lower right corner.
/// </summary>
public static class MenuIcons
{
    private static readonly IBrush Blue = Brush(0x2F, 0x7F, 0xE0);
    private static readonly IBrush Green = Brush(0x3B, 0xA5, 0x4A);
    private static readonly IBrush Red = Brush(0xE0, 0x48, 0x40);
    private static readonly IBrush Yellow = Brush(0xE6, 0xAA, 0x2E);
    private static readonly IBrush Purple = Brush(0x94, 0x70, 0xD0);
    private static readonly IBrush Teal = Brush(0x1F, 0xA5, 0x9C);
    private static readonly IBrush Orange = Brush(0xF0, 0x8A, 0x2C);
    private static readonly IBrush Gray = Brush(0x86, 0x92, 0x9E);

    /// <summary>The palette names an icon pack can recolor ("Blue" ...).</summary>
    private static readonly Dictionary<IBrush, string> s_paletteNames = new()
    {
        [Blue] = "Blue", [Green] = "Green", [Red] = "Red", [Yellow] = "Yellow",
        [Purple] = "Purple", [Teal] = "Teal", [Orange] = "Orange", [Gray] = "Gray",
    };

    /// <summary>The built-in color of a palette name ("Blue" ...): what a pack starts from (the icon pack editor).</summary>
    public static Color DefaultPaletteColor(string name) =>
        s_paletteNames.FirstOrDefault(p => string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase)).Key is ISolidColorBrush brush ? brush.Color : Colors.Gray;

    /// <summary>A palette color as the icon pack has it.</summary>
    private static IBrush Paint(IBrush color, Core.Config.IconPack pack) =>
        s_paletteNames.TryGetValue(color, out var name) ? Icons.IconPacks.Color(name, color, pack) : color;

    private sealed record Spec(string Glyph, IBrush Color, string? Badge = null, IBrush? BadgeColor = null);

    private static readonly Dictionary<string, Spec> s_map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Files
        ["nc_SetAttrib"] = new("MiAttrib", Blue),
        ["nc_PackFiles"] = new("FmArchiveIcon", Purple, "OvPlus", Green),
        ["nc_UnpackFiles"] = new("FmArchiveIcon", Purple, "OvArrowDown", Green),
        ["nc_TestArchive"] = new("FmArchiveIcon", Purple, "OvCheck", Green),
        ["nc_OpenArchive"] = new("FmArchiveIcon", Purple, "OvArrowRight", Blue),
        ["nc_CompareFilesByContent"] = new("MiCompare", Blue),
        ["nc_AssociateWith"] = new("MiGear", Gray),
        ["nc_InternalAssociate"] = new("MiGear", Teal),
        ["nc_Properties"] = new("MiInfo", Blue),
        ["nc_GetFileSpace"] = new("MiPie", Teal),
        ["nc_MultiRenameFiles"] = new("MiPencil", Orange, "OvPlus", Green),
        ["nc_EditComment"] = new("MiPencil", Yellow),
        ["nc_PrintFileList"] = new("MiPrinter", Gray),
        ["nc_PrintFile"] = new("MiPrinter", Blue),
        ["nc_SplitFile"] = new("MiSplit", Orange),
        ["nc_CombineFiles"] = new("MiCombine", Orange, "OvPlus", Green),
        ["nc_UUEncode"] = new("MiMail", Yellow, "OvArrowRight", Green),
        ["nc_UUDecode"] = new("MiMail", Yellow, "OvArrowLeft", Blue),
        ["nc_CRCcreate"] = new("MiHash", Teal),
        ["nc_CRCcheck"] = new("MiCheck", Green),
        ["nc_Open"] = new("MiFolderOpen", Yellow),
        ["nc_List"] = new("MiEye", Blue),
        ["nc_Edit"] = new("MiPencil", Orange),
        ["nc_EditNewFile"] = new("MiDoc", Gray, "OvPlus", Green),
        ["nc_OpenWith"] = new("MiDoc", Blue, "OvArrowRight", Green),
        ["nc_Copy"] = new("MiCopy", Blue),
        ["nc_RenMov"] = new("MiArrowRight", Green),
        // the Move button of the button bars points from the active panel to the other one
        ["nc_RenMov.left"] = new("MiArrowLeft", Green),
        ["nc_RenMov.up"] = new("MiArrowUp", Green),
        ["nc_RenMov.down"] = new("MiArrowDown", Green),
        ["nc_RenameOnly"] = new("MiPencil", Blue),
        ["nc_MkDir"] = new("FmFolderIcon", Yellow, "OvPlus", Green),
        ["nc_MkDirInTarget"] = new("FmFolderIcon", Yellow, "OvArrowRight", Green),
        ["nc_Delete"] = new("MiTrash", Red),
        ["nc_DeletePermanent"] = new("MiX", Red),
        ["nc_CopyToClipboard"] = new("MiClipboard", Blue),
        ["nc_CutToClipboard"] = new("MiScissors", Gray),
        ["nc_PasteFromClipboard"] = new("MiClipboard", Yellow, "OvArrowDown", Green),
        ["LOADLIST"] = new("MiFolderOpen", Yellow),
        ["nc_Exit"] = new("MiPower", Red),
        // Mark
        ["nc_SpreadSelection"] = new("MiPlusCircle", Green),
        ["nc_ShrinkSelection"] = new("MiMinusCircle", Red),
        ["nc_SelectAll"] = new("MiSelectAll", Green),
        ["nc_ClearAll"] = new("MiSelectNone", Gray),
        ["nc_ExchangeSelection"] = new("MiInvert", Blue),
        ["nc_SelectCurrentExtension"] = new("MiStar", Yellow),
        ["nc_SaveSelection"] = new("MiSave", Blue),
        ["nc_RestoreSelection"] = new("MiSelectAll", Blue, "OvArrowLeft", Green),
        ["nc_SaveSelectionToFile"] = new("MiSave", Teal),
        ["nc_LoadSelectionFromFile"] = new("MiFolderOpen", Yellow),
        ["nc_LoadSelectionFromClip"] = new("MiClipboard", Teal, "OvArrowRight", Green),
        ["nc_CopyNamesToClip"] = new("MiClipboard", Blue),
        ["nc_CopyFullNamesToClip"] = new("MiClipboard", Purple),
        ["nc_CopyDetailsToClip"] = new("MiClipboard", Teal),
        ["nc_CopyFullDetailsToClip"] = new("MiClipboard", Orange),
        ["nc_CopySrcPathToClip"] = new("FmFolderIcon", Yellow, "OvArrowRight", Blue),
        ["nc_CompareDirs"] = new("MiCompare", Orange),
        ["nc_CompareDirsHideSame"] = new("MiCompare", Orange, "OvX", Red),
        // Navigation
        ["nc_DirectoryHistory"] = new("MiHistory", Blue),
        ["nc_FrequentDirectories"] = new("MiStar", Orange),
        ["nc_DirectoryHotlist"] = new("MiStar", Yellow),
        ["nc_AddToHotlist"] = new("MiStar", Yellow, "OvPlus", Green),
        ["nc_ConfigDirHotlist"] = new("MiStar", Yellow, "OvCheck", Blue),
        ["nc_ShowQuickSearch"] = new("MiSearch", Blue),
        ["nc_ShowQuickFilter"] = new("MiFilter", Teal),
        ["nc_ClearQuickFilter"] = new("MiFilter", Gray, "OvX", Red),
        ["nc_DirTree"] = new("FmBarBranch", Yellow),
        ["nc_DirBranch"] = new("FmBarBranch", Teal),
        ["nc_DirBranchSel"] = new("FmBarBranch", Teal, "OvCheck", Green),
        ["nc_SyncChangeDir"] = new("MiSwap", Teal),
        ["nc_Exchange"] = new("MiExchange", Blue),
        ["nc_GoBack"] = new("MiArrowLeft", Blue),
        ["nc_GoForward"] = new("MiArrowRight", Blue),
        ["nc_GoToParent"] = new("MiArrowUp", Green),
        ["nc_GoToRoot"] = new("MiHome", Yellow),
        ["nc_EditPath"] = new("MiPencil", Gray),
        // Tabs
        ["nc_OpenNewTab"] = new("MiTabs", Yellow, "OvPlus", Green),
        ["nc_OpenDirInNewTab"] = new("MiTabs", Yellow, "OvArrowDown", Blue),
        ["nc_OpenDirInNewTabOther"] = new("MiTabs", Yellow, "OvArrowRight", Blue),
        ["nc_CloseCurrentTab"] = new("MiTabs", Gray, "OvX", Red),
        ["nc_CloseAllTabs"] = new("MiTabs", Gray, "OvX", Red),
        ["nc_CloseDuplicateTabs"] = new("MiTabs", Teal, "OvX", Red),
        ["nc_RestoreLastClosedTab"] = new("MiTabs", Yellow, "OvArrowLeft", Green),
        ["nc_OpenTabInOther"] = new("MiTabs", Yellow, "OvArrowRight", Blue),
        // the menus of the built-in editor (not commands: its own items)
        ["editor.save"] = new("MiSave", Blue),
        ["editor.reload"] = new("MiRefresh", Blue),
        ["editor.close"] = new("MiX", Gray),
        ["editor.undo"] = new("MiArrowLeft", Orange),
        ["editor.redo"] = new("MiArrowRight", Orange),
        ["editor.find"] = new("MiSearch", Blue),
        ["editor.replace"] = new("MiSearch", Orange, "OvArrowRight", Green),
        ["editor.findnext"] = new("MiSearch", Blue, "OvArrowDown", Green),
        ["editor.gotoline"] = new("MiArrowDown", Teal),
        ["editor.wordwrap"] = new("T:abc|de", Teal),
        ["editor.linenumbers"] = new("T:1|2", Gray),
        ["editor.font"] = new("T:Aa", Blue),
        ["editor.fontbigger"] = new("MiPlusCircle", Green),
        ["editor.fontsmaller"] = new("MiMinusCircle", Red),
        ["editor.bom"] = new("T:BOM", Purple),
        ["editor.reinterpret"] = new("MiRefresh", Orange),
        // the right-click menu of Lister (its view modes; the rest is shared with the editor)
        ["dup.keepnewest"] = new("MiHistory", Green, "OvCheck", Green),
        ["dup.keepoldest"] = new("MiHistory", Gray, "OvCheck", Green),
        ["dup.keepshortest"] = new("MiArrowLeft", Blue, "OvCheck", Green),
        ["dup.keeplongest"] = new("MiArrowRight", Blue, "OvCheck", Green),
        ["dup.keepfirstfolder"] = new("FmFolderIcon", Yellow, "OvCheck", Green),
        ["dup.markfolder"] = new("FmFolderIcon", Yellow, "OvPlus", Green),
        ["copy.addtarget"] = new("FmFolderIcon", Yellow, "OvPlus", Green),
        ["copy.addfilter"] = new("MiFilter", Blue, "OvPlus", Green),
        ["copy.savedefault"] = new("MiSave", Blue),
        ["find.open"] = new("FmDocumentIcon", Blue, "OvArrowRight", Green),
        ["find.goto"] = new("FmFolderIcon", Yellow, "OvArrowRight", Green),
        ["find.deletepermanently"] = new("MiTrash", Red, "OvX", Red),
        ["find.remove"] = new("MiDoc", Gray, "OvX", Red),
        ["find.export"] = new("MiSave", Blue, "OvArrowRight", Green),
        ["lister.text"] = new("FmDocumentIcon", Blue),
        ["lister.binary"] = new("T:01|10", Gray),
        ["lister.hex"] = new("T:0x|FF", Purple),
        ["lister.image"] = new("FmImageIcon", Teal),
        ["lister.preview"] = new("MiGlobe", Blue),
        ["lister.plugin"] = new("MiPlug", Purple),
        ["lister.media"] = new("FmVideoIcon", Red),
        ["lister.encoding"] = new("T:Aä", Purple),
        ["lister.findprevious"] = new("MiSearch", Blue, "OvArrowLeft", Green),
        ["nc_MoveTabLeft"] = new("MiTabs", Teal, "OvArrowLeft", Blue),
        ["nc_MoveTabRight"] = new("MiTabs", Teal, "OvArrowRight", Blue),
        ["nc_ShowClosedTabs"] = new("MiHistory", Gray),
        ["nc_ShowTabList"] = new("MiTabs", Blue),
        ["nc_LockTab"] = new("MiLock", Orange),
        ["nc_LockTabResettable"] = new("MiLock", Teal),
        ["nc_RenameTab"] = new("MiPencil", Blue),
        ["nc_SaveTabs"] = new("MiSave", Blue),
        ["nc_LoadTabs"] = new("MiFolderOpen", Yellow),
        // View
        ["nc_SrcShort"] = new("FmBarBrief", Blue),
        ["nc_SrcLong"] = new("FmBarFull", Blue),
        ["nc_SrcThumbs"] = new("FmBarThumbs", Teal),
        ["nc_SrcCustomViewMenu"] = new("MiColumns", Blue),
        ["nc_EditColumnSets"] = new("MiColumns", Gray, "OvPlus", Green),
        ["nc_QuickView"] = new("MiQuickView", Teal),
        ["nc_ShowTreePane"] = new("FmBarBranch", Yellow),
        ["nc_100Percent"] = new("MiFullView", Blue),
        ["nc_PanelsSplitPercent"] = new("MiSplitView", Blue),
        ["nc_SrcByName"] = new("T:ab|cd", Red),
        ["nc_SrcByExt"] = new("T:x.ab|y.cd", Blue),
        ["nc_SrcByDateTime"] = new("T:'05|'06", Purple),
        ["nc_SrcBySize"] = new("T:05k|60k", Green),
        ["nc_SrcUnsorted"] = new("T:xyz|ysz", Gray),
        ["nc_SrcNegOrder"] = new("T:z..|..a", Gray),
        ["nc_SrcComments"] = new("MiComments", Blue),
        ["nc_SrcAutoColumns"] = new("MiColumns", Teal),
        ["nc_SrcAllFiles"] = new("MiStar", Orange),
        ["nc_SrcExecs"] = new("FmExecutableIcon", Green),
        ["nc_SrcLastFilter"] = new("MiStar", Purple),
        ["nc_SrcUserSpec"] = new("MiFilter", Orange),
        ["nc_SrcOnlySelected"] = new("MiSelectAll", Red),
        ["nc_VerticalPanels"] = new("MiStacked", Blue),
        ["nc_NetConnect"] = new("MiNetwork", Green),
        ["nc_NetDisconnect"] = new("MiNetwork", Gray, "OvX", Red),
        ["nc_FtpNewConnection"] = new("MiGlobe", Blue, "OvPlus", Green),
        ["nc_ButtonConfig"] = new("FmBarCommands", Green),
        ["nc_VertButtonConfig"] = new("FmBarCommands", Teal),
        ["nc_ResetButtonBar"] = new("FmBarCommands", Green, "OvArrowLeft", Orange),
        ["nc_ResetVertButtonBar"] = new("FmBarCommands", Teal, "OvArrowLeft", Orange),
        // the button bars' own menu items (right click on a button)
        ["bar.edit"] = new("MiPencil", Orange),
        ["bar.delete"] = new("MiTrash", Red),
        ["bar.left"] = new("MiArrowLeft", Blue),
        ["bar.right"] = new("MiArrowRight", Blue),
        ["bar.up"] = new("MiArrowUp", Blue),
        ["bar.down"] = new("MiArrowDown", Blue),
        ["bar.separator"] = new("MiSplit", Gray),
        ["nc_EditConfig"] = new("MiDoc", Gray, "OvX", Red),
        ["nc_ConfigStartMenu"] = new("MiTerminal", Blue, "OvPlus", Green),
        ["nc_SetColorScheme"] = new("MiTheme", Orange),
        ["nc_Keyboard"] = new("MiKeyboard", Gray),
        ["nc_About"] = new("MiInfo", Blue),
        ["nc_SwitchHidSys"] = new("FmBarHidden", Gray),
        ["nc_SwitchHidden"] = new("FmBarHidden", Gray),
        ["nc_SwitchSystem"] = new("FmBarHidden", Orange),
        ["nc_SwitchColors"] = new("MiTheme", Purple),
        ["nc_SwitchSizeDisplay"] = new("MiPie", Blue),
        ["nc_SwitchAutoDirSizes"] = new("MiPie", Teal),
        ["nc_SwitchButtonBar"] = new("FmBarCommands", Gray),
        ["nc_SwitchVertButtonBar"] = new("FmBarCommands", Gray),
        ["nc_SwitchDarkmode"] = new("MiTheme", Blue),
        // Net
        ["nc_RemoteConnection"] = new("MiGlobe", Blue),
        ["nc_NewConnection"] = new("MiGlobe", Blue, "OvPlus", Green),
        ["nc_OpenConnection"] = new("MiGlobe", Blue),
        ["nc_ConnectionsMenu"] = new("MiGlobe", Blue),
        ["nc_PluginsMenu"] = new("MiPlug", Purple),
        ["nc_OpenNetwork"] = new("MiNetwork", Blue),
        ["nc_FtpDisconnect"] = new("MiGlobe", Gray, "OvX", Red),
        ["nc_DownloadUrl"] = new("MiGlobe", Blue, "OvArrowDown", Green),
        ["nc_MasterPassword"] = new("MiKey", Yellow),
        ["nc_LinkConnect"] = new("MiMonitor", Blue, "OvArrowRight", Green),
        ["nc_LinkShare"] = new("MiMonitor", Teal, "OvArrowLeft", Green),

        // the connection manager's own menus
        ["connections.new"] = new("MiGlobe", Blue, "OvPlus", Green),
        ["connections.folder"] = new("MiFolderOpen", Yellow, "OvPlus", Green),
        ["connections.connect"] = new("MiPlug", Green),
        ["connections.favorite"] = new("MiStar", Yellow),
        ["connections.duplicate"] = new("MiCopy", Blue),
        ["connections.copy"] = new("MiClipboard", Gray),
        ["connections.forget"] = new("MiKey", Gray, "OvX", Red),
        ["connections.rename"] = new("MiPencil", Gray),
        ["connections.delete"] = new("MiTrash", Red),
        ["connections.import"] = new("MiArrowDown", Blue),
        ["nc_Plugins"] = new("MiPlug", Purple),
        // Commands
        ["nc_SearchFor"] = new("MiSearch", Blue),
        ["nc_FindDuplicates"] = new("MiCopy", Orange),
        ["nc_DiskUsage"] = new("MiPie", Teal),
        ["nc_SearchStandalone"] = new("MiSearch", Teal, "OvArrowRight", Green),
        ["nc_VolumeLabel"] = new("MiTag", Orange),
        ["nc_RestoreFromTrash"] = new("MiTrash", Gray, "OvArrowLeft", Green),
        ["nc_EmptyTrash"] = new("MiTrash", Red, "OvX", Red),
        ["nc_SysInfo"] = new("MiChip", Teal),
        ["nc_SyncDirs"] = new("MiSwap", Green),
        ["nc_ExecuteTerminal"] = new("MiTerminal", Gray),
        ["nc_ToggleTerminal"] = new("MiTerminal", Blue),
        ["nc_MaximizeTerminal"] = new("MiTerminal", Blue),
        ["nc_SwitchWorkspaceTab"] = new("FmBarTab", Blue),
        ["nc_OpenDesktop"] = new("MiDesktop", Blue),
        ["nc_BackgroundTransfers"] = new("MiTransfer", Green),
        ["nc_MatchSrc"] = new("MiEquals", Blue),
        ["nc_RereadSource"] = new("MiRefresh", Green),
        ["nc_CountDirContent"] = new("MiPie", Yellow),
        ["nc_RevealInExplorer"] = new("FmFolderIcon", Yellow, "OvSearch", Blue),
        ["nc_Config"] = new("MiHammer", Orange),
        ["nc_CommandBrowser"] = new("MiKeyboard", Gray),
        ["nc_AddButton"] = new("FmBarCommands", Blue, "OvPlus", Green),
        ["nc_CheckForUpdates"] = new("MiRefresh", Blue, "OvArrowDown", Green),
        ["nc_ExportSettings"] = new("MiSave", Blue, "OvArrowRight", Green),
        ["nc_ImportSettings"] = new("MiFolderOpen", Yellow, "OvArrowDown", Green),
        ["nc_RestoreSettings"] = new("MiHistory", Blue, "OvArrowLeft", Green),
        ["nc_AddUserCommand"] = new("MiTerminal", Blue, "OvPlus", Green),
    };

    /// <summary>Commands that have an icon (tests).</summary>
    public static IReadOnlyCollection<string> Commands => s_map.Keys;

    /// <summary>Commands left without a menu icon on purpose.</summary>
    public static IReadOnlySet<string> WithoutIcon { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nc_NetShareDir", "nc_NetUnshareDir", "nc_SwitchAdminShares", "nc_FtpHiddenFiles", "nc_FtpDownloadList", "nc_SwitchDarkNormal",
        "nc_SavePosition", "nc_SaveSettings",
    };

    /// <summary>Sets an icon on every menu item (recursively) whose command parameter has one.</summary>
    public static void Apply(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            // Submenu headers name their icon's command in Tag.
            if (item.Icon is null && (item.CommandParameter ?? item.Tag) is string parameter && Create(parameter) is { } icon)
            {
                item.Icon = icon;
            }
            // Icons > show checked items as pressed buttons: the icon gets a pressed background instead of a check mark.
            item.Classes.Set("pressedcheck", PressedChecks && item.ToggleType == MenuItemToggleType.CheckBox && item.Icon is not null);
            Apply(item.Items);
        }
    }

    /// <summary>Icons > a frame around the menu icons.</summary>
    public static bool Frame { get; set; }

    /// <summary>Icons > checked items as pressed buttons instead of check marks.</summary>
    public static bool PressedChecks { get; set; } = true;

    /// <summary>Removes the icons again (setting "icons in the main menu" off).</summary>
    public static void Clear(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            item.Icon = null;
            Clear(item.Items);
        }
    }

    /// <summary>Builds the icon for a command line ("nc_X param" uses nc_X), or null; drawn as the icon pack says.</summary>
    public static Control? Create(string command, Core.Config.IconPack? pack = null)
    {
        pack ??= Icons.IconPacks.Current;
        var name = command.Split(' ', 2)[0];
        if (name == "nc_NewConnection" && command.Length > name.Length
            && Icons.BrandIcons.ProtocolOf(command[(name.Length + 1)..].Trim()) is { } protocol)
        {
            return Wrap(Icons.BrandIcons.Create(protocol)); // the type's product mark
        }
        if (!s_map.TryGetValue(name, out var spec))
        {
            return null;
        }
        if (spec.Glyph.StartsWith("T:", StringComparison.Ordinal))
        {
            var text = pack.Style == Core.Config.IconPackStyle.Monochrome ? Icons.IconPacks.MonochromeBrush(pack) : Paint(spec.Color, pack);
            return Wrap(TextIcon(spec.Glyph[2..], text));
        }
        if (Icons.IconPacks.Glyph(spec.Glyph, pack) is not { } glyph)
        {
            return null;
        }
        var panel = new Panel { Width = 16, Height = 16 };
        var badge = spec.Badge is null || !pack.Badges ? null : Icons.IconPacks.Glyph(spec.Badge, pack);
        if (badge is not null && Find("OvHalo") is { } halo)
        {
            glyph = new CombinedGeometry(GeometryCombineMode.Exclude, glyph, halo);
        }
        panel.Children.Add(Icons.IconPacks.Draw(glyph, Paint(spec.Color, pack), pack));
        if (badge is not null)
        {
            panel.Children.Add(Icons.IconPacks.Draw(badge, Paint(spec.BadgeColor ?? spec.Color, pack), pack));
        }
        return Wrap(panel);
    }

    /// <summary>Every icon sits in a border: frame (Icons > 3D frame) and pressed background for checked items.</summary>
    private static Border Wrap(Control icon)
    {
        var border = new Border { Child = icon, Padding = new Thickness(1), CornerRadius = new CornerRadius(2) };
        border.Classes.Add("menuicon");
        if (Frame)
        {
            border.Classes.Add("framed");
        }
        return border;
    }

    /// <summary>Two tiny lines of text ("ab" over "cd"): the sort icons.</summary>
    private static Control TextIcon(string text, IBrush color)
    {
        var lines = text.Split('|');
        var stack = new StackPanel { Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 7,
                LineHeight = 7.5,
                FontWeight = FontWeight.Bold,
                Foreground = color,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Classes = { "noloc" },
            });
        }
        return stack;
    }

    private static Geometry? Find(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;

    private static ImmutableSolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
