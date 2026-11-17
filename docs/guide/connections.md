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
| Remote desktop (RDP) | The desktop of a Windows computer (or a Linux one with xrdp), in the system's RDP client: see [Remote desktop and Telnet](#remote-desktop-rdp-and-telnet) |
| Telnet | A text session in the internal terminal (routers, switches, old systems) |

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

In the button bar, the connection manager's button opens the manager while there is no saved connection; once
there are some it is a drop-down (▾): **Connection manager...** first, then the saved connections grouped by type
(the command `nc_ConnectionsMenu` opens the same menu).

The **Plugins** button next to it (`nc_PluginsMenu`) lists the places the plugins offer (such as rclone remotes) and
the plugin list. It shows only while plugins are installed; *Configuration > Options > Layout > Button bars > Plugins button*
turns it off.

## The connection manager

![The connection manager](../screenshots/connections.png)

The manager opens in place of the left file panel, whichever panel is active (the right panel stays usable); ✕ or
Esc closes it and brings the file panel back. The connections are on the left, in folders; the selected connection or folder is on the right.
New connections start from the **New connection** button below the list.

- **Folders** can be nested and have a color label (for example red for production). Drag connections and folders
  to move them. An empty folder stays until you delete it.
- **Search** (Ctrl+F in the window) looks in names, servers, users and folders; the chips above the tree show only
  favorites, SSH or FTP connections, cloud storage, or the connections that are open now. The dot after a connection
  is green while it is connected, red when its last test failed.
- **Keys:** arrows move and open folders, Enter connects (Shift+Enter: in the right panel), F2 renames, Delete deletes
  (a folder with everything in it; its saved passwords are forgotten), Ctrl+Z puts the last deleted items back,
  Ctrl+D duplicates, Ctrl+N makes a new connection, Ctrl+S saves. The right click menu has the same, plus favorites,
  copying the address and forgetting the saved password.
- **New connection:** first choose its type from the cards (or paste an address such as
  `sftp://user@server:22/folder`, which fills it in). Then fill in the tabs:
  - **General:** the server, port and start folder (for FTP also the encryption and passive mode, for S3 the region;
    a **Service** for S3 and WebDAV, see below).
  - **Login:** the method: password, key file or the **SSH agent** (SFTP / SCP); access key or **AWS profile / SSO**
    (S3); account key / SAS or **Microsoft Entra ID** (Azure); anonymous (FTP). Cloud drives take their application id
    here.
  - **Advanced:** the timeout, simultaneous connections, a **jump host** (SFTP / SCP), the **file name encoding**
    (FTP), **server-side encryption** with SSE-S3 or SSE-KMS (S3), plain HTTP, accepting any certificate, SharePoint
    sites (OneDrive) and the favorite mark.
- **Test** connects with the settings as they are and lists the start folder; the result shows under the tabs.
  **Save** writes the connections (closing the window does too; new connections never filled in are dropped).
  **Connect** opens the connection; a folder's **Connect to all** opens each of its connections in a tab.
- **Import** (the arrow under the tree) brings in the sites of **FileZilla** (sitemanager.xml) and **WinSCP**
  (WinSCP.ini or its registry key) with their folders, key files, jump hosts (WinSCP tunnels) and saved passwords
  (into the encrypted store; FileZilla's master-password-encrypted ones cannot be read).

For S3 and WebDAV, the **Service** list fills in the server, port, region and start folder of well-known services:
Amazon S3, Google Cloud Storage, Cloudflare R2, Backblaze B2, Wasabi, DigitalOcean Spaces, Hetzner and MinIO;
Nextcloud / ownCloud, Yandex Disk, pCloud and Koofr. A part of the server name in capitals (such as `ACCOUNT_ID`
or `REGION`) is yours to replace; the note under the fields says how.

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

The connection is encrypted (TLS). Each installation has its own certificate; the code is made from both
certificates, so a third computer in between would show a different code.

### rclone remotes
rclone support is a plugin shipped with the program (`plugins/rclone` next to it; Configuration > Plugins can turn it
off). With [rclone](https://rclone.org/downloads) installed (on the PATH or through `FM_RCLONE`), **rclone remotes**
in the Plugins menu of the button bar lists the remotes of your rclone configuration; set them up with `rclone config`. Their passwords and tokens stay
with rclone. NextCommander starts rclone once as a local API server (only on 127.0.0.1, with a random password) and
stops it when it closes. Files are transferred through temporary files and keep their times where the service can.

### Remote desktop (RDP) and Telnet
These two are sessions on another computer, not folders: choosing one in the connection manager (or *Net > Saved
connections*, `nc_OpenConnection name`) does not change the panel.

- **Remote desktop (RDP)** opens in the system's client: *Remote Desktop Connection* (mstsc) on Windows, Microsoft's
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
| Remote desktop | Full screen, all monitors, size in a window, colors, sound (here, on the remote computer, none), Remote Desktop Gateway, shared clipboard, drives and printers, administrative session. |
| Telnet | Terminal type, character set, what Backspace (DEL or Ctrl+H) and Enter (CR LF, CR NUL, LF) send, local echo, keep-alive. |

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

Remote desktop (RDP) connections and PC link have no Proxy tab: the RDP client connects itself, and a PC link stays
on the local network.

## Downloading from the web

- If the clipboard holds web addresses, Ctrl+V in a panel offers to download them.
- *Net > Download from URL...* downloads one address, *Download the addresses of a list...* all addresses in a
  text file.
- An interrupted download continues where it stopped. Downloaded files get the system's "downloaded from the
  internet" mark.
