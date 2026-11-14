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
| Android over ADB | Phones with USB debugging on; also on macOS, where MTP is not available |
| rclone remotes | Every remote you set up with `rclone config` (70+ services) |

## The Net menu

- **Connection manager...** (Ctrl+F): your saved connections.
- **New connection:** a submenu with every connection type. The new connection is saved and opened.
- **Saved connections:** opens a saved connection directly.
- **Connect by address...** (Ctrl+N): type an address and connect without saving.
- **Disconnect** (Ctrl+Shift+F): closes the connection of the active panel; the panel goes back to the last local
  folder.
- **Network (machines of the local network)**: see [Network neighborhood](#network-neighborhood).
- **Map network drive...**, **Disconnect network drives...**, **Share current directory...**,
  **Unshare directory...**: the Windows network drive and sharing dialogs (Windows).
- **Download from URL...** and **Download the addresses of a list...**: download files from the web.
- **Show hidden files on FTP**.
- **Master password...**: see [Saved passwords](#saved-passwords-and-the-master-password).
- **rclone remotes**: see [rclone remotes](#rclone-remotes).

## The connection manager

![The connection manager](../screenshots/connections.png)

Each connection is shown with its type's icon. **New...** creates a connection, **Edit...** changes it,
**Copy** duplicates it, **Delete** removes it, and **Forget saved password** removes its stored password.
**Connect** opens it in the active panel.

A connection has a protocol, a server, a port, a user and a start folder, plus options that depend on the type.
The dialog shows hints for each type.

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

## Notes per type

### SFTP
Log in with a password or with a private key file (which may have its own passphrase).

### SCP
For SSH servers that have no SFTP subsystem. The login (password or key file) is the same as for SFTP. Folders are
listed and changed with shell commands on the server (`stat`, `mkdir`, `mv`, `rm`, `touch`), so the server needs a
POSIX shell; files are sent with SCP.

### FTP / FTPS
Choose plain FTP, FTPS with explicit TLS, or FTPS with implicit TLS (port 990), and passive or active mode.
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

### Azure Blob Storage
The user is the storage account; the password is the account key or a SAS token. The root lists the containers.

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

### rclone remotes
With [rclone](https://rclone.org/downloads) installed (on the PATH or through `FM_RCLONE`), **Net > rclone remotes**
lists the remotes of your rclone configuration; set them up with `rclone config`. Their passwords and tokens stay
with rclone. NextCommander starts rclone once as a local API server (only on 127.0.0.1, with a random password) and
stops it when it closes. Files are transferred through temporary files and keep their times where the service can.

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
- **Use this proxy**: an HTTP or SOCKS5 proxy with its server and port (or a full address such as
  `http://proxy:3128` or `socks5://proxy:1080`), a user name and password when the proxy asks for one (the password
  is kept encrypted with your other saved passwords), and the addresses that go directly (`localhost; *.local;
  10.*`; local names without a dot too, if you like). With **Also for FTP, SFTP and SMB connections** (on by
  default) these go through it as well: FTP (also its data connections) and SFTP with their own proxy support, SMB
  through a local port that leads to the proxy. An HTTP proxy has to allow CONNECT to their ports (21, 22, 445 ...);
  a SOCKS5 proxy takes any connection.

**Test** requests an address with the settings as they are in the window and tells whether it went through the
proxy, the answer and how long it took. Connections that are already open keep their way until they connect again.
The system's proxy is used for web requests only.

## Downloading from the web

- If the clipboard holds web addresses, Ctrl+V in a panel offers to download them.
- *Net > Download from URL...* downloads one address, *Download the addresses of a list...* all addresses in a
  text file.
- An interrupted download continues where it stopped. Downloaded files get the system's "downloaded from the
  internet" mark.
