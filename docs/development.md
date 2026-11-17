# Development

[Back to the README](../README.md)

NextCommander is written in C# on .NET 10 with Avalonia 12.1 (Fluent theme). The UI texts in the code are
English; translations are applied at run time. The repository folder and the GitHub repository still carry the
former name `FileManager`.

Project notes for contributors (commands, structure, rules, known pitfalls): [CLAUDE.md](../CLAUDE.md).
Other project documents:

- [docs/history-and-decisions.md](history-and-decisions.md): history, decisions, lessons.
- [docs/remaining-work.md](remaining-work.md): what is not done yet.
- [docs/screenshots/](screenshots/): screenshots of the application (1920x1080), made by `tests/NextCommander.App.Tests/DocScreenshots.cs`: `FM_DOC_SCREENSHOTS=<folder> dotnet test tests/NextCommander.App.Tests --filter DocScreenshots` (Windows).

## Requirements

- .NET SDK 10.0.100 or a later feature band (`global.json`, `rollForward: latestFeature`).
- Packages come from nuget.org only (`nuget.config` with `<clear />`); versions are central in
  `Directory.Packages.props`.
- Nothing machine-specific is used: everything needed to build is in the repository.
- Optional:
  - Python 3: the `build/*.py` scripts and the `tests/servers` test servers.
  - 7-Zip (writing 7z), VLC (media playback in Lister), Everything / plocate / mdfind (indexed search).
    None is needed to build; without them the feature is turned off.

## Commands

```sh
dotnet build                                   # warnings are errors (TreatWarningsAsErrors)
dotnet test                                    # Core and App tests; App tests are headless Avalonia
dotnet run --project src/NextCommander.App
```

| Command | Purpose |
|---|---|
| `./build/publish.ps1 -Rid win-x64` | zip, portable zip and the setup program (`-setup.exe`, Inno Setup) under `dist/` |
| `./build/publish.ps1 -Rid linux-x64` | `.deb` and `.rpm` (nfpm), `.tar.gz` (with `.desktop` file and icons); an AppImage if `appimagetool` is present |
| `./build/publish.ps1 -Rid osx-arm64` | `NextCommander.app` (in `dist/<name>/`) and a zip of it; on macOS also a `.dmg`. libmtp (phones over MTP) goes into the app from Homebrew's bottles, on any system (needs Python 3; downloaded once under `artifacts/tools`) |
| `./build/windows/new-signing-certificate.ps1 -Trust` | code signing certificate for the setup program (`CN=NextCommander`, user store) and trusting it on this machine (UAC) |
| `./tests/servers/run-integration-tests.ps1` | remote tests against local FTP / SFTP / WebDAV / S3 / SMB servers (+ Azurite if Node.js is installed); needs Python |
| `python build/make_icons.py` | application icons (Assets) from the design files in `build/icons` |
| `python build/make_menu_icons.py src/NextCommander.App/Styles/MenuIcons.axaml` | menu glyphs |
| `python build/extract_strings.py missing.json` | UI texts of the code missing from en.json, with suggested keys (and literals still holding Turkish letters) |

Environment variables used by tests and tools:

