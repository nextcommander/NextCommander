# Remaining work

## Options not in the Configuration window yet

The items below do not exist yet. They were not added to the window, so that no checkbox does nothing.

### Layout — done
- [x] Flat icons — for the drive buttons.
- [x] Open the breadcrumb automatically on mouse hover.
- [x] Command line only when it has the focus.
- [x] Choice between two separate drive button bars or one shared bar.

### Display — done
- [x] Show the parent folder `[..]` in drive roots too.
- [x] Append `\` to folder names.
- [x] Choice of sort method: natural, alphabetical (considering accents), strict character code order.
- [x] Sorting of folders: always by name / like files (also by date).
- [x] Tree: always load the whole folder tree; Enter in the tree opens the folder in the other panel.
- [x] Help texts: turning off tooltips, volume label in the drive list, showing a file name that does not fit and the description (descript.ion) in a tooltip.
- [-] Old 8.3 names — out of scope: specific to old Windows file systems, no cross-platform counterpart.

### Icons — done
- [x] Icon mode: all associated + EXE/LNK / associated only / standard only / no icons.
- [x] Not loading EXE/LNK icons on network drives and removable disks.
- [x] Custom folder icons (desktop.ini); Explorer's default icons in the "all associated" icon mode.
- [x] Overlay icons (Windows shell overlays).
- [x] `!` mark on hidden/system files; setting to show hidden files dimmed.
- [x] Icon size (16 / 24 / 32 / 48) and horizontal/vertical extra spacing.
- [x] Main menu icon size, icon frame, showing checked items as pressed buttons.
- [x] Take drive icons from the system.
- [-] Special icons of virtual folders (Network etc.) — out of scope: the application has no Windows shell virtual folders.
- [-] Choice of a default icon library (.icl/.dll) — out of scope: built-in icons are vector glyphs (`build/make_menu_icons.py`).

### Font — done
- [x] Dialog scaling (does not scale the main window). "Adapt to large fonts" is always on in Avalonia.
- [x] Main window font and dialog font.

### Color
Since 7 November 2026 colors come only from theme files (default: system colors, dark theme `Themes/dark.json`).
- [x] Text, background, second background (alternating rows), mark, cursor, cursor text, Lister background and text: the `FileList.*`, `Lister.*` keys in the theme file.
- [x] Inverse selection.
- [-] Editing colors one by one in the settings window, inverse cursor, system accent color for the cursor, color sample: replaced by theme files (user request).

### Tabstops/Data Formats
- [x] Show the extension right after the name (the extension column is hidden) or in a separate column.
- [x] Column widths (tab stops) and "name column fills the window width".
- [x] Date, time, thousands and decimal separators.
- [x] Show the century (20xx), 12-hour AM/PM, folder count in the footer.
- [x] Size unit of header/footer (bytes / kbytes / dynamic).

### Folder Tabs
- [x] Tabs on multiple lines; all tabs the same width; title length limit.
- [x] Ask before closing all; double-click closes a tab.
- [x] Ctrl+Up opens the new tab in front; Ctrl+Tab switches to the last used tab.
- [x] Show the tab list with Ctrl+Tab (list mode): while Ctrl is held, Tab/Shift+Tab move through it, releasing Ctrl switches, Esc cancels.
- [x] Open new tabs next to the current tab or at the end.
- [x] Show locked tabs with `*` (closable); drive letter in the tab title; icons on tabs.
- [x] Clicking a tab activates the target panel (as a setting).
- [x] Save the shown file types (filter) separately for each tab (as a setting).

### Custom columns, View mode, Auto switch mode
- [x] "View modes" page in Settings (column sets as JSON; the list editor is in the Show menu).
- [x] List background and tab color depending on the view mode; commands on entering/leaving a mode.
- [x] View mode background in the brief and thumbnail views too.

### Operation
- [x] Move to the system tray when minimized.
- [x] Always go to the root when changing drives (when off, returns to the last used folder on that drive).
- [x] Open with a single click.
- [x] Select only the name (not the extension) when renaming — as a setting.
- [x] Autocomplete paths (path line, copy / move target, command line); append the suggested name automatically.
- [x] Number of lines kept visible below (and above) the cursor (in the detailed view).
- [x] Mouse selection mode: selection with the right button (NC style, context menu by holding for 0.6 s).
- [x] Selection by clicking the icon; apply the selection mode only on touch screens.
- [x] Selection with Num+: files only / folders too.
- [x] Save on exit: folders and tabs, panel views, command line history (separately; "Save settings" writes all of them).
- [-] NTFS daylight saving correction (ignore a 1-hour difference): .NET returns time stamps in UTC, there is no offset to correct.
- [x] Calculate the folder size when selecting with Space (as a setting).
- [x] Subfolder size before copying/deleting: always calculated (a counting pass runs first for the progress), no separate option needed. Use Everything for the folder size (Windows, es.exe; not tried, the application's own file index is used instead).
- [x] File descriptions: character set when writing (automatic / ANSI / DOS / UTF-8 / UTF-16), copy / move / rename descriptions together with the files. The type is detected automatically when reading.

### Pages added later
- [x] Refresh: auto refresh on/off, network and removable drives, excluded folders, refresh when the window gets the focus.
- [x] Log file: copy/move, delete, pack, rename; errors only.
- [x] Log file: FTP / SFTP / WebDAV / S3 operations and unpacking from archives as separate options.
- [x] Ignore list: name, mask or full path; `nc_SwitchIgnoreList`.
- [x] History: length of folder and command line history.
- [x] Zip packer: default compression level.
- [x] Zip packer: names in UTF-8 or the OEM code page; encryption AES-256 / AES-128 / ZipCrypto.
- [x] Misc.: ask before closing (Alt+F4 and closing the window).

### Further options (10 November 2026)
- [x] 7-Zip: compression level, method, solid archive, CPU threads, encrypt names, extra parameters.
- [x] Packer: set the archive date to the newest file's date (local targets).
- [x] Copy/Delete: default answer for existing files (Overwrite, Skip, ...); drag and drop without the Copy dialog.
- [x] FTP: passive mode as the default of new connections; number of retries.
- [x] Thumbnails: disk cache on/off, clear the cache.
- [x] Refresh: no refresh while in the background.
- [x] Log file: rotation daily or by size, number of old files kept.
- [x] Configuration window: changed-from-default marks, Reset page, dependent options greyed out, search in translated texts.
- [x] Copy date/time of directories: always done (no option needed).
- [x] 7-Zip: dictionary size and word size; memory usage estimate.
- [x] Zip: own folder for temporary files; open partial ZIP files (listed and read from the local headers); keep damaged files (bad CRC); 8.3 short names (Windows).
- [x] Copy/Delete: copy buffer, big file mode (write-through, own buffer), compatibility mode for drives (small blocks), copy / move / delete with Windows Explorer.
- [x] Refresh: put files that appear at the end of the list; do not remember these drives on exit.
- [-] Refresh: "update the header / footer only": the list refresh always updates both.
- [x] FTP: transfer type (binary / ASCII / by mask), FTP log file, PORT range and external address, break off a stalled transfer (retried).
- [-] FTP: MODE Z compression (FluentFTP has none).
- [x] Thumbnails: fields under thumbnails, fields by type, thumbnails from Explorer (Windows), IrfanView / XnView nconvert, text file thumbnails; remove the thumbnails of deleted files.
- [x] Log file: skipped files; file system plugin operations.
- [x] History: history thinning as a setting, folder history kept between sessions, frequently used folders (kept / shown, saved on exit).
- [-] History: last visited folder in the middle of the list, own storage place for frequently used folders.
- [x] Ignore list: kept in its own file; add the names selected in the panel (with or without path).
- [x] Misc.: confirmations for non-empty folders, overwriting read-only / hidden / system files, dropping on the F8 button; a sound when an operation ends.
- [x] Fix: Windows properties dialog and network drive dialogs were never reached (the platform classes did not name the interface).
- [-] Edit/View: external viewer for F3 (Associations and Lister plugins cover it); ARJ / LHA / UC2 / ACE packers (obsolete formats).

### Change button bar
- [x] Preview, add / delete / separator / move, icon size, command, parameters, start folder, icon file, name, tooltip.
- [x] "Run minimized" / "Run maximized" (the program's window state; Windows).
- [x] Choosing among the icons in an icon file (exe, dll, icl; stored as `file,index`; Windows).
- [-] "XP style": Avalonia buttons always take the theme's look.

## Connections
- [x] WebDAV (dav://, davs://) and S3-compatible storage (s3://), in the same connection manager as FTP / SFTP.
- [x] Keeping file times on WebDAV and S3: WebDAV PROPPATCH (Nextcloud/ownCloud `lastmodified`, writable `getlastmodified`, IIS `Win32LastModifiedTime`; the one that works is found per server), S3 and Azure `mtime` metadata (same as rclone). The time zone shift on FTP was fixed too.
- [x] S3: virtual-hosted addresses (bucket.host), session token and AWS profiles (~/.aws/credentials, ~/.aws/config).
- [x] Azure Blob Storage (azblob://; account key or SAS).
- [x] SMB/CIFS on every platform with the built-in client (smb://; SMBLibrary, LGPL).
- [x] Google Drive, OneDrive, Dropbox (gdrive://, onedrive://, dropbox://): OAuth sign-in in the browser (PKCE, localhost:53682). The user registers the application ID with each service (steps in the connection dialog and the README). Tested against a fake API; should be tried with real accounts.
- [x] Google Drive: shared drives and "Shared with me"; OneDrive: SharePoint sites and document libraries (optional). Tested with the fake API.
- [x] Proxy for web requests (Configuration > Operation > Proxy: system / none / manual with user, password and bypass list): WebDAV, S3, Azure, cloud drives and their sign-in, URL downloads, update check.
- [x] FTP, SFTP and SMB through the manual proxy (HTTP CONNECT or SOCKS5): FluentFTP and SSH.NET proxy clients, SMB through a local forwarding port. Tested with fake proxies; to be tried with a real proxy.
- [x] More proxy types (user's request, 17 November 2026): HTTPS (CONNECT inside TLS to the proxy), SOCKS4 / SOCKS4a, SSH tunnel (`SshProxy`: channels of one SSH session, key file or password, the host key asked as for any server) and an automatic configuration script (PAC, `PacScript` in Jint with the usual helper functions). Web requests through HTTPS / SSH / PAC proxies open their connections through `ProxyTunnel` (`ConnectCallback`); FTP through HTTPS / SSH proxies with `FtpTunnelClient`, SFTP through SOCKS4a / HTTPS / SSH with a one-session local port. Tested with fake proxies and a fake SSH connector; to be tried with real proxies and a real PAC file.
- [x] A connection's own proxy (Proxy tab: the program's setting, none, its own), its password in the password store (`proxy:<secret id>`).
- [x] Richer options per connection type (user's request, 17 November 2026): keep-alive and pinned fingerprint (SSH, FTP, TLS types), SFTP request size, FTP listing / server time zone / own active ports / data encryption / TLS version, WebDAV authentication type, S3 storage class / ACL / part size / requester pays, Azure access tier, SMB hidden shares, permanent delete (Google Drive, OneDrive work accounts, Box), Google export format, RDP screen / colors / sound / gateway / redirections / admin session, Telnet terminal type / character set / Backspace / Enter / local echo. The headers, the .rdp file and the FreeRDP arguments are tested; to be tried against real servers.
- [x] Pack dialog: split archives (7-Zip `-v`: name.7z.001 ..., the volume size with units) and self-extracting archives (`-sfx` with the 7z.sfx module next to 7-Zip; .exe). Both for the 7-ZIP packer; not together (7-Zip cannot split an SFX). Tried with 7-Zip on Windows.
- [x] Opening split archives (name.7z.001, name.zip.001 ...) in a panel like a single archive: the volumes are read as one stream (on other providers joined into a temporary copy); read-only. Tried with 7-Zip volumes.
- [x] The Net menu reorganized by connection type (New connection, Saved connections, Connect by address, Network); product icons for connection types.
- [x] One protocol table (`RemoteProtocols`: scheme, aliases, default port, kind) instead of the scheme maps in several places.
- [x] Service presets in the connection dialog (S3: Amazon, Google Cloud Storage, Cloudflare R2, Backblaze B2, Wasabi, DigitalOcean Spaces, Hetzner, MinIO; WebDAV: Nextcloud / ownCloud, Yandex Disk, pCloud, Koofr).
- [x] Shortcuts to the local folders of cloud sync clients (OneDrive, iCloud Drive, Dropbox, Google Drive, Nextcloud, Box, MEGA, pCloud) and to WSL distributions: Computer view, optional drive bar shortcuts (Configuration > Layout).
- [x] SCP (scp://) for SSH servers without SFTP: shell commands for listing and changes (tested in a real POSIX shell), SCP for the data. To be tried against a real server.
- [x] Web server folder listings (http://, https://, read only): Apache, nginx, IIS, lighttpd, Python pages; tried against archive.apache.org and cdn.kernel.org.
- [x] Box (box://): OAuth (client id and secret), upload sessions for big files, times given with the upload. Tested against a fake API; to be tried with a real account.
- [x] Azure Files (azfile://): shares, folders, ranges, renames on the server, SMB times. Tested against a fake API; to be tried with a real account.
- [x] Android over ADB (adb://serial/sdcard): phones as drives (the ADB server's device list, watched with the drives), toybox shell, exec-out / push, free space. Tested with a fake adb over a real shell; to be tried with a phone.
- [x] Connection manager in two panes (folders with color labels, search and filter chips, drag and drop, F2 / Del / Ctrl+D / Ctrl+Z, favorites, connection state; the editor with General / Login / Advanced tabs, the choice of the type as cards or a pasted address, test, connect, connect to a whole folder). The separate connection editor dialog is gone (Net > New connection opens the manager).
- [x] SSH agent login for SFTP / SCP (Windows OpenSSH agent, Pageant, SSH_AUTH_SOCK) and jump hosts (a tunnel through another SSH server); tried against an asyncssh server (tests/servers/sftp_server.py with an authorized_keys file).
- [x] Connection timeout, FTP file name encoding, S3 server-side encryption (SSE-S3, SSE-KMS), AWS SSO profiles (device sign-in, the AWS CLI's token cache), Azure Entra ID sign-in (Blob and Files). SSO and Entra ID are tested against fakes; to be tried with real accounts.
- [x] Importing FileZilla (sitemanager.xml) and WinSCP (WinSCP.ini or the registry) sites with folders, key files, tunnels and saved passwords.
- [x] rclone remotes (rclone://remote/path; a plugin shipped with the program since 15 November 2026, its entry point in the Plugins menu of the button bar): rclone rcd on 127.0.0.1 with a random password, copyfile through temporary files. Tested against a fake rc API; to be tried with a real rclone (FM_TEST_RCLONE=remote runs the test against it).
- [x] RDP (rdp://; user's request, 17 November 2026): a "Remote desktop (RDP)" type in the connection manager (server, port 3389, user, full screen, saved password) that starts the system's client: Windows `mstsc.exe` with a generated .rdp file (the saved password written for the logon session as `TERMSRV/host` through CredWrite, not `cmdkey`), macOS the app that opens .rdp files (Windows App), Linux FreeRDP (`xfreerdp3` / `sdl-freerdp3` / `xfreerdp`, the password on its standard input) or Remmina; without one the user is told what to install. The .rdp file and the FreeRDP arguments are tested; to be tried against real servers on each system.
- [x] SSH, VNC and rsh / rlogin (ssh://, vnc://, rsh:// rlogin://; user's request, 17 November 2026): SSH as a tab of the internal terminal with the SFTP login (password, key, agent, jump host, proxy; tested against tests/servers/sftp_server.py, which has a small shell now), VNC in the system's viewer (TigerVNC / UltraVNC / RealVNC / TightVNC / Screen Sharing / Remmina / KRDC; the arguments are tested, the viewers not tried), rsh / rlogin in the terminal (tested against a fake server on the loopback; to be tried with a real host). rlogin tells the window size when the server asks (its urgent byte) and on every change.
- [x] Serial port consoles (serial://COM3, serial://ttyUSB0:115200; user's request, 17 November 2026): a tab of the internal terminal over System.IO.Ports (speed, data bits, parity, stop bits, flow control, DTR, Backspace / Enter / local echo, character set); the ports found are offered. Tested with a socket in place of the port; to be tried with a real device on each system.
- [x] Document preview in the Lister and Quick View (user's request, 17 November 2026): PDF pages drawn by PDFium (only the pages near the view, Ctrl+wheel zoom), Office Open XML and OpenDocument as HTML (text, headings, lists, tables, sheets with column letters, slides, pictures), other documents through LibreOffice (converted to PDF once, cached). Tested with generated documents and a fake soffice. Tried with real-world files (PDF, docx / xlsx, old doc / xls) and LibreOffice (user, 18 November 2026: works).
- [x] Document preview extras (user's request, 17 November 2026): text search (F7 / F3, the match marked and scrolled to) and selection (drag within a page, Ctrl+A, Ctrl+C) in PDF pages through PDFium's text API; a password prompt for protected PDFs; old Office files (doc, xls, ppt) without LibreOffice through `CompoundFile` (MS-CFB) and their binary formats (Word's piece table, Excel's BIFF8 records with shared strings across CONTINUE, PowerPoint's text atoms). Tested with generated documents (an RC4-encrypted PDF, compound files). Tried with real-world files (PDF, docx / xlsx, old doc / xls) and LibreOffice (user, 18 November 2026: works).
- [x] Document preview (user's request, 18 November 2026): a selection across pages in PDFs (drag past the view's edge scrolls, Shift+click extends); the old formats' formatting: .doc headings (styles 1-9), tables (PAPX: sprmPFInTable / sprmPFTtp, else guessed from the cell marks) and bold / italic / underline / strikethrough runs (CHPX), bold cells of .xls (FONT / XF). Tested with generated documents. Tried with real-world files (PDF, docx / xlsx, old doc / xls) and LibreOffice (user, 18 November 2026: works).
- [x] RDP extras (user's request, 17 November 2026): the active panel's folder shared in the session (FreeRDP `/drive:name,folder`; mstsc shares its drive: `drivestoredirect`); **Files (SMB)** in the connection manager opens the same computer over SMB (a saved SMB connection to that server, else one made from the remote desktop). To be tried against real servers.
- [x] Remote desktop in a tab of the main window (user's request, 18 November 2026): Windows through the system's Remote Desktop ActiveX control (MsTscAx in ATL's AtlAxWin, driven by IDispatch without the .NET COM interop; the control, all settings, the password and Connect tried on this machine without a server), Linux through `xfreerdp /parent-window` in the tab's X11 window. Tried against a real server on Windows (user, 18 November 2026: works); Linux (FreeRDP in the tab) still to be tried; macOS still opens the Windows App.
- [x] Connections (user's request, 18 November 2026): each connection's own log (`logs/connections/<name>.log`: connecting, login, server key, session ends, tests, errors with their causes; the Log tab of the editor, "Show the log" with a server's error, the causes in the terminal and the remote desktop tab); what must be filled in is checked field by field (a red * on required fields, the wrong ones framed in red, Save / Connect / Test show the tab of the first problem, a red ! in the tree); the connection manager is a page with a tab like the maximized terminal; a new connection asks for its name first (1 Name › 2 Type › 3 Settings, a name used by no other connection).
- [x] Linux drive names (user's report, 19 November 2026): the drive list showed the mount point (.NET's DriveInfo.VolumeLabel is the mount point on Linux and macOS) in place of the disk's label; labels now only from Windows' volume labels, lsblk's LABEL then PARTLABEL, without lsblk /dev/disk/by-label. To be checked on Linux.
- [x] Settings backups (user's request, 19 November 2026): the last 7 different settings files that loaded are kept in the settings folder's `backups` (no new copy while nothing changed); a damaged settings file is kept as `.corrupt` and the newest backup that loads is restored, with a message at the start. Configuration > Restore settings from a backup lists them (when, connections, Hotlist entries, the one in use, damaged ones) and restores the one chosen with a restart (the current settings kept as a backup first).
- [x] Remote desktop settings (user's request, 19 November 2026): tabs Display / Local resources / Experience; sound quality, microphone, smart cards, connection speed with the visual effects, Windows key combinations, reconnecting by itself (the .rdp file, the control's AdvancedSettings / SecuredSettings, FreeRDP's arguments). Not yet tried with a real server.
- [x] Remote desktop in a tab, its title bar's options (user's request, 19 November 2026): full screen through the control's FullScreen (OnEnterFullScreenMode / OnLeaveFullScreenMode, the session sized to the screen and back; OnRequestContainerMinimize minimizes the window), fit to the tab (SmartSizing switched live), reconnect. Windows only for the first two; not yet tried with a real server. Linux (FreeRDP in the tab): full screen and fitting are hidden (FreeRDP cannot switch them while it runs).
- [x] Remote desktop in a tab (user's request, 18 November 2026): only the panel's drive shared (IMsRdpClientNonScriptable3's DriveCollection, slots from mstscax.dll's type library); the session's end reason from the control's OnDisconnected event (a hand-made IDispatch sink on IMsTscAxEvents; the reason and GetErrorDescription's text; the state is polled only when the events cannot be had); FreeRDP in a tab follows the tab's size (its X11 windows resized, /dynamic-resolution kept). The drive, the events and the reason text were tried with the real control on Windows without a server; the Linux resizing is still to be tried.
- [x] Telnet (telnet://; user's request, 17 November 2026): a tab of the internal terminal (`TelnetTerminal` on the terminal emulation; ECHO / SGA / BINARY taken, TERMINAL-TYPE xterm and NAWS offered, other options refused). Tested against a server on the loopback; to be tried against a real device.

## Compared with MobaXterm (user's request, 21 November 2026)

What MobaXterm's session manager and terminal have that the connection manager lacks, by value. Out of scope: the
built-in X11 server and XDMCP, Mosh, the built-in servers (TFTP, HTTP, SSH; PC link shares), the Cygwin tools.

- [x] The files of an SSH session over SFTP: *Files (SFTP)* in the connection manager and in the title of an SSH tab of
  the terminal (a saved SFTP / SCP connection to the same server, else the session's login as SFTP: key file, agent,
  jump host, proxy and its saved password); opened in the shell's folder when the shell reports it (OSC 7).
- [x] SSH tunnels: local, remote and dynamic (SOCKS) port forwarding, saved, started / stopped by hand or at start (Net > SSH tunnels..., `TunnelService`, `SshTunnelRunner`; tested with a fake runner, to be tried against a real server).
- [x] Import PuTTY sessions (registry) and MobaXterm's `.mxtsessions`; export connections to a file (without passwords)
  and import such a file (`ConnectionImport.Sessions.cs`; the MobaXterm fields follow a reverse-engineered description
  of version 26.3, Telnet / rsh / FTP by host, port and user only: to be tried with real files).
- [x] Reconnect a remote session that ended: already there (an ended SSH / Telnet tab connects again with Enter).
- [x] Log a terminal session's output to a file (the terminal's **Log** button, or every tab by the setting; plain text, `Core/Text/TerminalLog`).
- [x] Find in the terminal's scrollback (Find / Ctrl+Shift+F: marked matches, older / newer, match case).
- [x] MultiExec: type into several terminal tabs at once (the terminal's MultiExec button, a check box per tab).
- [x] Saved commands (snippets) sent to the terminal with a click (the terminal's Snippets menu, the list on the Misc. page).
- [x] Shared credentials: one user / password entry used by several connections (Login / Logins... in the connection editor, `SharedCredential`, the password under `credential:<id>`).
- [ ] SSH agent forwarding: not possible with SSH.NET 2026.0.0 (checked 21 November 2026): its channel messages know only session, x11, direct-tcpip and forwarded-tcpip, so the server's `auth-agent@openssh.com` channel cannot be accepted, nor can `auth-agent-req@openssh.com` be sent. Needs a change in SSH.NET (or another SSH library).
- [x] Split terminal: two or four terminals in one tab (the terminal's Split menu: side by side, one above the other, four).
- [x] Monitor bar of an SSH server: CPU, memory, disk and network through commands over SSH (the terminal's Monitor button; Linux /proc, `ServerStats`; tested with recorded output, to be tried against real servers).
- [x] A connection's own terminal look: font, color scheme, tab color (a production server in red) (`TerminalLook`, the editor's terminal tab).
- [x] Network tools: Wake-on-LAN, ping, port scan (Net > Network tools..., `Core/Net/NetworkTools`).
- [x] Highlighting words such as "error" / "warning" in the terminal output (`HighlightRule`, the list on the Misc. page; the real terminal only, the line console stays plain).

## Internal terminal
- [x] Terminal at the bottom of the window via the button at the end of the command line / Ctrl+` (cmd.exe or $SHELL, redirected input/output). Commands are typed after the prompt, inside the output area.
- [x] Real terminal emulation (Windows ConPTY / Linux, macOS pty; VT100 / xterm screen): colors, programs that draw the screen themselves, Ctrl+C signal. When turned off in the settings, the line console is used. The app was tried on Linux (Ubuntu 26.10) and macOS (M5); on macOS job control (Ctrl+Z) may not work because the shell has no controlling terminal.
- [x] Multiple terminal tabs; choice among the installed shells (cmd, PowerShell, pwsh, Git Bash, WSL, $SHELL).
- [x] Keep the terminal height and the open tabs in the settings.
- [x] Maximize / restore (button in the title bar, double click on the title, `nc_MaximizeTerminal`): maximized, the terminal starts where the panels start. cmd.exe is shown as "Command Prompt".

