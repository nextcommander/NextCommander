# NextCommander — project notes

A cross-platform (Windows / Linux / macOS) dual-pane file manager with keyboard-driven panels. For personal use.
C# / .NET 10, Avalonia 11.3, Fluent theme. The UI language of the code is English; the translations (Turkish, German, Spanish, French, Chinese; others as user files) are applied at run time.
Formerly FileManager (renamed NextCommander on 8 November 2026). The settings folder is `NextCommander`; if it is missing, the old `FileManager` folder is copied on first start (`SettingsStore.CopyLegacyFolder`).
Commands are named `nc_*` (built in) and `uc_*` (user commands); the names of earlier versions (`cm_*`, `em_*`, `%COMMANDER_PATH%`) are converted when settings are read and still run (`Core/Commands/LegacyNames.cs`).

This file gathers what is needed to continue the project on another machine or in a new session. Details:
- Features and usage: [README.md](README.md)
- History, decisions, lessons: [docs/history-and-decisions.md](docs/history-and-decisions.md)
- Remaining work: [docs/remaining-work.md](docs/remaining-work.md)
- Screenshots of the app: [docs/screenshots/](docs/screenshots/)

## Requirements
- .NET SDK 10.0.100 or a later feature band (`global.json`, `rollForward: latestFeature`).
- Packages come from nuget.org only (`nuget.config`, `<clear />`). Versions are central: `Directory.Packages.props`.
- Optional:
  - Python 3: for the `build/*.py` scripts and the `tests/servers` test servers.
  - 7-Zip (to write 7z), VLC (media playback in the Lister), Everything / plocate / mdfind (indexed search). None is needed to build; without them the feature is off.

## Commands
```sh
dotnet build                                   # warnings are errors (TreatWarningsAsErrors)
dotnet test                                    # Core ~256, App ~206 tests; App tests are headless Avalonia
dotnet run --project src/NextCommander.App
./build/publish.ps1 -Rid win-x64               # zip, portable zip, .msix and -setup.exe (Inno Setup, build/windows/NextCommander.iss) under dist/
                                               # (without the SDK / Inno Setup, makeappx and ISCC come from NuGet: BuildTools, Tools.InnoSetup)
./build/windows/new-msix-certificate.ps1 -Trust # MSIX signing certificate (CN=NextCommander, user store) + trust it on this machine (UAC)
./tests/servers/run-integration-tests.ps1      # remote tests against local FTP/SFTP/WebDAV/S3/SMB (+ Azurite if Node is there) servers (needs Python)
python build/make_icons.py                     # application icons (Assets) + MSIX logos from the design files in build/icons
python build/make_menu_icons.py src/NextCommander.App/Styles/MenuIcons.axaml   # menu glyphs
python build/extract_strings.py missing.json   # UI texts of the code missing from en.json, with suggested keys (and literals still holding Turkish letters)
```
Tests that take screenshots write PNGs when `FM_SCREENSHOT_DIR=<folder>` is set.
With `FM_L10N_REPORT=<file>`, `LocalizationSurveyTests` lists texts left untranslated in Turkish mode.
The remote integration tests are skipped unless `FM_TEST_FTP` / `FM_TEST_SFTP` / `FM_TEST_WEBDAV` / `FM_TEST_S3` / `FM_TEST_SMB` / `FM_TEST_AZURE` are set (`scheme://user:pass@host:port/dir`; WebDAV `dav://`, S3 `s3://key:secret@host:port/bucket`, SMB `smb://...:4445/TEST`, Azure `azblob://account:key@host:port/container`).

