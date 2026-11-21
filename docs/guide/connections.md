# Connections

[Back to the README](../../README.md)

NextCommander opens servers and cloud storage in the same panels as your local files. Copying, moving,
deleting, renaming, viewing (F3), editing (F4) and opening archives work there too.

Supported types:

| Type | For |
|---|---|
| SFTP (SSH) | Linux and Unix servers, NAS devices |
| SCP (SSH without SFTP) | SSH servers without the SFTP subsystem (routers, small devices) |
| FTP / FTPS | FTP servers, with or without encryption (explicit or implicit TLS) |
| WebDAV | Nextcloud, ownCloud, SharePoint, IIS, Apache, NAS devices |
| SMB (Windows share) | Windows shares, Samba, NAS devices; also on Linux and macOS without mounting |
| Amazon S3 / compatible | Amazon S3, MinIO, Cloudflare R2, Wasabi, Backblaze B2, DigitalOcean Spaces |
| Azure Blob Storage | Azure storage accounts |
| Azure Files | The file shares of an Azure storage account |
| Google Drive | Your Drive, "Shared with me" and shared drives |
| OneDrive | Personal and work / school OneDrive, optionally SharePoint sites |
| Dropbox | Your Dropbox |
| Box | Your Box account |
| Web server folder listings | `http://` and `https://` folders with an index page (Apache, nginx, IIS ...); read only |
| Android over ADB | Phones with USB debugging on (also when MTP is not available) |
| PC link | Another computer running NextCommander, over a USB or Ethernet cable or the network: see [PC link](#pc-link) |
| rclone remotes | Every remote you set up with `rclone config` (70+ services); the rclone plugin shipped with the program |
| Remote desktop (RDP) | The desktop of a Windows computer (or a Linux one with xrdp), in a tab of NextCommander or the system's RDP client: see [Remote sessions](#remote-sessions-rdp-vnc-ssh-telnet-rsh) |
| VNC | The screen of another computer (any system with a VNC server), in a VNC viewer |
| SSH | A shell on a server in the internal terminal |
| Telnet | A text session in the internal terminal (routers, switches, old systems) |
| rsh / rlogin | Old Unix hosts without SSH, in the internal terminal (not encrypted) |
| Serial port | The console of a switch, router, server or board over a serial cable (COM3, /dev/ttyUSB0), in the internal terminal |

## The Net menu

- **Connection manager...** (Ctrl+F): your saved connections.
- **Saved connections:** opens a saved connection directly; the connections are grouped by type (SFTP, FTP,
  Amazon S3, Google Drive ...).
- **Connect by address...** (Ctrl+N): type an address and connect without saving.
- **Disconnect** (Ctrl+Shift+F): closes the connection of the active panel; the panel goes back to the last local
  folder. While a panel shows a connection, its path line has a red button with the connection's name that does the
  same.
- **Network (machines of the local network)**: see [Network neighborhood](#network-neighborhood).
- **Map network drive...**, **Disconnect network drives...**, **Share current directory...**,
  **Unshare directory...**: the Windows network drive and sharing dialogs (Windows).
- **Download from URL...** and **Download the addresses of a list...**: download files from the web.
- **Show hidden files on FTP**.
- **Master password...**: see [Saved passwords](#saved-passwords-and-the-master-password).
- **PC link...**, **Share with PC link**: see [PC link](#pc-link).
- **SSH tunnels...**: see [SSH tunnels](#ssh-tunnels).
- **Network tools...**: **Ping** (replies with their times and TTL, then sent / received / lost and min / avg / max),
  **Port scan** (TCP ports of a host: a list such as `22, 80, 8000-8100`, *Common ports* or 1-1024; the open ones with
  their usual service as they are found) and **Wake-on-LAN** (the magic packet to a MAC address, sent as a broadcast).
  The server of the active panel's connection is filled in.

In the button bar, the connection manager's button opens the manager while there is no saved connection; once
there are some it is a drop-down (▾): **Connection manager...** first, then the saved connections grouped by type
(the command `nc_ConnectionsMenu` opens the same menu).

The **Plugins** button next to it (`nc_PluginsMenu`) lists the places the plugins offer (such as rclone remotes) and
the plugin list. It shows only while plugins are installed; *Configuration > Options > Layout > Button bars > Plugins button*
turns it off.

## The connection manager

![The connection manager](../screenshots/connections.png)

The manager opens as a page of its own with a tab, like the maximized internal terminal: the **Files** tab above it
goes back to the panels while the manager stays open; ✕, its tab's × or Esc closes it. The connections are on the
left, in folders; the selected connection or folder is on the right. Above the list is the toolbar: **＋ New**, a new
folder and Import / export (FileZilla, WinSCP, PuTTY, MobaXterm, a connections file). The actions of the selected connection are in the bar above it, all in the accent color: *Test*,
*Duplicate*, *Delete* (for a remote desktop also *Files (SMB)*, for SSH *Files (SFTP)*) on the left, **Save** and **Connect** on the right; for
a folder *New connection*, *New subfolder*, *Delete* and *Connect to all*. Messages ("Saved", "Moved") show under them.
The manager opens with its first connection selected; without connections it shows **＋ New** and **Import...**.

- **Folders** can be nested and have a color label (for example red for production). Drag connections and folders
  to move them: onto a folder (it is highlighted) puts it inside, onto a connection into that one's folder, onto the
  empty space below the tree to the top; a folder cannot go into itself. The move is saved at once. An empty folder
  stays until you delete it.
- **Search** (Ctrl+F in the window) looks in names, servers, users and folders; the chips above the tree show only
  favorites, SSH or FTP connections, cloud storage, or the connections that are open now. The dot after a connection
  is green while it is connected, red when its last test failed.
- **Keys:** arrows move and open folders, Enter connects (Shift+Enter: in the right panel), F2 renames, Delete deletes
  (a folder with everything in it; its saved passwords are forgotten), Ctrl+Z puts the last deleted items back,
  Ctrl+D duplicates, Ctrl+N opens the new connection wizard, Ctrl+S saves. The right click menu has the same, plus favorites,
  copying the address and forgetting the saved password.
- **New connection:** **＋ New** (Ctrl+N, the right click menu, *Net > New connection*) opens the **new connection wizard**, a window over the manager in steps (1 Type › 2 Details › 3 Advanced › 4 Summary). The step's title (and on
  the first step the search) stays at the top; only the fields under it scroll.
  - **Type:** the cards in their groups. The **search box** above them keeps the types whose name, description or
    group has all the words typed (`cloud`, `azure`, `windows` ...; Enter chooses the only one left). Next to it an
    address such as `sftp://user@server:22/folder` can be pasted and Enter pressed (it fills in the type, server,
    port, user and start folder). *Net > New connection* of a type starts at the details.
  - **Details:** a **name** is suggested and selected (the type, "SFTP 2" when that is taken, or user@server of an
    address; the list shows the connection by it, its log is kept under it, and it must not be another connection's
    name); the **folder** is the group of the type (*Servers*, *Local network*, *Cloud storage*, *Cloud drives*,
    *Remote sessions*; another folder can be typed, made when new; a folder's *New connection* puts it there). Then
    only what the type needs: the server and port, the user (required for SSH, the access key id of S3, the storage
    account of Azure), an optional key file (SFTP / SCP / SSH), the application id of a cloud drive (and Box's
    client secret), the serial port, an optional start folder. Required fields have a red *; Next and Test mark what
    is missing. The fields are in cards (Connection, Server or Device, Login) with their captions on the left. The
    password is asked when connecting, as for every connection.
  - **Advanced:** the editor's own Advanced tab, a remote desktop's Display / Local resources / Experience tabs and
    the Proxy tab for this connection: timeouts, a jump host, encryption, the proxy ... (all of them can be changed
    later in the editor too).
  - **Summary:** everything entered, as the list will hold it: the folders down to the connection and its address,
    then the server (or device), the login and the advanced options that differ from the type's defaults.
  - **Next ›** goes on (the details are checked first); **‹ Back** goes one step back (from the details to the
    types); **Test** connects with these details from any step after the type; **OK** on the summary adds the
    connection to its folder, shows it in the tree and saves; **Cancel** (Esc) adds nothing.

  Everything else is in the editor on the right, in its tabs:
  - **General:** the server, port and start folder (for FTP also the encryption and passive mode, for S3 the region;
    a **Service** for S3 and WebDAV, see below).
  - **Login:** the method: password, key file or the **SSH agent** (SFTP / SCP); access key or **AWS profile / SSO**
    (S3); account key / SAS or **Microsoft Entra ID** (Azure); anonymous (FTP). Cloud drives take their application id
    here.
  - **Advanced:** the timeout, simultaneous connections, a **jump host** (SFTP / SCP), the **file name encoding**
    (FTP), **server-side encryption** with SSE-S3 or SSE-KMS (S3), plain HTTP, accepting any certificate, SharePoint
    sites (OneDrive) and the favorite mark.
  - **Log:** the connection's own log (see *Connection logs* below).
- **What must be filled in** has a red \* after its caption: the server; the user of SFTP, SCP and SSH (an SSH login
  always has one); S3's access key id (or an AWS profile) and Azure's storage account; a cloud drive's application id
  (and Box's client secret); the key file when the login is by key file (it must exist); the own proxy's server. A
  field that is empty or wrong is framed in red with what is wrong under it (a new connection once you start filling
  it in). *Save*, *Connect* and *Test* refuse such a connection, show the tab of the first problem and put the cursor
  there; in the tree a red **!** marks a connection that cannot connect yet (its tooltip says why).
- **Test** connects with the settings as they are and lists the start folder; the result shows under the tabs.
  **Save** writes the connections (closing the window does too; new connections never filled in are dropped).
  **Connect** opens the connection; a folder's **Connect to all** opens each of its connections in a tab.
- **Import** (the arrow under the tree) brings in the sites of **FileZilla** (sitemanager.xml) and **WinSCP**
  (WinSCP.ini or its registry key) with their folders, key files, jump hosts (WinSCP tunnels) and saved passwords
  (into the encrypted store; FileZilla's master-password-encrypted ones cannot be read).
  The same menu imports **PuTTY** sessions (Windows: its registry key; Linux / macOS: `~/.putty/sessions`): SSH,
  Telnet, rlogin and serial ports with the user, key file, remote command, terminal type and proxy, and
  **MobaXterm** sessions from an exported `.mxtsessions` file or `MobaXterm.ini`, with their folders: SSH (key file,
  the first SSH gateway as jump host, proxy, command), SFTP, RDP (drives, printers, clipboard, gateway ...), VNC,
  Telnet, rsh and FTP. Neither keeps passwords that can be imported; local shells, WSL, X11 and the other MobaXterm
  kinds are left out.
- **Export all connections to a file...** (or **Export this folder to a file...** while a folder is selected) writes
  the connections with their folders to a file for another computer or person: without passwords and client
  secrets. **Connections file of NextCommander...** imports such a file; a name already taken in the folder gets a
  number ("web (2)").

For S3 and WebDAV, the **Service** list fills in the server, port, region and start folder of well-known services:
Amazon S3, Google Cloud Storage, Cloudflare R2, Backblaze B2, Wasabi, DigitalOcean Spaces, Hetzner and MinIO;
Nextcloud / ownCloud, Yandex Disk, pCloud and Koofr. A part of the server name in capitals (such as `ACCOUNT_ID`
or `REGION`) is yours to replace; the note under the fields says how.

## Connection logs

Every connection has its own log: connecting (the address, the jump host, the proxy, the login method), logging in,
a new or changed server key, sessions ending, the tests and **every error with its causes** (the inner errors, such
as the system's socket error, and the details for a report). Passwords, keys and tokens are never written. The logs
are files in the settings folder's `logs/connections` (one per connection, named after it; one over 1 MB is set
aside as `.old.log`); the errors also go to the program log when it is on.

- The **Log** tab of a connection in the connection manager shows its log, errors in red, and follows new entries;
  *Refresh*, *Copy* and *Clear the log* are next to it.
- When a server cannot be opened, the message names the connection (name and address), where, the error with its
  causes and the log file; **Show the log** opens the log in the Lister. *Test* shows the whole chain of causes too.
- Terminal sessions (SSH, Telnet, rsh, serial port) write their causes and the log file in the terminal; a remote
  desktop tab under its end message.

## Typing an address

You can also type an address straight into the path line (Alt+D) or into *Connect by address...*:

| Type | Example |
|---|---|
| SFTP | `sftp://user@server.example.com:2222/home/user` |
| FTP | `ftp://user@ftp.example.com/` |
| WebDAV | `davs://user@cloud.example.com/remote.php/dav/files/user` (`dav://` without encryption) |
| SMB | `smb://DOMAIN;user@server/share` |
| S3 | `s3://ACCESS_KEY@s3.eu-central-1.amazonaws.com/bucket/folder` |
| SCP | `scp://user@router.example.com/etc` |
| Azure Blob | `azblob://account@account.blob.core.windows.net/container` |
| Azure Files | `azfile://account@account.file.core.windows.net/share/folder` |
| Web server folder | `https://archive.apache.org/dist/` |
| Android over ADB | `adb://SERIAL/sdcard/DCIM` |
| rclone | `rclone://remote/folder` (`rclone:///` lists the remotes) |
| RDP, Telnet | `rdp://user@pc.example.com`, `telnet://router:2323` (in *Connect by address...*; they do not open in a panel) |

## Notes per type

### SFTP
Log in with a password, a private key file (which may have its own passphrase), or the keys of the **SSH agent**:
the Windows OpenSSH agent, PuTTY's Pageant, or the agent of `SSH_AUTH_SOCK` on Linux and macOS. The keys stay in the
agent, which signs the login. With a **jump host** (`user@bastion:22`), the connection goes through that SSH server;
it logs in with the same key file, agent or password, and its key is confirmed like a server's.

### SCP
For SSH servers that have no SFTP subsystem. The login (password or key file) is the same as for SFTP. Folders are
listed and changed with shell commands on the server (`stat`, `mkdir`, `mv`, `rm`, `touch`), so the server needs a
POSIX shell; files are sent with SCP.

### FTP / FTPS
Choose plain FTP, FTPS with explicit TLS, or FTPS with implicit TLS (port 990), and passive or active mode. The file
name encoding is UTF-8 when the server supports it; for old servers choose one (Windows-1254, ISO-8859-9 ...).
More FTP options (transfer type, retries, a log file) are on the *Operation > FTP* page of the
[Configuration window](configuration.md).

### WebDAV
The start folder is the server's WebDAV path, for example `/remote.php/dav/files/<user>` for Nextcloud.
Without a user name the connection is anonymous. File times are kept when copying, where the server allows it.

### SMB
The user is `DOMAIN\user` or just `user`; leave it empty for a guest login. The root lists the machine's
shares.

### Amazon S3 and compatible
- **Server:** the S3 endpoint, for example `s3.eu-central-1.amazonaws.com` or your MinIO server.
- **User:** the access key id. The secret key is asked as the password and can be saved.
- Or choose an **AWS profile**: the keys are then read from your AWS command line settings, and no password is
  asked.
- The root lists your buckets; F7 there creates a bucket. Folders are formed by `/` in the object names.
- Options: the region (usually found automatically), virtual-hosted addresses, and "No TLS" for a local MinIO.
- **AWS SSO:** an AWS profile set up for IAM Identity Center (`sso_session` or `sso_start_url` in ~/.aws/config)
  gives temporary keys of its account and role. The sign-in token is shared with the AWS CLI; when it has run out,
  the browser signs in (as `aws sso login`).
- **Server-side encryption** (Advanced): SSE-S3 or SSE-KMS (with an optional KMS key) for every uploaded object.

### Azure Blob Storage
The user is the storage account; the password is the account key or a SAS token. The root lists the containers.
With **Microsoft Entra ID** (Login tab) you sign in with your work account in the browser instead; the account
needs a data role on the storage (Storage Blob Data Reader / Contributor ...). The tenant and the application id
may stay empty (your organization; the Azure CLI's application). The same works for Azure Files.

### Azure Files
The user is the storage account; the password is the account key or a SAS token. The root lists the file shares
(F7 there creates a share). Moves inside a share are renames on the server; file times are kept.

### Google Drive, OneDrive, Dropbox and Box
- The first time you connect, your browser opens the service's own sign-in page. With
  **Remember the sign-in** on, you only sign in once.
- The **account name** is just a label (for example `personal` or `work`) to tell several accounts of the same
  service apart.
- **You need your own application id.** NextCommander has no id of its own, so you register a free application
  with the service once and enter its id in the connection. The connection dialog shows the steps:
  - **Google Drive:** in the Google Cloud console, create a project, enable the Google Drive API, set up the
    OAuth consent screen (add your own account as a test user), and create an OAuth client id of type
    *Desktop app*. Enter the client id and the client secret.
  - **OneDrive:** in Microsoft Entra, create an app registration for personal and organizational accounts,
    add the *Mobile and desktop applications* platform with the redirect address `http://localhost`, and give it
    the `Files.ReadWrite` and `offline_access` permissions (for SharePoint also `Files.ReadWrite.All` and
    `Sites.Read.All`). Enter the application (client) id.
  - **Dropbox:** in the Dropbox App Console, create an app with *Scoped access* and *Full Dropbox*, allow the
    `files.metadata.read/write` and `files.content.read/write` permissions, and add the redirect address
    `http://localhost:53682/`. Enter the app key.
  - **Box:** in the Box developer console, create a *Platform App* with *User Authentication (OAuth 2.0)*, add the
    redirect address `http://localhost:53682/` and the scope to read and write all files and folders. Enter the
    client id and the client secret.
- Deleted files go to the service's own trash.
- **Google Drive:** the root shows My Drive plus the folders **Shared with me** and **Shared drives**.
  Google Docs, Sheets and Slides appear as `.docx`, `.xlsx` and `.pptx` files (Drawings as `.pdf`) and are
  converted when you copy or view them; they are read-only.
- **OneDrive:** with *Show SharePoint sites too* (work or school accounts), a `SharePoint` folder at the root
  lists your organization's sites and their document libraries.

- **Dropbox and Box** take a file's time only with the upload; a copy sends it then, so times are kept.

### Web server folder listings
An `http://` or `https://` address of a folder that the server shows as a list of links (Apache, nginx, IIS,
lighttpd, Python's http.server ...) opens like a folder. Sizes and dates are read from the page where it shows them.
The listing is read only: copy files out of it.

### Android phones over ADB
With *USB debugging* on (Developer options) and the installed `adb` (Android SDK platform-tools; found on the PATH,
in the SDK folder, or through the `FM_ADB` environment variable), connected phones appear as drives named
"*Model* (ADB)", next to the MTP ones. They open at `/sdcard` (the shared storage); going up shows the rest of the
phone as far as Android allows. The first time, allow the computer on the phone. Copies keep file times; the panel
footer shows the free space.

### PC link

Two computers that both run NextCommander can work on each other's folders over a cable — like the "port
connection" of older file managers — or over the network.

**The cable.** Anything the operating system shows as a network connection works:
- a direct **Ethernet** cable between the two computers (no router needed: both get an address such as
  169.254.x.x by themselves, which takes up to a minute after plugging in);
- **USB-C / Thunderbolt** between two computers (Thunderbolt networking on Windows and macOS);
- a **USB link cable** ("USB 3.0 bridge" / "data transfer" cable) whose driver makes a network adapter;
- both computers on the same Wi-Fi or LAN.

Old transfer cables that only work with their own program (and no network adapter) are not supported.

**On the computer that shares**
1. *Configuration > Operation > PC link*: add the **shared folders** (a name the other computer sees, the folder,
   and *Read only* if the other computer may not change it). Nothing outside these folders can be reached.
2. **Net > Share with PC link** turns sharing on (a check mark); the same switch is on the page above, which also
   shows the computer name, the port (47290) and this computer's addresses.
3. The first time, Windows asks whether NextCommander may use the network: allow it. A direct cable is often a
   "Public" network on Windows; allow public networks too, or set that network to "Private".

**On the computer that connects**
1. **Net > PC link...** lists the computers that share (they answer within a second or two). Open one: its shared
   folders appear, and everything works as on a local disk — copy, move, delete, view, edit, file times.
2. The first time, both screens show the **same six-digit code**: accept it here when it matches, and the other
   computer's user clicks **Allow**. Both computers remember each other; the next connection asks nothing.
   *Configuration > Operation > PC link > Allowed computers* forgets a computer.
3. A computer that does not show up in the list can be typed: `link://169.254.12.34/` (its address, shown on its
   settings page) or saved in the connection manager as a **PC link** connection.

**When a computer is not found.** The search is a UDP message to port 47290 of the network (the answer comes back the
same way), the files go over TCP port 47290. A firewall on either computer can drop them:
- **Linux:** with ufw on, allow the port on both computers that use PC link: `sudo ufw allow 47290/udp` and
  `sudo ufw allow 47290/tcp` (the answer to a search is a new packet for the firewall, so the computer that searches
  needs the UDP port open too). firewalld: `sudo firewall-cmd --permanent --add-port=47290/udp --add-port=47290/tcp`
  then `sudo firewall-cmd --reload`.
- **Windows:** the network must be "Private", or NextCommander allowed on public networks (*Windows Security >
  Firewall > Allow an app*).
- **Allow through the firewall** (*Configuration > Operation > PC link*) does this for you on the computer it runs on:
  Windows Defender Firewall gets inbound rules for the port (TCP and UDP, every network), Linux ufw or firewalld (when
  one is on) allows the port. It asks for administrator rights; do it on both computers.
- Each search is written to the log `logs/connections/PC link.log` in the settings folder: which addresses it went
  out from and whether anything answered; the sharing computer writes there which searches reached it. A search that
  reached the other computer but found nothing was dropped on its way back.
- Typing the address skips the search (only TCP 47290 is needed then).

**Which cables work.** PC link needs a network between the two computers; the cable must make one:
- an Ethernet cable (directly or through a switch / router), or a USB / USB-C Ethernet adapter on each side;
- a USB "link" / "bridge" cable (it shows up as a network adapter on both computers);
- a USB-C cable only between two USB4 / Thunderbolt ports, where the system offers networking over it (Windows:
  USB4 / Thunderbolt networking; Linux: the thunderbolt-net module). An ordinary USB-C port (USB 3) cannot connect
  two computers: nothing appears on either side.
When the cable makes a network, both computers show a new network adapter (Windows: Settings > Network & internet;
Linux: `ip link`); without one, PC link has nothing to search on.

The connection is encrypted (TLS). Each installation has its own certificate; the code is made from both
certificates, so a third computer in between would show a different code.

### rclone remotes
rclone support is a plugin shipped with the program (`plugins/rclone` next to it; Configuration > Plugins can turn it
off). With [rclone](https://rclone.org/downloads) installed (on the PATH or through `FM_RCLONE`), **rclone remotes**
in the Plugins menu of the button bar lists the remotes of your rclone configuration; set them up with `rclone config`. Their passwords and tokens stay
with rclone. NextCommander starts rclone once as a local API server (only on 127.0.0.1, with a random password) and
stops it when it closes. Files are transferred through temporary files and keep their times where the service can.

### SSH tunnels
**Net > SSH tunnels...** keeps port forwardings that go through a saved SSH, SFTP or SCP connection (its login:
the agent, a key or the saved password, the jump host, the proxy and the host key are used as for its files). Each
tunnel is a card with its state (running, starting, stopped, or failed with the reason), **Start** / **Stop**,
**Edit**, **Delete** and **Start with NextCommander**. **New tunnel** asks for:
- the kind: **Local** (a port of this computer leads to a host and port as the server sees them, e.g. 127.0.0.1:15432
  to the database `db.internal:5432` behind the server), **Remote** (a port of the server leads to a host and port as
  this computer sees them, e.g. the server's port 8080 to `localhost:3000` here) or **SOCKS** (a SOCKS proxy on this
  computer: browsers and tools set to it go out from the server);
- the connection it goes through, where it listens (127.0.0.1: only this computer; 0.0.0.0: the network too; for a
  remote tunnel the server must allow it with `GatewayPorts`) and where it leads.

Tunnels marked **Start with NextCommander** start when the program starts; all tunnels stop when it closes. Starting,
stopping and errors (a port already in use, a lost connection) are written to the connection's log.

### Remote sessions: RDP, VNC, SSH, Telnet, rsh, serial port

A session that opens in the terminal (SSH, Telnet, rsh, serial port) can have its own look, set on the tab with the
terminal type: **Font** and **Font size** (empty: the terminal's), a **Color scheme** (dark, light, Solarized,
*Production (dark red)*, green on black; or the terminal's colors) and a **Tab color** (a stripe and a tint on its
tab), e.g. red for a production server so it is not mistaken for a test one.
These are sessions on another computer, not folders: choosing one in the connection manager (or *Net > Saved
connections*, `nc_OpenConnection name`) does not change the panel.

- **Remote desktop in a tab** (Windows and Linux): the session opens in a tab of the main window, beside the files
  tab (like the options), and its desktop follows the tab's size (a fixed *Size in a window* is scaled into it).
  Windows draws it with its own Remote Desktop control (the engine of Remote Desktop Connection), Linux with the X11
  FreeRDP (`xfreerdp3` or `xfreerdp`). Several sessions can be open, each with a tab of the connection's name; the
  hidden ones keep running; when the tab's size changes the remote desktop follows (Windows 8.1 / Server 2012 R2 and
  later; Linux: FreeRDP's window in the tab is resized). Click into the desktop to type there (the keys then go to the
  remote computer); the tabs above switch back. A password is asked when none is saved. When the session ends the tab
  says so (why it could not connect, in the system's words) and offers *Connect again* or *Close*; the tab's ×
  disconnects. The title bar above the session has its options: **⛶ Full screen** (Windows: the whole screen with
  Remote Desktop's connection bar, whose restore button comes back to the tab and whose minimize button minimizes
  NextCommander; a desktop that follows the tab gets the screen's size meanwhile), **⤢ Fit to the tab** (Windows:
  scales the remote desktop into the tab) and **⟳ Reconnect**. *Full screen* and *Open in the client's own window*
  in the connection's settings open the client's own window instead, as described
  next. On Windows *Share the active panel's folder* shares the folder's drive. macOS has no way to show it in a tab:
  the Windows App opens.
- **Remote desktop (RDP)** in its own window opens in the system's client: *Remote Desktop Connection* (mstsc) on Windows, Microsoft's
  **Windows App** on macOS (from the App Store), **FreeRDP** (`xfreerdp3`, `sdl-freerdp3`, `xfreerdp`) or **Remmina**
  on Linux; without one you are told what to install. Settings: server, port (3389), user (`DOMAIN\user`), *Full
  screen* (otherwise a window whose desktop follows its size), and *Save password*. A saved password is handed to
  the client without a command line: on Windows it goes to the credential store for the current logon only (as
  `TERMSRV/server`, where mstsc looks), on Linux to FreeRDP's standard input. FreeRDP cannot ask for a password by
  itself here, so on Linux it is asked when none is saved. On macOS the Windows App asks and remembers it itself.
  The remote computer must allow remote desktop connections: Windows Pro / Server (not Home), or Linux with xrdp.
- **Telnet** opens a tab in the internal terminal (Ctrl+`), titled with the connection's name. The server asks for
  the user and the password there. The terminal says it is xterm and sends its size, again when it changes. When the
  server closes the connection the tab stays with the last screen; Enter connects again. Telnet tabs are not
  reopened with the shells at the next start.
  Telnet is **not encrypted**: everything typed, the password too, can be read on the network. Prefer SSH where the
  device has it.
- **Remote desktop extras:** *Share the active panel's folder* makes the folder of the active panel visible in the
  session (FreeRDP shares that folder under its name; Remote Desktop Connection shares only whole drives, so it
  shares the folder's drive). **Files (SMB)** in the connection manager opens the same computer's shares in the panel:
  with a saved SMB connection to that server, else with the remote desktop's user.
- **SSH** opens a shell of the server in a tab of the internal terminal. It logs in as SFTP does: the password
  (asked in a dialog, or saved), a private key file or the SSH agent; through a jump host or a proxy when they are
  set. The server's key is checked as for SFTP (a key trusted for SFTP on the same server is trusted here too; a
  pinned key must match). The terminal type (xterm-256color unless you set another) and the window size go to the
  server, again when the window changes. *Command when it opens* is typed into the shell first (e.g. `tmux attach`).
  `exit` closes the session; the tab stays with the last screen, and Enter connects again.
  **Files (SFTP)** (in the terminal's title while an SSH tab is shown, and in the connection manager) opens the
  server's files in a new tab of the active panel: through a saved SFTP or SCP connection to the same server, else
  with the session's own login (key file, agent, jump host, proxy and its saved password). When the shell reports its
  folder (OSC 7, as many prompts do), the files open in that folder; otherwise at the root.
- **VNC** opens in a VNC viewer of this computer: a `vncviewer` (TigerVNC, UltraVNC, TurboVNC), RealVNC Viewer or
  TightVNC (on Windows also where they are installed), macOS's Screen Sharing, or Remmina / KRDC on Linux. Port 5900
  is display `:0`, 5901 display `:1` and so on. Options: full screen, view only (no keys or mouse sent). The viewer
  asks for the password.
- **rsh / rlogin** is for old Unix hosts that have no SSH. Without a command it is an rlogin session (port 513) in
  the terminal; with a command, rsh runs it (port 514) and the tab shows its output. There is no password: the
  server lets this computer in by its `.rhosts` or `hosts.equiv` (with the *user on this computer* and the *user on
  the server*); rlogin asks for one in the terminal when it does not. rlogin tells the server the window's size when it asks, and again
  when the window changes (on a direct connection). The connection comes from a port below 1024
  when this computer allows it, as servers usually want (on Linux and macOS only as root). Nothing is encrypted.
- **Serial port** opens a port of this computer (`COM3` on Windows; `/dev/ttyUSB0`, `/dev/ttyACM0` on Linux;
  `/dev/cu.usbserial-...` on macOS; a bare name means `/dev/<name>`) in a tab of the internal terminal: the console
  of a switch, a router, a server or a board. The port box offers the ports found. Settings: the speed (300 to
  921600; usually 9600 or 115200) on the General tab; data bits, parity, stop bits, flow control (none, XON / XOFF,
  RTS / CTS), DTR, terminal type, character set, what Backspace and Enter send (CR by default) and local echo (off:
  devices echo themselves) on the Advanced tab. `serial://COM3:115200` opens a port with that speed. *Test* checks
  that the port opens. On Linux the user must be in the `dialout` group (`uucp` on some systems).

*Test* in the connection manager checks that the server answers on the port.

### Options of the Advanced tab

Besides the timeout and the number of simultaneous connections, the **Advanced** tab of a connection has the
options of its type:

| Type | Options |
| --- | --- |
| SFTP, SCP | Jump host; keep-alive; request size (SFTP: larger requests are faster on far servers); pinned host key. |
| FTP, FTPS | File name encoding; folder listing (MLSD when the server has it, or always LIST); the server's time zone (for servers that list local times); active mode ports of its own; keep-alive (NOOP); FTPS: encrypt the data connections too, TLS version, pinned certificate. |
| WebDAV | Authentication (as the server asks, Basic, Digest, NTLM, Negotiate); HTTPS: TLS version, pinned certificate. |
| Amazon S3 | Server-side encryption and KMS key; storage class of uploads (STANDARD_IA, GLACIER_IR, DEEP_ARCHIVE ...); ACL of uploads; part size of large uploads; requester pays; virtual-hosted addresses; plain HTTP; TLS version, pinned certificate. |
| Azure | Access tier of uploaded blobs (Hot, Cool, Cold, Archive); plain HTTP; TLS version, pinned certificate. |
| SMB | Show the hidden administrative shares (C$, ADMIN$). |
| Google Drive, OneDrive, Box | Delete permanently instead of to the service's trash (OneDrive: work / school accounts; personal ones keep the recycle bin). Google Drive: Google documents as Office, OpenDocument or PDF files. |
| Remote desktop | Tabs of their own. **Display:** in a tab or the client's own window, full screen, all monitors, size in a window, colors. **Local resources:** sound (here, on the remote computer, none) and its quality, the microphone, shared clipboard, printers, drives, the active panel's folder, smart cards. **Experience:** the connection's speed (detected, or LAN to modem) and the visual effects it allows (background, font smoothing, desktop composition, window contents while dragging, animations, visual styles; each can be changed with a chosen speed). **Advanced:** Remote Desktop Gateway, where Windows key combinations go, reconnecting by itself, administrative session. All go to Remote Desktop Connection's .rdp file, the control of a tab and FreeRDP's arguments. |
| Telnet | Terminal type, character set, what Backspace (DEL or Ctrl+H) and Enter (CR LF, CR NUL, LF) send, local echo, keep-alive. |
| SSH | Jump host, keep-alive, pinned host key, command when it opens, terminal type, character set. |
| VNC | Full screen, view only. |
| rsh / rlogin | Command (empty: rlogin), terminal type, character set; the users on the Login tab. |
| Serial port | Data bits, parity, stop bits, flow control, DTR, terminal type, character set, Backspace, Enter, local echo; the speed on the General tab. |

A **pinned** host key or certificate (`SHA256:...`) is the only one trusted for the connection: another one is
refused without asking.

## Security

The first time you connect to an SFTP server, or to a server using an encrypted (TLS) connection, NextCommander
shows its key or certificate fingerprint and asks you to trust it. Compare it with what the server's
administrator gives you. If the key changes later, you get a clear warning, because this can mean someone is
intercepting the connection.

## Saved passwords and the master password

- When you log in for the first time you can choose to save the password. It is saved only after a successful
  login, and asked again if it turns out to be wrong.
- Saved passwords are stored encrypted in the [settings folder](settings-folder.md).
- **Shared logins:** several connections can use one login (a domain account, the deploy user of the web servers).
  In the connection editor, **Login** chooses between the connection's own user and password and a shared login;
  **Logins...** next to it edits them: name, user, password (kept once in the encrypted store) and, for SFTP, SCP
  and SSH, a private key file. The connections take the login's user and key; its password is asked once and then
  used by all of them. Changing the login changes them all; deleting it leaves them with the user and their own
  password. An exported connections file keeps the user, not the login. The new connection wizard offers the
  shared logins under **Login** too (when there are any).
- With a **master password** (*Net > Master password...*), the passwords are encrypted with a key made from it.
  It is asked once per session. Leave the new master password empty to remove it.
- Without a master password, the passwords are protected with a key tied to your user account on this computer.

## Network neighborhood

*Net > Network (machines of the local network)*, or `\` in the drive box, shows the machines of your local network
as folders.

- NextCommander looks for known neighbors, devices that announce themselves (NAS, Mac, Linux), and machines
  that answer on the SFTP, FTP, SMB and web ports. Ctrl+R scans again.
- Inside a machine you see its services (SFTP, FTP, WebDAV) and, on Windows, its shared folders. On Linux and
  macOS an "SMB" entry lists the shares.
- Opening a service connects to it: with your saved connection for that machine if there is one, otherwise you
  are asked for a user name and password.
- *Net > Show admin shares* also lists hidden administrative shares such as `C$` (Windows).
- This view is for browsing only; you cannot copy into it or delete from it.

## Proxy

WebDAV, S3, Azure Blob, Google Drive, OneDrive, Dropbox (also their sign-in), URL downloads and the update check
go through the proxy chosen on the *Operation > Proxy* page of the [Configuration window](configuration.md):

- **Use the system's proxy** (default): the proxy of Windows' Internet settings, the macOS network settings, or the
  `http_proxy` / `https_proxy` / `no_proxy` variables on Linux.
- **No proxy**: always directly, even when the system has a proxy.
- **Use this proxy**: a proxy with its server and port (or a full address such as `http://proxy:3128`,
  `socks4a://proxy:1080` or `ssh://bastion:22`), a user name and password when the proxy asks for one (the password
  is kept encrypted with your other saved passwords), and the addresses that go directly (`localhost; *.local;
  10.*`; local names without a dot too, if you like). Its type:
  - **HTTP**: an HTTP proxy (other connections through CONNECT).
  - **HTTPS (TLS to the proxy)**: the same, reached over an encrypted connection; its certificate must be valid.
  - **SOCKS5**, **SOCKS4a**, **SOCKS4**: SOCKS proxies (SOCKS5 with a user name and password; SOCKS4 sends the user
    name as its user id; SOCKS4 needs an IPv4 address, which is looked up here, SOCKS4a sends the name).
  - **SSH tunnel**: an SSH server, as `ssh -D`: every connection is a channel of one SSH session. It logs in with
    the password or a private key file (its passphrase as the password); the server's key is checked and asked
    for like any SSH server's.

  With **Also for FTP, SFTP and SMB connections** (on by default) these go through it as well: FTP (also its data
  connections), SFTP and SCP, SMB and Telnet. An HTTP proxy has to allow CONNECT to their ports (21, 22, 445 ...);
  SOCKS and SSH proxies take any connection.
- **Automatic configuration script (PAC)**: the address of a proxy auto-config script (`http://wpad.example.com/wpad.dat`,
  or a file). Its `FindProxyForURL` chooses the way of every address: directly, or through the HTTP, HTTPS or
  SOCKS proxy it names (the first one of its answer). The user name and password above are used for these proxies.
  The script is read without a proxy and kept for half an hour; a script that cannot be read or fails sends the
  connections directly.

**Test** requests an address with the settings as they are in the window and tells whether it went through the
proxy, the answer and how long it took. Connections that are already open keep their way until they connect again.
The system's proxy is used for web requests only.

### A connection's own proxy

The **Proxy** tab of a connection in the connection manager chooses its way, whatever the program's setting is:

- **The program's setting**: as above (the default).
- **No proxy**: always directly.
- **Its own proxy**: one of the types above with its server, port, user and password (an SSH tunnel also with a
  private key). The password is kept in the password store when the connection is saved.

Remote desktop (RDP) and VNC connections, serial ports and PC link have no Proxy tab: the RDP and VNC viewers connect themselves,
and a PC link stays on the local network.

## Downloading from the web

- If the clipboard holds web addresses, Ctrl+V in a panel offers to download them.
- *Net > Download from URL...* downloads one address, *Download the addresses of a list...* all addresses in a
  text file.
- An interrupted download continues where it stopped. Downloaded files get the system's "downloaded from the
  internet" mark.