| Variable | Effect |
|---|---|
| `FM_SCREENSHOT_DIR=<folder>` | Tests that take screenshots write PNGs there. |
| `FM_L10N_REPORT=<file>` | `LocalizationSurveyTests` lists texts left untranslated in Turkish mode. |
| `FM_TEST_FTP`, `FM_TEST_SFTP`, `FM_TEST_WEBDAV`, `FM_TEST_S3`, `FM_TEST_SMB`, `FM_TEST_AZURE` | Enable the remote integration tests (`scheme://user:pass@host:port/dir`; WebDAV `dav://`, S3 `s3://key:secret@host:port/bucket`, SMB `smb://...:4445/TEST`, Azure `azblob://account:key@host:port/container`). Otherwise they are skipped. |
| `FM_7Z` | Path of the 7-Zip program. |
| `FM_LIBVLC` | Path of LibVLC (VLC is not bundled). |
| `SIGN_CERT`, `SIGN_CERT_PASSWORD`, `SIGN_PUBLISHER` | A `.pfx` (and its password) for signing the setup program, or the subject of the certificate to take from the user store (default `CN=NextCommander`). The former `MSIX_*` names work too. |
| `PACKAGE_MAINTAINER` | The maintainer written into the `.deb` / `.rpm` (`Name <mail>`; default `NextCommander`). |
| `MACOS_SIGN_IDENTITY`, `MACOS_NOTARY_PROFILE` | macOS signing and notarization. |
| `MACOS_LIBMTP_DIR` | A folder of ready macOS libraries (libmtp.9.dylib and what it loads, with `@loader_path` names) to put into the app instead of Homebrew's bottles. |
| `FM_LIBMTP` | Path of the libmtp library the program loads (Linux, macOS). |

GitHub Actions (`.github/workflows/ci.yml`) builds and tests on the three platforms and uploads the packages.

## Project structure

