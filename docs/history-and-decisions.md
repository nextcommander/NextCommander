# Development history and decisions

Developed with Claude Code in November 2026. This file keeps the information from the session notes inside the project.

## Initial decisions (user)
- **Goal:** A keyboard-driven, cross-platform dual-pane file manager for personal use.
- **Technology:** C# .NET 10 + Avalonia (11.3).
- **Look:** Classic dual-pane layout and shortcuts, modern theme, dark and light mode.
- **Scope:** The main feature groups of a dual-pane file manager, in phases.

## Phases
| Phase | Content |
|---|---|
| 0 | Solution, central packages, DI, Serilog, settings, theme, CI, main window skeleton |
| 1 | Dual panel: detailed/brief view, sorting, color rules, selection, F5–F8, JobQueue and progress, clipboard, drag and drop, folder size |
| 2 | Tabs, history, hotlist, quick search and filter, command line, button bars, parameters, branch view, tree, LOADLIST, thumbnails |
| 3 | Lister (text, hex, image), editor, find files, duplicate file finder |
| 4 | Archives: entering like folders, nested archives, pack/unpack/test, encrypted zip. Sequential cache for tar.gz (60 s → 0.7 s) |
| 5 | Multi-rename, synchronize folders, compare by content, checksums, split/combine, encoding, attributes, descriptions |
| 6 | SFTP/FTP(S), connection manager, master password (`SecretStore`), plugins, column providers, column sets |
| 7 | Settings window, single window + IPC, packaging (win/linux/osx), icons. English was deliberately skipped in that phase and added later |

## Next steps (same sessions)
- **Menus:** The Files, Mark and Commands menus were reorganized.
- **Look:**
  - The dark theme was switched to the VS Code Dark+ palette.
  - Colored icons were added to the menus. The glyphs are generated with `build/make_menu_icons.py`.
- **Remaining work round:**
  - BLAKE3, verification by hashing while copying, and speed limit.
  - HTTP download (resuming with Range).
  - Mark-of-the-web.
  - 7z writing (with the 7-Zip program).
  - HTML / Markdown / RTF preview in Lister (Markdig, Avalonia.HtmlRenderer, our own RTF converter).
  - Indexed search (Everything / locate / mdfind).
  - Packer and viewer plugin interfaces.
  - Windows taskbar progress, update checker.
  - LibVLC media player.
  - English UI.
  - MSIX/DMG packaging steps.
- **Layout:**
  - Main window: drive buttons, `[label] … k / … k free` line, tabs, `▾ path\*.* * ▾` path line, `[folder]`, vertical button bar.
  - Menus: Net / Show / Configuration / Start / Help. Also the configuration window.
- **Office color themes:** White, Colorful, Dark gray, Black; Word / Excel / PowerPoint / Outlook / OneNote accent color.

## 10 November 2026
- **English as the base language:** English became the base language of the code and the language files: UI texts in the code are English, `en.json` is the base language file and `tr.json` the Turkish translation. The existing UI texts were converted with a one-off script through tr.json → key → en.json. The default language "system" means Turkish on a Turkish system, English otherwise.
- **Documentation:** The docs were translated to English; `kalan-isler.md` became [remaining-work.md](remaining-work.md) and `gecmis-ve-kararlar.md` became `history-and-decisions.md`.
- **Color theme:** "Follow the system (light / dark)" became the default. Old settings files without a theme keep the system colors.
- **Function keys:** Only divider lines between the keys; the keys take the window's background.
- **Language list:** Shown with vector flags.
- **JSON settings lists:** Validated (syntax errors with line and column, unknown fields, values); OK and Apply refuse invalid lists.
- **Default buttons:** A global style draws every default (Enter) button in the accent color.
- **Icon packs:** Colored, Monochrome, Fluent. JSON files in `IconPacks` and `<settings>/iconpacks`; styles Colored / Monochrome / Outline, palette and glyph overrides.

## Important design decisions and their reasons
- **Single URI scheme (`VfsPath`):** Local, archive (`archive://<container>!/path`), SFTP and FTP are addressed through the same interface. That way every copy combination between archive ↔ FTP ↔ local works.
- **Archive libraries:** SharpCompress cannot read the AES zips that SharpZipLib writes. So encrypted zip entries are read with SharpZipLib.
- **7-Zip CLI for 7z writing:** .NET has no reliable 7z writer. Without the program, the 7z format does not appear in the list.
- **VLC is not bundled:** The native libraries take 150–200 MB (the distribution is ~48 MB). The system's VLC is used; without it, Lister's info screen states the reason.
- **Localization without touching call sites:** There were more than 1000 texts in 64 files. A gettext-style dictionary (key = Turkish text, `{0}` patterns) and a TextBlock hook were chosen. New texts are found with `build/extract_strings.py`; `LocalizationSurveyTests` lists those missed in English mode. (Since 10 November 2026 the code texts are English and en.json is the base; see above.)
- **The settings window works on a copy:** Only the settings edited in the window are written back. That way the panel and tab state is not affected by Apply.
- **No ineffective options:** options without an implementation are not put in the UI; they are written to [remaining-work.md](remaining-work.md).
- **Single window:** Named mutex + named pipe. The listener starts early and buffers incoming requests until the window is ready. Checking the pipe with `File.Exists` was not reliable.
- **Secrets (`SecretStore`):** AES-256-GCM. The key is derived with PBKDF2 (600k) when there is a master password; otherwise DPAPI on Windows, and a file with 0600 permissions on Unix.
- **Office themes:** The Fluent palette is replaced at runtime with a new object; the Fm* brushes are written to the root of the application resources.

## Important bugs fixed (so as not to fall into them again)
- Race when showing a folder in the tree: overlapping reveals overwrote a bool flag. Solved with a counter + generation check.
- The MinValue date of implicit folders in archives crashed copying. Dates before 1980 are skipped; implicit folders take the archive's date.
- Writing tar.zst was not supported: the output was wrapped in a ZStandard stream.
- Non-ASCII passwords on FTP: the client encoding was set to UTF-8.
- Remote connection errors showed `<Read error!>`: the IOException is shown as the message, an authorization error becomes a read error line.
- Single window "pipe busy" log flood: listening stops on ERROR_PIPE_BUSY; the forwarding handler runs in the background.
- "editor" did not find "editör" in the settings search: accent folding (`SettingsWindow.Fold`).
- Converting UI texts to English: interpolated strings and nested string literals need a real C# scanner; regular expressions are not enough.
- Running a text-conversion script more than once re-converts test data: data files must be excluded.
- The path line disappearing (ItemsControl measurement), the Office palette that could not be re-added, shared appearance state breaking tests: [CLAUDE.md](../CLAUDE.md) "Known pitfalls".

## Verification status
- `dotnet test`: Core 195, App 114 tests pass; the build has no warnings.
- Remote tests pass against local FTP/SFTP servers with `tests/servers/run-integration-tests.ps1`.
- **Not testable on this machine:**
  - Real playback with VLC.
  - MSIX (no Windows SDK) and DMG (needs macOS).
  - Real Everything / locate / mdfind indexes.
  - Using the Windows network drive dialogs on a real network.
- The Windows distribution passed a smoke test: single window forwarding, `--find`, clean shutdown. No manual run on Linux and macOS (built and tested in the CI matrix).
