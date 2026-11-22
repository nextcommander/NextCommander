using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Services.Icons;
using NextCommander.Platform;

namespace NextCommander.App.Views;

/// <summary>
/// The drive box of the path row (the user's template, 22 November 2026): a button with the panel's drive that opens a
/// list of the drives (label, free space, a used-space bar, the letter), the places (home, documents, downloads,
/// desktop) and Computer, Network and the trash. ↑ / ↓ choose, Enter opens, a letter opens that drive, Esc closes;
/// a right click shows the drive's menu (the system's or the application's). Alt+F1 / Alt+F2 open it.
/// </summary>
public partial class FilePanelView
{
    /// <summary>Whether the drive box's list is open (tests).</summary>
    public bool IsDriveBoxOpen => DrivePopup.IsOpen;

    /// <summary>The entries of the open list that can be chosen (tests).</summary>
    public IReadOnlyList<DriveItem> DriveBoxEntries =>
        [.. DriveList.Items.OfType<ListBoxItem>().Select(i => i.DataContext).OfType<DriveItem>()];

    private void InitializeDriveBox()
    {
        // the card's bars (drive chips at the top, status at the bottom) follow its rounded corners: ClipToBounds of
        // the frame cuts square, which left their corners standing out of the line
        FrameContent.SizeChanged += (_, e) =>
            FrameContent.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        DriveBox.Click += (_, _) =>
        {
            Activated?.Invoke(this, EventArgs.Empty);
            if (DrivePopup.IsOpen)
            {
                DrivePopup.IsOpen = false;
            }
            else
            {
                OpenDriveBox();
            }
        };
        DrivePopup.Opened += (_, _) => DriveBox.Classes.Set("open", true);
        DrivePopup.Closed += (_, _) => DriveBox.Classes.Set("open", false);
        DriveList.AddHandler(KeyDownEvent, OnDriveListKeyDown, RoutingStrategies.Tunnel);
        DriveList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left
                && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: DriveItem drive })
            {
                PickDrive(drive);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        DriveList.AddHandler(ContextRequestedEvent, OnDriveContextRequested, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Opens the list on the panel's drive (Alt+F1 / Alt+F2 too).</summary>
    public void OpenDriveBox()
    {
        if (_vm is not { } vm)
        {
            return;
        }
        vm.RefreshDrives();
        DriveList.Items.Clear();
        var current = vm.SelectedDrive;
        ListBoxItem? selected = null;
        void Header(string text) => DriveList.Items.Add(new ListBoxItem
        {
            Classes = { "header" },
            Focusable = false,
            Content = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, [!TextBlock.ForegroundProperty] = Resource("NcFaintBrush") },
        });
        void Line() => DriveList.Items.Add(new ListBoxItem
        {
            Classes = { "header" },
            Focusable = false,
            Padding = new Thickness(2, 4),
            Content = new Border { Height = 1, [!Border.BackgroundProperty] = Resource("NcMwStrokeBrush") },
        });
        ListBoxItem Add(DriveItem drive, Control content)
        {
            var item = new ListBoxItem { DataContext = drive, Content = content };
            DriveList.Items.Add(item);
            if (current is not null && (ReferenceEquals(drive, current)
                || string.Equals(drive.RootPath, current.RootPath, StringComparison.OrdinalIgnoreCase) && drive.RootPath.Length > 0))
            {
                selected ??= item;
            }
            return item;
        }

        Header("Drives");
        foreach (var drive in vm.Drives)
        {
            Add(drive, DriveRow(drive));
        }
        var places = DriveItem.FolderShortcuts(true, true, true, true).Where(p => p.RootPath.Length > 0).ToList();
        if (places.Count > 0)
        {
            Line();
            Header("Places");
            foreach (var place in places)
            {
                Add(place, PlaceRow(place, place.RootPath));
            }
        }
        Line();
        foreach (var special in DriveItem.SpecialEntries)
        {
            Add(special, PlaceRow(special, null));
        }

        DrivePopup.IsOpen = true;
        var first = selected ?? DriveList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is DriveItem);
        DriveList.SelectedItem = first;
        Dispatcher.UIThread.Post(() =>
        {
            DriveList.Focus();
            if (first is not null)
            {
                DriveList.ScrollIntoView(first);
                first.Focus();
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>Closes the list without choosing.</summary>
    public void CloseDriveBox() => DrivePopup.IsOpen = false;

    private static Avalonia.Data.BindingBase Resource(string key) => new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key);

    private static Control Icon(DriveItem drive)
    {
        var panel = new Panel { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        if (DriveIconConverter.Instance.Convert(drive, typeof(IImage), null, System.Globalization.CultureInfo.CurrentCulture) is IImage image)
        {
            panel.Children.Add(new Image { Source = image, Width = 16, Height = 16 });
        }
        else if (DriveIconConverter.Glyph.Convert(drive.Kind, typeof(Geometry), null, System.Globalization.CultureInfo.CurrentCulture) is Geometry glyph)
        {
            panel.Children.Add(new PathIcon { Data = glyph, Classes = { "driveicon" } });
        }
        return panel;
    }

    /// <summary>"C: System" / "484 GB free of 931 GB" / the used-space bar; the letter at the right.</summary>
    private static Control DriveRow(DriveItem drive)
    {
        var name = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        name.Inlines!.Add(new Avalonia.Controls.Documents.Run(drive.Name + " ") { FontWeight = FontWeight.SemiBold });
        name.Inlines.Add(new Avalonia.Controls.Documents.Run(drive.HasLabel ? drive.Label! : Core.Localization.Localizer.T(drive.KindText)));
        var texts = new StackPanel { Spacing = 1 };
        texts.Children.Add(name);
        if (DriveUsage.Known(drive))
        {
            texts.Children.Add(new TextBlock
            {
                Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} free of {1}",
                    Core.Formatting.SizeFormatter.Human(drive.FreeSpace), Core.Formatting.SizeFormatter.Human(drive.TotalSize)),
                FontSize = 11,
                [!TextBlock.ForegroundProperty] = Resource("NcFaintBrush"),
            });
            var bar = new Border { Classes = { "usage" }, Width = 100, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            bar.Classes.Set("low", DriveUsage.Low(drive));
            bar.Child = new Border { Width = Math.Round(100 * DriveUsage.Used(drive)) };
            texts.Children.Add(bar);
        }
        var letter = drive.Name.Length >= 2 && drive.Name[1] == ':' ? drive.Name[..1] : string.Empty;
        return Row(Icon(drive), texts, letter);
    }

    /// <summary>A place or Computer / Network / the trash: its name and, for a folder, where it is.</summary>
    private static Control PlaceRow(DriveItem item, string? where)
    {
        var texts = new StackPanel { Spacing = 1 };
        texts.Children.Add(new TextBlock { Text = item.IsSpecial && item.Kind is DriveKind.Computer or DriveKind.NetworkPlaces or DriveKind.Trash ? item.Label : item.ShortName,
            FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (where is not null)
        {
            texts.Children.Add(new TextBlock { Text = where, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" },
                [!TextBlock.ForegroundProperty] = Resource("NcFaintBrush") });
        }
        return Row(Icon(item), texts, string.Empty);
    }

    private static Avalonia.Controls.Grid Row(Control icon, Control texts, string key)
    {
        var grid = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto"), ColumnSpacing = 10 };
        Avalonia.Controls.Grid.SetColumn(texts, 1);
        var letter = new TextBlock
        {
            Text = key,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "noloc" },
            [!TextBlock.FontFamilyProperty] = Resource("NcMonoFont"),
            [!TextBlock.ForegroundProperty] = Resource("NcFaintBrush"),
        };
        Avalonia.Controls.Grid.SetColumn(letter, 2);
        grid.Children.AddRange([icon, texts, letter]);
        return grid;
    }

    private void OnDriveListKeyDown(object? sender, KeyEventArgs e)
    {
        var choices = DriveList.Items.OfType<ListBoxItem>().Where(i => i.DataContext is DriveItem).ToList();
        var index = DriveList.SelectedItem is ListBoxItem current ? choices.IndexOf(current) : -1;
        switch (e.Key)
        {
            // the headers and lines are skipped
            case Key.Down or Key.Up when choices.Count > 0:
                var next = choices[Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, choices.Count - 1)];
                DriveList.SelectedItem = next;
                DriveList.ScrollIntoView(next);
                next.Focus();
                e.Handled = true;
                break;
            case Key.Home or Key.End when choices.Count > 0:
                var edge = e.Key == Key.Home ? choices[0] : choices[^1];
                DriveList.SelectedItem = edge;
                DriveList.ScrollIntoView(edge);
                edge.Focus();
                e.Handled = true;
                break;
            case Key.Enter or Key.Space when DriveList.SelectedItem is ListBoxItem { DataContext: DriveItem drive }:
                PickDrive(drive);
                e.Handled = true;
                break;
            case Key.Escape or Key.Tab:
                DrivePopup.IsOpen = false;
                FocusList();
                e.Handled = true;
                break;
            case >= Key.A and <= Key.Z when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                var letter = (char)('A' + (e.Key - Key.A));
                if (choices.Select(c => c.DataContext).OfType<DriveItem>()
                        .FirstOrDefault(d => d.Name.Length >= 2 && d.Name[1] == ':' && char.ToUpperInvariant(d.Name[0]) == letter) is { } byLetter)
                {
                    PickDrive(byLetter);
                }
                e.Handled = true;
                break;
        }
    }

    private void PickDrive(DriveItem drive)
    {
        DrivePopup.IsOpen = false;
        if (_vm is null)
        {
            return;
        }
        Activated?.Invoke(this, EventArgs.Empty);
        _ = _vm.ChangeDriveAsync(drive);
        FocusList();
    }
}