## Tools
- [x] Duplicate files window (Commands > Duplicate files...): search by choosing folders, groups, marking by rule, deleting to the recycle bin / permanently, moving to a folder.
- [x] Folder sizes window (like WinDirStat): tree, extension statistics, treemap; deleting.
- [x] Find files content search: several files read at the same time (local: up to 8, others: 2), the line of the first match under each result (in the file's own encoding), F3 opens the Lister at the match, *Skip binary files*.

## Other
- [x] New application icon (dark package) verified on Windows (exe, window, taskbar; the old icon came from Windows' icon cache).
- [-] Master password lock with Windows Hello / Touch ID — not planned (user's decision, 20 November 2026; it would need a separate Windows-specific build target of the application).
- [x] plocate tried in a real environment (WSL Ubuntu 24.04, plocate 1.1.19): indexed search results match a folder scan. With several masks (`*.bin *.txt`) nothing was returned because plocate combines patterns with AND; each pattern is now a separate query.
- [x] The application's own file index (Settings > Operation > File index): fast search, duplicate files and folder size without Everything / plocate / Spotlight; search in ~1 million files takes 20–80 ms.
- [-] MSIX package: dropped on 15 November 2026 (user's request); the Inno Setup program covers Windows.
- [x] PC link: two NextCommanders over a USB or Ethernet cable or the network (Net > PC link, Net > Share with PC link): shared folders (read-only possible), TLS, pairing code, finding computers on link-local addresses. Tested with a real server and client on loopback.
- [x] PC link: "Allow through the firewall" (Configuration > Operation > PC link) opens the port in Windows Defender Firewall (every network profile) or ufw / firewalld, with administrator rights (19 November 2026: Windows 11 ↔ Ubuntu 26.10 did not find each other; the Windows log showed only its own searches answered, so the other side's searches never reached it).
- [-] PC link over a USB-C to USB-C cable between ordinary (USB 3) ports: such a cable makes no network between two computers (user's report, 19 November 2026); only USB4 / Thunderbolt ports with networking do. The guide says which cables work.
- [ ] PC link: to be tried with two computers on a direct Ethernet cable, a USB-C / Thunderbolt cable and a USB link cable (Windows, Linux, macOS). Over Wi-Fi (the same network) it works (user, 19 November 2026). Windows 11 and Ubuntu 26.10 did not find each other either way (user, 19 November 2026): the search now also goes out on interfaces Linux reports as "unknown" / "dormant", and each search is logged (logs/connections/PC link.log: probes and answers; the sharing side logs the searches it answered); the guide says how to open the ports in ufw / firewalld. To be tried again.
- [-] PC link over a raw USB cable without a network adapter (old "Easy Transfer" cables in their own mode): needs a USB driver of its own; out of scope.
- [x] The .deb package installs and runs on Ubuntu (17 November 2026). Ubuntu's App Center warns about any .deb that does not come from a repository; `sudo apt install ./NextCommander-....deb` does not. Removing the warning would need an APT repository / PPA, Snap or Flatpak.
- [x] The .rpm package installed on a real Fedora system: it works (user, 21 November 2026).
- [ ] Media playback with VLC, DMG packaging and mdfind (Spotlight) should be tried in a real environment.
- [ ] System icons of the user folders (documents, downloads ...): tried on Windows and Linux (icon theme, SVG drawn with Svg.Skia; 14 November 2026); the macOS NSWorkspace icon should be tried on macOS. The Trash's icon on macOS was a plain folder (the icon of ~/.Trash); since 18 November 2026 it is AppKit's NSTrashEmpty / NSTrashFull image (user's report; checked on a Mac by the user, 18 November 2026: right).
- [x] "Show admin shares": in the network view (drive box `\`) the SMB shares of the machines are listed (Windows); when on, admin shares such as C$, ADMIN$ too.
- [x] SMB shares on Linux / macOS: the machine's "SMB" entry in the network view opens the shares with the built-in client.
- [x] Drive lists refresh by themselves when a USB disk, memory card or phone (as a disk) is plugged in or removed, or a network drive is mapped (`IPlatformService.WatchDrives`, `DriveWatcher`: a cheap signature read every second; Windows: drive letters and media in removable drives, Linux: `/proc/self/mounts` + `/sys/block`, macOS: mount points). A panel on a removed drive goes to the nearest existing folder or home. Tried on Windows (tests) and on Linux with real devices (14 November 2026); to be tried on macOS.
- [x] Phones, cameras and players over MTP / PTP as drives (`DriveKind.Phone`). Windows: the system's WPD API through source-generated COM (`Platform/Windows/Wpd`), provider `mtp://<device>/<storage>/...` (`Platform/Mtp/MtpFileSystemProvider`): list, read, write (collected first: MTP needs the size up front), new folder, rename, delete; a move to another folder is copy + delete; file times can only be given with the upload. Linux: the folders gvfs mounts (`$XDG_RUNTIME_DIR/gvfs/mtp:host=...`, `gphoto2:`). Tested with a fake device and the WPD value objects; tried with a real phone on Windows (incl. the phone's Explorer icon and right-click menu) and Linux (14 November 2026).
- [x] MTP on macOS (the system has no MTP) and on Linux desktops without gvfs (KDE uses kio-mtp, no folders): through libmtp (`Platform/Mtp/LibMtp`), loaded when it is installed; the deb / rpm recommend it; `publish.ps1` puts Homebrew's bottles into the .app on any system (`build/macos/bundle_libmtp.py`, 17 November 2026).
- [x] MTP through libmtp works with a real phone on Ubuntu (17 November 2026).
- [ ] MTP through libmtp to be tried on macOS with libmtp inside the app (Apple silicon and Intel; Photos / ptpcamerad taking the device; a company Mac's device control may block USB devices), and with a camera. Linux in WSL works too: `usbipd attach --wsl` passes a USB phone through. User's report (18 November 2026, Samsung phone on a Mac): "The device could not be opened" although the phone was unlocked and allowed: macOS's camera service ptpcamerad holds it; NextCommander now stops it and opens the phone again (`MacImageCapture`), to be tried on the Mac.
- [x] Right click on a drive (drive buttons, drive list, Computer view): the system's menu of the drive (Windows: Explorer's; the Recycle Bin, Computer and Network entries too). Drive list and Computer view: the label or the kind of the drive ("Local Disk"), the drives' system icons in the Computer view.
- [x] Trash / Recycle Bin in a panel (`trash:///`, `Platform/Trash`): Windows `$Recycle.Bin\<SID>` (`$I` / `$R` pairs), Linux freedesktop.org trash (home and `.Trash-<uid>` of volumes), macOS `~/.Trash` (no put back). Restore, copy / move out, delete for good, empty (`nc_RestoreFromTrash`, `nc_EmptyTrash`). Tested with folder layouts; tried on Linux and macOS (14 November 2026).
- [x] Drive box without "My Documents" (a drive bar shortcut instead); Computer view ordered: drive letters, external disks, phones, the trash; the trash button of the drive bar is an option (Configuration > Layout); system icons for every drive box entry (Windows shell incl. Recycle Bin / This PC / Network / phones by their shell name, Linux icon theme, macOS Finder). Tried on Windows, Linux and macOS, with a phone too (14 November 2026).
- [x] Trash: the original location of deleted items as a column ("Original location", `TrashColumns`): the trash gets its own view mode by itself unless a view mode of the user matches it.
- [x] MTP: free space of the phone's storages in the footer (WPD storage capacity, `IVolumeSpaceProvider`); a phone folder shown in a panel is read again every 3 s (ids only) and the panel refreshes when files come or go (Configuration > Refresh: removable drives). To be tried with a real phone.

## AI support (ideas, to be evaluated)

Collected on 19 November 2026 at the user's request; nothing decided yet. The idea throughout: AI fills in the options of
the existing tools or makes suggestions; what changes files still goes through the tools' own preview and confirmation.

Suggested start (small, results visible at once, no risk; they build the groundwork for the rest: provider setting,
key storage, sending a request, preview):
- [ ] Multi-rename: a rule described in words ("date first, spaces to underscores, add a counter") becomes the
  rename options (mask, search / replace, regex, counter); the user checks it in the preview.
- [ ] Connection errors: "Explain" next to "Show the log" reads the connection log and the error's causes and says in
  plain words what to change.

Low risk, high value:
- [ ] Find files (Alt+F7): a search described in words ("PDFs over 100 MB changed in the last 7 days") fills in the
  search form; the program itself searches.
- [ ] Language editor: suggest translations for missing keys (keeping `{0}` holes and menu access keys); the user
  accepts them.

Understanding content:
- [ ] Lister / preview: summarize, translate or answer questions about a PDF, Office or text file; describe images,
  read text in them (OCR).
- [ ] Compare window: explain the differences of two versions in plain words.
- [ ] Meaning-based search ("rental contracts", "2025 invoices") on top of the program's own file index (summaries or
  a vector index of the contents).
- [ ] File comments: suggest descript.ion comments for the selected files.

Tidying up:
- [ ] Folder sizes / duplicate files: point out what is probably build output or cache; deleting stays the user's
  choice, to the Recycle Bin.
- [ ] Downloads folder: a plan of folders by kind and content, carried out by the move job with a preview.
- [ ] Synchronize directories: sum up the risks of the plan ("12 files deleted for good, 3 of them newer on the right").

Commands and help:
- [ ] Command line / internal terminal: a shell command or a chain of `nc_` commands from a description, shown before
  it runs; explain a shell's error message.
- [ ] Settings search in plain words ("dark theme", "deleted files not to the Recycle Bin") goes to the setting.
- [ ] Help: answer "how do I ..." questions from the user guide (docs/guide).
- [ ] Theme editor: a theme from a description ("navy, high-contrast dark theme"); the readability checks are there.

Design rules (to keep with the project's rules):
- Off by default; before file contents leave the computer, show what is sent; files of remote connections and chosen
  folders can be left out.
- Provider: the Claude API or a local model (e.g. Ollama); the API key in the password store (`SecretStore`), under
  the master password.
- AI never acts by itself: it fills in options or suggests; changes go through the existing previews and confirmations.
- Every feature is an `nc_` command (menu icon, a key in every language file); settings in `AppSettings` with a
  Configuration page; no JSON for the user.
- The use (cost) of a request is visible; without a connection the program works as before.