| Project | Contents |
|---|---|
| `NextCommander.Core` | The UI-less core: VFS (`VfsPath` with the `archive://`, `sftp://`, `ftp://` … schemes, `IFileSystemProvider`, `ProviderRegistry`), operations (`CopyMoveJob`, `JobQueue`, `HttpDownloadJob`, `SpeedLimiter`), file mask matcher, command registry, search and indexes (`LocalFileIndex`, Everything / plocate / Spotlight), tools (checksums / BLAKE3, split / combine, encoding, Mark-of-the-web, update check, `DiskUsage`, `TreemapLayout`), text, columns, plugins (loader, `IPackerPlugin`, `IViewerPlugin`), localization (`Localizer`, base language `en`), config (`AppSettings`, `SettingsStore`, `ColorTheme`, `IconPack`). |
| `NextCommander.Vfs.Local` | Local file system provider (watching, links, volume detection). |
| `NextCommander.Vfs.Archive` | Archives: browsing like folders, changing zips in place, `PackJob`, test jobs, `SevenZipCli`. |
| `NextCommander.Vfs.Remote` | SFTP (SSH.NET), FTP/FTPS (FluentFTP), SMB (SMBLibrary, LGPL); WebDAV, S3 and Azure Blob without packages (HttpClient, `S3Signer` AWS SigV4, `AzureSigner` Shared Key, `AwsProfiles`); `RemoteProviderBase` (pool, login, certificate approval). `Cloud/`: Google Drive, OneDrive, Dropbox (`CloudProviderBase`, `CloudSession` token refresh, `OAuthClient` PKCE with the `localhost:53682` callback), tested against `FakeCloud`. |
| `NextCommander.Columns` | Image (MetadataExtractor) and audio / video (TagLib#) column providers. |
| `NextCommander.Platform` | Windows / Linux / macOS services: drives, recycle bin, opening with the default application, terminal, taskbar progress, network drive dialogs, `SecretStore`, the Explorer context menu, running elevated. |
| `NextCommander.App` | The Avalonia UI (MVVM). |
| `tests/NextCommander.Core.Tests` | xUnit unit tests. |
| `tests/NextCommander.App.Tests` | Avalonia.Headless UI tests. |
| `src/NextCommander.Vfs.Remote/Link` | PC link: `LinkProtocol` (frames in TLS, the pairing code), `LinkIdentity` (the installation's self-signed certificate, kept in the secret store), `LinkServer` (the shared folders; refuses paths and links leading out of them), `LinkClient` + `LinkFileSystemProvider` (`link://computer/share/...` on `RemoteProviderBase`), `LinkDiscovery` / `LinkResponder` (UDP probe to every interface's broadcast address incl. 169.254.x.x and ff02::1). The App side is `Services/LinkService.cs`. |
| `src/Plugins/NextCommander.Plugin.Rclone` | The rclone file system as a plugin shipped with the program: built with the application (not referenced) and copied with its `plugin.json` to `plugins/rclone` next to it, also in the publish folder (`NcShippedPlugin` items in `NextCommander.App.csproj`). |
| `tests/SamplePlugin` | An example plugin. |
| `tests/servers` | FTP, SFTP, WebDAV (wsgidav), S3 (moto) and SMB (impacket, port 4445) test servers; Azurite for Azure is installed into `node_modules`. |

Inside `NextCommander.App`:

- `MainWindow`, `PanelSideView` → `FilePanelView` → `PathBarView`; `ViewModels/*` (`MainWindowViewModel` and
  `.Menus.cs`).
- Tool windows: Lister, editor, find files, multi-rename, synchronize, compare, `DuplicatesWindow`,
  `DiskUsageWindow` with `Controls/TreemapControl`.
- `Services/Icons/`: `BrandIcons.cs` (connection type icons), `Flags.cs` (vector flags of the language list),
  `IconPacks.cs`, `FileKinds.cs`, `DriveIconConverter.cs`.
- `Services/MenuIcons.cs` (command → icon), `Services/ColorThemes.cs`, `Services/UiLocalization.cs`,
  `Services/TerminalSession.cs` + `Views/TerminalView.cs` (built-in terminal: `Services/Terminal/PseudoTerminal`
  on ConPTY / pty + `VtScreen`; fallback line console `TerminalConsole`), `Services/MediaSupport.cs`,
  `Services/Elevation.cs` (retrying an operation with administrator rights), `Services/HotkeyMap.cs`.
- `Localization/*.json` (copied to `Languages/` in the output), `Themes/*.json`, `IconPacks/*.json`.
- `Styles/NextCommanderStyles.axaml`: shared styles (`Button[IsDefault=True]` draws default buttons in the
  accent color; `Button.dialog` / `StackPanel.buttonrow` for OK / Cancel rows).

## Rules of the code base

- **Commands:** every action is a `nc_*` command in the `CommandRegistry` (user commands: `uc_*`). Menus, button
  bars, shortcuts (`Services/HotkeyMap.cs`) and the command line call the same commands. A new menu item needs a
  command registration, a `CommandParameter` in XAML, a `MenuIcons` mapping and an en.json / tr.json key.
- **Menus** keep a fixed order (details in [CLAUDE.md](../CLAUDE.md)).
- **Settings** live in `AppSettings`. Every setting in the Configuration window is a `SettingItem`; the window
  works on a copy and writes back only these items on Apply / OK. Lists edited as JSON (`JsonList<T>`) are
  validated (`ParseJsonList`, `ValidateEntry`).
- **No option that does nothing:** features without an implementation go to
  [remaining-work.md](remaining-work.md).

## Localization

- The base language is English: UI texts are written in English in the code.
- Language files map keys to texts. Keys have the form `Form.Item.Option`: `Menu.nc_X`, `Command.nc_X`,
  `Settings.<Page>.<Setting>[.Description]`, `Settings.<Page>.Group.<Group>`, `<Window>.<Text>`, `Common.*`
  (details in `build/l10n_keys.py`).
- At run time the English text in the code is linked to its key through en.json, and the key to the translation
  (`Localizer.Join`). The same English text has one key. `{0}` patterns cover interpolated texts.
- No call is needed in the code; a TextBlock hook translates. File and folder names are not translated: the
  classes `cell`, `crumb`, `tab`, `noloc` and anything inside `TreeViewItem` / `DataGridCell` are excluded.
- New text: add the key to `en.json` (English) and `tr.json` (Turkish). `python build/extract_strings.py missing.json`
  suggests keys; a test checks that both files have the same keys.
- Language files are read from `Languages/` next to the program and `languages/` in the settings folder (the
  latter wins). The "System" setting picks the OS language when a file exists for it, otherwise English.

## Themes and icon packs

- Color themes are JSON files (`Themes/` in the program, `themes/` in the settings folder) with `Name`, `Base`
  (Light / Dark) and `Colors` (theme key → color). `ColorThemes.Keys` maps theme keys to Fluent palette colors or
  `Fm*` resources. "Follow the system" uses the system colors in light mode and `dark.json` in dark mode, and
  switches live. `nc_SetColorScheme <name>` selects a theme (`system` = follow the system).
- Icon packs are JSON files (`IconPacks/` / `iconpacks/`) with `Name`, `Style`, `Color`, `Badges`, `Colors` and
  `Glyphs` (SVG path data on a 16×16 grid, keyed by resource names such as `FmFolderIcon`, `MiCopy`). The
  built-in glyphs are generated into `Styles/MenuIcons.axaml` by `build/make_menu_icons.py`.
- User guides: [themes](guide/themes.md), [icon packs](guide/icon-packs.md), [languages](guide/languages.md).

## Plugins

The full guide for using and writing plugins: [guide/plugins.md](guide/plugins.md). In short:

- Location: `<settings folder>/plugins/<name>/plugin.json` or the `plugins` folder next to the program.
- `plugin.json`: `Name`, `Assembly` (the main assembly, relative to the folder), optional `Description`,
  `Version`.
- Through the `NextCommander.Core` interfaces a plugin can add file systems (`IFileSystemProvider`), columns
  (`IColumnProvider`), archive formats for Alt+F5 (`IPackerPlugin`) and Lister views (`IViewerPlugin`: HTML,
  text or its own control).
- A file system plugin can offer a place to start (`IPluginEntryPoint`: name, group, root folder, why it cannot
  open now): it is listed in the Plugins menu of the button bar (`nc_PluginsMenu`, `Services/ConnectionMenus.cs`
  `PluginMenus`) under its group. Plugin file systems that are `IDisposable` are disposed when the program ends.
- Each plugin is loaded in its own load context and cannot load assemblies outside its folder.
- *Configuration > Plugins...* lists the loaded plugins and errors; the `DisabledPlugins` setting turns one off.
- Example: `tests/SamplePlugin` (a column and a `hello://` file system).

## Settings file

- `settings.json` in the settings folder (`%APPDATA%\NextCommander`, `~/.config/NextCommander` or
  `$XDG_CONFIG_HOME`, `~/Library/Application Support/NextCommander`); a `portable.json` next to the executable
  switches to the executable's folder. See `SettingsStore`.
- A corrupt file is kept as `settings.json.corrupt` and the program starts with defaults.
- Shortcuts can be overridden in `Hotkeys`, e.g. `"Ctrl+F5": "nc_Copy"`; an empty value removes the default.
- `Associations`: mask → program (with `%P%N` parameters), a `nc_` command, `lister`, `editor` or `system`.
  `EditorAssociations` does the same for F4 (`{"Mask": "*.docx", "Program": "system"}`); `UseInternalEditor: false`
  turns the internal editor off.
- `QuickSearchTrigger` (`CtrlAltLetter` / `LetterOnly`) and `QuickSearchMode` (`Prefix` / `Anywhere` / `Fuzzy`).
- Logs go to `logs/` in the settings folder; a startup crash is written to `logs/crash.log` (on macOS also
  `~/Library/Logs/NextCommander/crash.log`).

## Command line and parameters

- Program arguments: `NextCommander <folder|file>`, `--lister <file>`, `--edit <file>`. With the single-window
  setting on (default), a second start passes its arguments to the running window (mutex + named pipe) and exits.
  `--elevated <request.json> <result.json>` is the internal helper started with administrator rights.
- Internal commands on the command line: `cd path` (`cd path\:` puts the cursor on the item), `md a\b`,
  `nc_*` commands with parameters (`nc_PanelsSplitPercent 30`, `nc_NewConnection sftp`,
  `nc_OpenConnection <name>`, `nc_SetColorScheme dark`), `LOADLIST file`, `LOADSEARCH name`, `OPENTABS file`,
  `SAVETABS`, environment variable substrings `%v:~2,3%`.
- Button and user command (`uc_name`) parameters: `%P %T %N %M %O %E %S %R %L %F %UL %WL %B %B+1 %C1 %C2 %Q %Z
  %$DATE+1D% %$CLIPBOARD% %|ENV|`, substrings `%N:~0,3`; a leading `?` asks for the parameters; a `*` before the
  program runs it as administrator.
- Hotlist entries: `Group/Name` makes submenus; `%NC_PATH%` makes relative paths.

## Technical notes

- **Saved passwords:** `secrets.json`, AES-256-GCM. With a master password the key is derived with PBKDF2
  (600,000 rounds); otherwise it is protected with DPAPI on Windows and with a user-only file elsewhere.
- **Host keys / certificates** (SFTP, FTPS, WebDAV, S3, Azure over HTTPS) are confirmed by fingerprint on first
  use and saved in the settings.
- **Connection pool:** at most 3 connections per server; listing does not wait for running copies.
- **WebDAV times:** kept with PROPPATCH (Nextcloud / ownCloud `lastmodified`, writable `getlastmodified`, IIS
  `Win32LastModifiedTime`); the method that works is found on the first file and remembered per server.
- **S3 / Azure times:** stored in `x-amz-meta-mtime` / `mtime` metadata (as rclone does); read for folders with at
  most 300 files. Files over 64 MB are uploaded in parts. Renames are copy + delete.
- **Cloud drives:** OAuth 2.0 with PKCE; the refresh token goes to the encrypted secret store when "Remember the
  sign-in" is on. Uploads of large files use upload sessions.
- **Network view** (`network:///`): ARP neighbors, mDNS / Bonjour, and a short probe of ports 22, 21, 445, 80, 443
  on the local subnets (at most /24); cached for a minute.
- **Linux drives:** read with `lsblk`; unmounted partitions are mounted with `udisksctl`.
- **Copy verification:** the source is hashed with BLAKE3 while copying, then only the target is read.
- **Zip safety:** entries escaping with `../` or absolute paths are anchored at the target (no zip slip). Packing
  writes a `.part` file first.
- **File index:** stored in `<settings folder>/index/files.idx`. Measured on about 1 million files / 155 thousand
  folders: first scan about 3 minutes, search 20–80 ms, folder size about 30 ms, about 120 MB of memory, a 44 MB
  file. On Linux, if the inotify watch limit is exceeded, it refreshes every 10 minutes.
- **Native dependencies:** BLAKE3 has a native library (embedded in the single-file publish). LibVLC is not
  bundled; the system's VLC is used (or `FM_LIBVLC`).
- **Elevation:** an operation refused for missing rights is written to a request file, and the program starts
  itself again through UAC (Windows), `pkexec` / polkit (Linux) or the macOS authorization dialog, performs only
  that operation without a window, and writes back the result.

## Packaging

The outputs of `build/publish.ps1` are single-file, self-contained .NET applications; no .NET installation is
needed on the target. The portable zip contains `portable.json`: settings, passwords and logs stay in the
program's folder.

### Windows (setup program)

`build/windows/NextCommander.iss` is an Inno Setup 7 script (Inno Setup 6 cannot compile it); `publish.ps1` compiles
it with the `ISCC.exe` of an installed Inno Setup 7, or else from the `Tools.InnoSetup` 7.1.0 NuGet package
(downloaded once under `artifacts/tools`). The setup program itself is 64-bit (`SetupArchitecture=x64`; on Windows
on Arm it runs emulated) and its wizard follows the light / dark mode of Windows (`WizardStyle=modern dynamic`).
The setup installs per user by default (`{autopf}` = `%LOCALAPPDATA%\Programs`, no administrator rights; "all
users" can be chosen), in English, Turkish, German, Spanish, French or Simplified Chinese, with a Start menu entry and the optional tasks desktop icon and
"Open in NextCommander" (folders, folder background, drives; `HKA\Software\Classes`). A running NextCommander is
closed through the Restart Manager before its files are replaced. Uninstalling keeps the settings folder. The
setup is signed when a certificate is there: `build/windows/new-signing-certificate.ps1` creates a self-signed
`CN=NextCommander` code signing certificate in the user store (no password, no .pfx; `-Trust` also trusts it on this
machine), or `SIGN_CERT` names a `.pfx`. `signtool` comes from an installed Windows SDK, or else from the
`Microsoft.Windows.SDK.BuildTools` NuGet package (downloaded once under `artifacts/tools`, only when there is
something to sign). The MSIX package was dropped on 15 November 2026 (user's request): the setup program covers it.
Silent use:
`NextCommander-<ver>-win-x64-setup.exe /VERYSILENT /CURRENTUSER [/DIR=...] [/TASKS=desktopicon,explorermenu]`.

### macOS

If the macOS package was built on Windows or Linux, only the program is signed (ad hoc), and macOS refuses to
open a downloaded copy as "damaged". On the Mac:

```sh
xattr -cr /Applications/NextCommander.app
codesign --force --deep -s - /Applications/NextCommander.app
```

A package built on a Mac is signed as a whole (`codesign -s -`). A copy not signed with an Apple Developer
identity and notarized still needs *Privacy & Security > Open Anyway*. With `MACOS_SIGN_IDENTITY` and
`MACOS_NOTARY_PROFILE` the app and the `.dmg` are signed and notarized.

### Linux

The `.deb` and `.rpm` are made by [nfpm](https://nfpm.goreleaser.com) from `build/linux/nfpm.yaml`: an installed
`nfpm`, or else its release 2.48.0 for the machine that packs (Windows, Linux or macOS; downloaded once under
`artifacts/tools` and checked against the release's `checksums.txt`). So both packages are made on Windows too,
without `dpkg-deb` or `rpmbuild`. They install:

| Path | |
|---|---|
| `/opt/nextcommander/` | The program (`NextCommander`, languages, themes, icon packs, `plugins/`). |
| `/usr/bin/nextcommander` | A link to the program. |
| `/usr/share/applications/nextcommander.desktop` | The menu entry (`Exec=nextcommander %F`). |
| `/usr/share/icons/hicolor/<size>/apps/nextcommander.png`, `scalable/apps/nextcommander.svg` | The icons. |

The package scripts (`build/linux/postinstall.sh`, `postremove.sh`) refresh the desktop database and the icon cache;
`.gitattributes` keeps them with LF line ends, and `publish.ps1` converts them again before packing. The dependencies
are ICU, fontconfig and X11 (Debian / Ubuntu names in the `.deb`, Fedora / RHEL names in the `.rpm`); the program is
self-contained otherwise. The settings in `~/.config/NextCommander` stay when the package is removed.

The `.tar.gz` contains a `.desktop` file and the `icons/hicolor/<size>/apps/nextcommander.png` and
`hicolor/scalable/apps/nextcommander.svg` icon theme (`usr/share/icons` in the AppImage).

### Application icon

The design files are in `build/icons`: `nextcommander.ico` (Windows, 16–256 px), `nextcommander.icns` (macOS,
16–1024 px, including Retina), `png/<size>.png` (16–1024 px) and `nextcommander.svg` (scalable Linux icon).
`python build/make_icons.py` puts them into `src/NextCommander.App/Assets` (exe icon, window icon, macOS bundle)
When the icon changes, update `build/icons` and run the script again.

## Roadmap

All phases of the original plan are done:

0. Skeleton
1. Core dual panel
2. Tabs, history, hotlist, quick search / filter, button bar, branch view, thumbnails
3. Lister, Quick View, editor, find files
4. Archives
5. Multi-rename, directory synchronization, content comparison, checksums
6. FTP / SFTP, plugins, custom columns
7. Polish and distribution (including the English UI)

What is left is tracked in [remaining-work.md](remaining-work.md).