## Structure
- `src/NextCommander.Core`: the core without UI.
  - Vfs: `VfsPath` (`archive://`, `sftp://`, `ftp://` schemes), `ProviderRegistry`.
  - Operations: `CopyMoveJob`, `JobQueue`, `HttpDownloadJob`, `SpeedLimiter`.
  - Search (indexes included: `LocalFileIndex` the application's own file index (`FileIndexes.Local`, in the App `Services/FileIndexService`), Everything / plocate / Spotlight), Tools (checksums/BLAKE3, split/combine, encoding, Mark-of-the-web, update check), Text, Columns, Plugins (loader + `IPackerPlugin` / `IViewerPlugin`), Localization (`Localizer`, `Localizer.BaseLanguage` = "en"), Config (`AppSettings`, `ColorTheme`, `IconPack`).
- `src/NextCommander.Vfs.Local|Archive|Remote`: file system providers. Archive side: `PackJob`, `SevenZipCli`; remote side: SSH.NET, FluentFTP, SMBLibrary (SMB, LGPL); WebDAV, S3 and Azure Blob without packages (HttpClient, `S3Signer` AWS SigV4, `AzureSigner` Shared Key, `AwsProfiles`; `RemoteProviderBase` shared: pool, login, certificate approval). `Cloud/`: Google Drive, OneDrive, Dropbox (`CloudProviderBase`, `CloudSession` token refresh, `OAuthClient` PKCE + `localhost:53682` callback; the user registers the app id, `RemoteConnection.ClientId`); tested with `FakeCloud` (a fake API). Google has the virtual root folders "Shared with me" / "Shared drives", OneDrive "SharePoint" with `IncludeSharePoint`.
- `src/NextCommander.Platform`: Windows / Linux / macOS services (recycle bin, drives, taskbar progress, network drive dialogs, `SecretStore`).
- `src/NextCommander.Columns`: image and media column providers.
- `src/NextCommander.App`: the Avalonia UI.
  - `MainWindow`, `PanelSideView` → `FilePanelView` → `PathBarView`; `ViewModels/*` (`MainWindowViewModel` + `.Menus.cs`).
  - Tool windows (Lister, editor, find files, multi-rename, synchronize, compare, duplicate files `DuplicatesWindow`, folder sizes `DiskUsageWindow` + `Controls/TreemapControl`; in the core `Tools/DiskUsage.cs` scan and `TreemapLayout`).
  - `Services/Icons/`: `BrandIcons.cs` (connection type icons; cloud product marks as vectors), `Flags.cs` (vector flags of the language list), `IconPacks.cs` (the icon pack in use: glyphs and colors of the built-in icons), `FileKinds.cs`, `DriveIconConverter.cs`, `SystemFolderIcons.cs` (the operating system's icon of the user folders (`KnownFolders.KindOf`: home, documents, downloads, desktop, pictures, music, videos) on tabs, file list, tree and drive bar: Windows shell icon, Linux icon theme (`FreedesktopIconTheme`, SVG drawn with Svg.Skia), macOS NSWorkspace).
  - `Services/MenuIcons.cs` (command → icon, drawn through the icon pack), `Services/ColorThemes.cs` (system colors without a theme file; "system" follows the OS light/dark mode; `Keys`: theme key → Fluent palette color or Fm* resource), `Services/UiLocalization.cs` (language files), `Services/TerminalSession.cs` + `Views/TerminalView.cs` (built-in terminal: tabs `TerminalTab`; `EmulatedTerminal` = `Services/Terminal/PseudoTerminal` (ConPTY / pty) + `VtScreen` (VT100/xterm), fallback line console `TerminalConsole`), `Services/MediaSupport.cs`.
  - `Localization/*.json` (`Languages/` in the output), `Themes/*.json` (color themes; `dark.json` the dark theme "Dark", `macos.json` / `macos-dark.json` macOS, `ubuntu.json` / `ubuntu-dark.json` Ubuntu (Yaru) look), `IconPacks/*.json` (`colored.json` default, `monochrome.json`, `fluent.json`). The user's own languages, themes and icon packs go to `languages/`, `themes/`, `iconpacks/` in the settings folder.
  - `Styles/NextCommanderStyles.axaml`: shared styles; `Button[IsDefault=True]` draws every default (Enter) button in the accent color, `Button.dialog` / `StackPanel.buttonrow` for OK / Cancel rows.
- `tests/`: `NextCommander.Core.Tests`, `NextCommander.App.Tests` (headless), `SamplePlugin`, `servers/` (FTP, SFTP, WebDAV (wsgidav), S3 (moto), SMB (impacket, 4445) test servers; Azurite for Azure is installed into `node_modules`).

## Rules
- Every action is a `nc_*` command (`CommandRegistry`); user commands are `uc_*`. Menus, button bars, shortcuts (`Services/HotkeyMap.cs`) and the command line call the same commands. A new menu item = command registration + `CommandParameter` in XAML + `MenuIcons` mapping + a key in every language file (en, tr, de, es, fr, zh).
- The menu order: Files, Mark, Commands, Net, Show, Configuration, Start, Help on the right, with one addition: an **Edit** menu after Files holding the F-key commands (view, edit, copy, move, new folder, delete, clipboard; user's request, 10 November 2026). At the user's request: in Files, related commands sit in submenus; Show gathers similar options in submenus, with Navigation / Tabs directly in the menu; the Net menu is ordered by connection type; Plugins are in Configuration, and Configuration lists its extra commands directly (no "Other" submenu); Export / Import settings follow Save settings in its first group (user's request, 11 November 2026). New commands go at the end of their menu or into its "Other" submenu where one exists. A few items stay without an icon on purpose (`MenuIcons.WithoutIcon`).
- File list right click: Windows Explorer's own menu (`ShellContextMenu`, setting `SystemContextMenu`, default on); Shift+right click and other systems: the application's menu.
- Operations that fail for missing rights offer to run again with administrator / root rights: the app starts itself as `--elevated request.json result.json` through the system's consent dialog (`Services/Elevation.cs`, `IPlatformService.RunElevatedAsync`).
- UI texts are written in English in the code. The language files map keys to texts, key `Form.Item.Option` (`Menu.nc_X`, `Command.nc_X`, `Settings.<Page>.<Setting>[.Description]`, `<Window>.<Text>`; details in `build/l10n_keys.py`). New text: add the key to `en.json` with the English text and to every shipped translation with its own text: `tr.json`, `de.json`, `es.json`, `fr.json`, `zh.json` (`build/extract_strings.py` suggests keys; a test checks all files have the same keys and keep the `{0}` holes; zh: menu access keys as "视图(_V)"). At run time the English text in the code is linked to its key through en.json, the key to the translation (`Localizer.Join`); the same English text has one key. `{0}` patterns cover interpolated texts. No call is needed in the code; the TextBlock hook translates. File and folder names are not translated: the classes `cell`, `crumb`, `tab`, `noloc` and anything inside `TreeViewItem` / `DataGridCell` are excluded. Default language "system": Turkish on a Turkish system, otherwise English.
- Settings live in `AppSettings`. Every setting edited in the settings window is a `SettingItem`; the window works on a copy and writes back only these items on Apply/OK. Lists are edited with controls (`ListItem<T>` / `TextListItem` → `Views/Dialogs/ListEditor.cs`: entries left, fields of the selected one right; plugins as a checklist, view modes through `ColumnSetDialog`); entries are checked with `ValidateEntry` and OK / Apply refuse while one is invalid (`InvalidJsonClass`). No setting is edited as raw JSON text.
- No option or check box that does nothing; features without an implementation go to `docs/remaining-work.md`.
- UI rules (user's request, 12 November 2026):
  - **Icons in every menu:** every menu item that can have an icon gets one: the main menu, all context menus (file list, tabs, button bars, tree ...), the menus of tool windows (editor, Lister ...) and new menus. Icons come from `Services/MenuIcons.cs` (commands by their `nc_` name, a window's own items by a key such as `editor.save`), are drawn through the icon pack, follow the "icons in the menus" setting (`ShowMenuIcons`) and are set when the menu opens (`Opening` and `Opened`: a menu opened in code gets no `Opening`). Plain choice lists (encoding names, folder lists) and the main-menu items left without an icon on purpose (`MenuIcons.WithoutIcon`) stay without one.
  - **No JSON for the user:** the user never types JSON by hand. Every setting, list or structured value gets a UI (list editor, dialog, check boxes, pickers); JSON stays the storage format of the settings and themes only.
- Commit only when the user asks. The main branch is `main`.

## Known pitfalls (experienced in this project)
- **Bash heredoc escapes:** heredocs / quoted Python in the Bash tool can mangle `\n` and `\\` escapes (real line breaks in C# strings, a `\U` error, a real NUL byte instead of `"\\0"`, backslashes in paths halved). For C# / XAML edits use the Edit/Write tools or a Python patch file written with `Write`; for C# strings with escapes (`\x1b`, `\0`) only Edit/Write.
- **C# `\x` escape:** `"\x1bb"` is a single character (`\x1BB`); when a letter follows ESC, write `\u001b`.
- **ConPTY:** if the parent process's I/O is redirected (test host, IDE), the shell inherits those handles and exits at once; `STARTUPINFO.dwFlags = STARTF_USESTDHANDLES` (with empty handles) is needed.
- **Main window keys:** `MainWindow.OnWindowKeyDown` catches shortcuts in the tunnel phase; while the focus is in `TerminalScreen` only `nc_ToggleTerminal` runs, everything else goes to the shell.
- **Shared appearance state:** the color theme, the panel font, the icon pack and `Localizer.Current` are application-wide. Headless tests interleave on the same UI thread. Test classes that change them must be in `[Collection("Appearance")]` (`AppearanceCollection.cs`, not parallel).
- **Fluent palettes:** a `ColorPaletteResources` removed from the palette keeps its ownership and cannot be added again ("already has a parent"). `ColorThemes` therefore builds new palette objects on every switch.
- **ItemsControl measuring:** in the path line an `ItemsControl` was sometimes measured at width 0 before its items existed and stayed so. The path crumbs are now built in code by `PathBarView.RebuildCrumbs`.
- **Lost bindings:** writing `IsVisible = ...` in code removes the XAML binding. To hide controls bound to a setting, use a separate container (see `PathBarView` `CrumbArea`).
- **Root resource priority:** the root keys of `Application.Resources` are read before the theme dictionaries; the color theme writes the Fm* brushes there.
- **Closing the running app:** never kill the process. Close the window properly (`CloseMainWindow`); the single-window lock (mutex + named pipe) relies on it. If the app runs from bin/Debug, ask the user to close it before building.
- **Plain `[Fact]` and Avalonia:** in the App tests, a test that touches a static member of an Avalonia control class (e.g. `FilePanelView.X`, `ButtonBarControl.Y`) must be `[AvaloniaFact]` / `[AvaloniaTheory]`; a plain `[Fact]` runs on the thread pool and can start the dispatcher on the wrong thread, after which a random test fails in 1 ms with `Dispatcher.PushFrame: PlatformNotSupportedException`.
- **Timing tests:** they slow down under the full suite; keep limits generous (e.g. 8 s in the single-window test).
- **Windows PowerShell 5.1:** with `$ErrorActionPreference = 'Stop'`, native tools that write to stderr (pip, git) count as failing.
- **Single-file publish:** `Assembly.Location` is empty in the published program and the analyzer makes it an error (IL3000) only in `dotnet publish`, not in `dotnet build`. Use `AppContext.BaseDirectory` (`AppRestart.ProgramDll()`). Run `./build/publish.ps1 -Rid win-x64` (or at least `dotnet publish -r win-x64 -p:PublishSingleFile=true`) before a release.
- **Native dependencies:** BLAKE3 contains a native library (embedded in the single-file publish). LibVLC is not bundled; the system's VLC is used (or `FM_LIBVLC`).
- **Text conversions over the code:** interpolated strings and string literals nested in interpolation holes need a real C# scanner; a conversion script run more than once also converts test data (Turkish file names, contents), so exclude data files.
