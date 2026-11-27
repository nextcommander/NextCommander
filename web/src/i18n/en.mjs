// English texts of the site. tr.mjs has the same keys in the same shape (build.mjs checks it).
// Texts may hold a little inline HTML (<kbd>, <code>, <strong>); they are written here, never taken from visitors.

export default {
  lang: "en",
  locale: "en_US",
  name: "English",

  meta: {
    "": { title: "NextCommander — the keyboard-driven dual-pane file manager", description: "A dual-pane file manager for Windows, Linux and macOS: two panels, tabs, archives, servers, clouds, containers, compare and merge, all within a few keys." },
    features: { title: "Features — NextCommander", description: "Everything NextCommander does: panels and tabs, file operations with undo, viewer and editor, search, compare and merge, archives, automation and looks." },
    connections: { title: "Servers, clouds and devices — NextCommander", description: "FTP, SFTP, SMB, WebDAV, S3, Azure, Google Drive, OneDrive, Dropbox, Box, Docker, Kubernetes, phones, RDP, SSH and more in the same panels as your local files." },
    plugins: { title: "Plugins — NextCommander", description: "Git, Docker, Kubernetes, SQLite, rclone, GitHub and GitLab, virtual disks, packages and viewers ship as plugins. Write your own in C#." },
    download: { title: "Download — NextCommander", description: "Download NextCommander for Windows, Linux or macOS, x64 and Arm. Self-contained packages, a portable zip, nothing else to install." },
    faq: { title: "Questions and answers — NextCommander", description: "Answers about installing, settings, connections, security, languages and plugins of NextCommander." },
  },

  nav: {
    "": "Home", features: "Features", connections: "Connections", plugins: "Plugins", download: "Download", faq: "FAQ",
    skip: "Skip to the content", menu: "Menu", theme: "Light or dark", language: "Language", github: "GitHub",
  },

  common: {
    download: "Download",
    downloadFor: "Download for {0}",
    getStarted: "Getting started guide",
    allFeatures: "All features",
    seeConnections: "Every connection type",
    seePlugins: "The plugins",
    readGuide: "Read the guide",
    latestRelease: "Latest release on GitHub",
    copy: "Copy",
    copied: "Copied",
    shortcut: "Shortcut",
    where: "Where",
    screenshotOf: "Screenshot: {0}",
    onThisPage: "On this page",
    needs: "Needs",
  },

  home: {
    pill: "Windows · Linux · macOS · x64 and Arm",
    titleA: "Two panels.",
    titleB: "Every file within a few keys.",
    lead: "NextCommander is a dual-pane file manager you drive from the keyboard. Copy between the panels with one key, open archives, servers, clouds and containers like folders, compare and merge, and keep your hands where they work.",
    fkeys: [["F3", "View"], ["F4", "Edit"], ["F5", "Copy"], ["F6", "Move"], ["F7", "New folder"], ["F8", "Delete"]],
    stats: [["25+", "connection types"], ["9", "plugins shipped with it"], ["6", "languages built in"], ["4", "panels at once"]],
    why: {
      eyebrow: "Why NextCommander",
      title: "Built for people who move a lot of files",
      lead: "On their disk, in archives, on servers and in the cloud — without reaching for the mouse.",
      items: [
        { icon: "keyboard", title: "The keyboard first", body: "Every action is a command with a key you can change. The command palette finds any of them by typing a few letters." },
        { icon: "globe", title: "One place for every file", body: "Local disks, archives, FTP and SFTP, S3, cloud drives, Docker, Kubernetes and phones open in the same panels and copy across." },
        { icon: "undo", title: "Safe to work fast", body: "Jobs run in the background with a queue and pause; Ctrl+Z undoes copy, move, rename and delete to the Recycle Bin." },
        { icon: "layers", title: "Everything in the box", body: "Viewer, editor, compare and merge, multi-rename, synchronize, folder sizes and a terminal — self-contained packages, nothing else to install." },
      ],
    },
    tour: {
      eyebrow: "Tour",
      title: "Look around the way you would in the app",
      lead: "Move the cursor with <kbd>↑</kbd> <kbd>↓</kbd> or click a line; the right side shows it, like Quick View does.",
      listTitle: "Name",
      keyTitle: "Key",
      items: [
        { name: "Two panels and tabs", key: "Tab", shot: "main-light.png", body: "Two file lists side by side, each with folder tabs. Copy, move and compare between them with one key; split a side into two panels, up to four." },
        { name: "Quick View", key: "Ctrl+Q", shot: "quick-view-right.png", body: "The file under the cursor shown next to the list: pictures, documents, PDF pages, code with syntax colors, as you move." },
        { name: "Command palette", key: "Ctrl+Shift+P", shot: "command-palette.png", body: "Any command, folder, Hotlist entry or saved connection, found by typing a few letters." },
        { name: "Viewer", key: "F3", shot: "lister.png", body: "Text with syntax colors, hex, pictures with camera RAW, audio and video, Markdown, Office documents without Office." },
        { name: "Picture metadata", key: "F3, 6", shot: "picture-metadata.png", body: "Camera, lens, exposure, GPS position with a map link, IPTC and XMP, and every tag of a photo." },
        { name: "Archives", key: "Enter", shot: "archive.png", body: "zip, 7z, rar, tar, iso, virtual machine disks, .deb, .rpm and .msi packages open like folders." },
        { name: "Find files", key: "Alt+F7", shot: "find-files.png", body: "By name, content, size, date and attributes, also inside Office files. A saved search opens like a folder of its results." },
        { name: "Multi-Rename Tool", key: "Ctrl+M", shot: "multi-rename.png", body: "Rename many files at once with patterns, counters, search and replace, and a preview of every new name." },
        { name: "Compare and merge", where: "Files menu", shot: "compare.png", body: "Two files side by side, edited in place; copy differences across, merge three files, make and apply patches." },
        { name: "Synchronize directories", where: "Tools menu", shot: "synchronize.png", body: "Compare two folders by date, size or content and make them equal in one direction or both." },
        { name: "Folder sizes", where: "Tools menu", shot: "disk-usage.png", body: "Where the space went: a sortable tree, a treemap and the file types, with delete right there." },
        { name: "Connection manager", key: "Ctrl+F", shot: "connections.png", body: "Saved servers, clouds and sessions in folders, with the options of each type and a log per connection." },
      ],
    },
    connections: {
      eyebrow: "Connections",
      title: "Servers, clouds and devices in the same panels",
      lead: "A remote folder is a folder: copy to it with <kbd>F5</kbd>, view with <kbd>F3</kbd>, compare it with a local one.",
      chips: ["FTP / FTPS", "SFTP", "SCP", "WebDAV", "SMB", "Amazon S3", "Azure Blob", "Azure Files", "Google Drive", "OneDrive", "Dropbox", "Box", "rclone", "Docker", "Kubernetes", "GitHub", "GitLab", "Android (ADB)", "MTP phones", "PC link", "RDP", "VNC", "SSH", "Telnet", "Serial port"],
    },
    looks: {
      eyebrow: "Looks",
      title: "Fits the system it runs on",
      lead: "Follows the system's light or dark mode, or takes a macOS or Ubuntu look. Three icon packs, six languages built in, your own themes, and every shortcut and button can be changed.",
      options: [["Light", "main-light.png"], ["Dark", "main-dark.png"], ["Ubuntu", "main-ubuntu.png"], ["macOS dark", "main-macos-dark.png"]],
    },
    cta: {
      title: "Try it on your own files",
      lead: "Self-contained packages for Windows, Linux and macOS, x64 and Arm. The portable zip runs from a USB stick.",
    },
  },

  features: {
    eyebrow: "Features",
    title: "Everything a file manager should do, from the keyboard",
    lead: "A tour of what NextCommander does, part by part. Each part links to its guide for the details.",
    sections: [
      {
        id: "panels", icon: "columns", title: "Two panels and tabs", shot: "tree-and-thumbnails.png", guide: "getting-started",
        lead: "Two file lists side by side; copy, move and compare between them with one key.",
        points: [
          "Folder tabs in each panel: open, close, reorder by dragging, lock, rename, save and load tab sets.",
          "Brief, full, comments and thumbnail views; a folder tree inside the panel; custom column sets such as photo dimensions, music tags or line counts.",
          "Folder history, most frequently used folders and a Hotlist of favorite folders.",
          "Quick search and quick filter while you type; Branch View shows the files of all subfolders in one list.",
          "Split a side into two panels, side by side or one above the other — up to four panels.",
          "Saved workspaces: the tabs of both panels and the terminal under a name, opened again with one key.",
        ],
      },
      {
        id: "operations", icon: "copy", title: "File operations", shot: "permissions.png", guide: "getting-started",
        lead: "Copy, move, rename and delete with the F keys, by drag and drop or through the clipboard.",
        points: [
          "Copy and move jobs run in the background with a progress window, a queue, pause and a speed limit; copies can be verified after writing.",
          "<kbd>Ctrl+Z</kbd> undoes copy, move, rename, multi-rename, new folder and deleting into the Recycle Bin.",
          "When an operation fails for missing rights, it offers to run again with administrator rights through the system's own dialog.",
          "Tags with color labels on files and folders, a Tags column and a filter by tag.",
          "Rules carried out while the program runs: arriving files moved or copied, folders synchronized every night.",
          "Permissions: Unix mode, owner and group (also over SFTP) and Windows access lists in one dialog. Secure delete overwrites files first.",
        ],
      },
      {
        id: "viewer", icon: "eye", title: "Viewer and editor", shot: "lister.png", guide: "tools",
        lead: "Lister (F3) and Quick View show almost any file without another program.",
        points: [
          "Text with syntax colors, binary and hex, pictures including camera RAW, audio and video (with VLC installed).",
          "Markdown, HTML and RTF previews; PDF pages; Word, Excel, PowerPoint and OpenDocument files without Office.",
          "CSV as a sortable table; JSON, XML and YAML as a tree; e-mails (.eml, .msg), e-books, Jupyter notebooks, certificates, programs, fonts, contacts and calendars.",
          "Picture metadata: camera, lens, exposure, GPS with a map link, IPTC, XMP and every tag.",
          "A built-in text editor (F4) that keeps the file's encoding and line endings.",
        ],
      },
      {
        id: "search", icon: "search", title: "Search and tools", shot: "find-files.png", guide: "tools",
        lead: "Find what you need and change many files at once.",
        points: [
          "Find files by name, content, size, date and attributes — also inside Office and OpenDocument files.",
          "A saved search opens in a panel like a folder of its results, searched again on every refresh.",
          "An optional file index makes searches and folder sizes nearly instant; Everything, plocate and Spotlight are used when present.",
          "Multi-Rename Tool with patterns, counters, search and replace, and a preview of every name.",
          "Duplicate files, folder sizes with a treemap, checksums (including BLAKE3), split and combine, and a built-in terminal.",
        ],
      },
      {
        id: "compare", icon: "diff", title: "Compare and merge", shot: "compare.png", guide: "tools",
        lead: "Differences side by side, edited in place — like a dedicated diff tool, built in.",
        points: [
          "Two files side by side with differences down to the word; copy blocks across, undo, save.",
          "Ignore case, white space, blank lines and line endings; line filters with regular expressions; moved blocks marked.",
          "Three-way merge with a result pane, conflict navigation and \"take left / right / both\"; works with Git conflicts.",
          "Hex compare, picture compare with a difference layer, and PDF or Office documents compared as text.",
          "Synchronize directories compares folders by date, size or content, opens each pair, and makes them equal.",
          "Make unified patches, apply them with a preview, and export an HTML report.",
        ],
      },
      {
        id: "archives", icon: "archive", title: "Archives, disks and packages", shot: "archive.png", guide: "archives",
        lead: "Containers open like folders — Enter goes in, Backspace comes back.",
        points: [
          "zip, 7z, rar, tar and other archives, and .iso images; zip files changed in place.",
          "Pack to zip, tar, iso and (with 7-Zip installed) 7z, with passwords; unpack and test archives; mount .iso images (on Windows also .vhd / .vhdx).",
          "Virtual machine disks (.vhd, .vhdx, .vmdk, .vdi, .dmg) as folders of their partitions: NTFS, FAT, ext, XFS, Btrfs, HFS+ and LVM, read-only, on every system.",
          "Packages and installers: .deb, .rpm, .cab, .msi (files at their install paths) and container images (docker save, OCI).",
          "SQLite databases as folders of their tables; a Git repository's branches, tags and commits as folders.",
        ],
      },
      {
        id: "automation", icon: "bolt", title: "Macros and scripts", shot: "command-palette.png", guide: "scripts",
        lead: "Teach it the work you repeat.",
        points: [
          "Record the commands you run as a macro — a command of your own with a key and a button.",
          "JavaScript commands that drive the program, custom columns written in JavaScript, and file systems that show a web service's files in a panel.",
          "Command palette (<kbd>Ctrl+Shift+P</kbd>) for every command, folder, Hotlist entry and connection.",
          "Every shortcut, menu entry and button bar can be changed.",
        ],
      },
      {
        id: "looks", icon: "palette", title: "Looks and languages", shot: "configuration-color.png", guide: "themes",
        lead: "Make it yours, without editing a settings file.",
        points: [
          "Follows the system's light / dark mode; Dark, macOS and Ubuntu looks built in, or your own color theme from the theme editor.",
          "Three icon packs (Colored, Monochrome, Fluent) or your own.",
          "English, Turkish, German, Spanish, French and Chinese built in; add a language with a single file.",
          "Settings backups at every start, restored part by part: settings, connections, Hotlist, shortcuts, button bars, theme.",
        ],
      },
    ],
  },

  connections: {
    eyebrow: "Connections",
    title: "Servers, clouds and devices in the same panels",
    lead: "Every connection opens as a folder. Copy, move, view, edit, compare and synchronize work across all of them — from an SFTP server to a OneDrive folder, from a Docker volume to a phone.",
    shot: "connections.png",
    groups: [
      { icon: "server", title: "File servers", items: [["FTP and FTPS", "Explicit and implicit TLS, active and passive, pinned certificates, hidden files per connection."], ["SFTP and SCP", "Keys, the SSH agent and Pageant, jump hosts, pinned host keys, keep-alive."], ["WebDAV", "Ready-made settings for well-known services."], ["SMB", "Windows shares and Samba, also from Linux and macOS."], ["Web folder listings", "http:// and https:// directory pages open like folders, read-only."]] },
      { icon: "bucket", title: "Object storage", items: [["Amazon S3 and compatible stores", "AWS profiles and SSO, storage classes, ready-made settings for S3-compatible services."], ["Azure Blob Storage and Azure Files", "Shared keys or Microsoft Entra sign-in."]] },
      { icon: "cloud", title: "Cloud drives", items: [["Google Drive", "Including Shared with me and shared drives."], ["OneDrive", "Including SharePoint libraries."], ["Dropbox and Box", "Signed in through the browser."], ["rclone", "Every remote of an installed rclone."], ["Sync folders", "Shortcuts to the local folders of OneDrive, iCloud Drive, Dropbox, Google Drive and WSL."]] },
      { icon: "box", title: "Containers and code", items: [["Docker", "Containers and volumes; stopped containers read-only; start, stop, restart from the menu."], ["Kubernetes", "Context, namespace, pod and container as folders; logs, describe, delete, restart."], ["GitHub and GitLab", "Branches, tags, releases and files without cloning; clone with one command."]] },
      { icon: "phone", title: "Devices", items: [["Android phones over ADB", "USB debugging; phones appear as drives."], ["Phones and cameras over MTP", "Copy photos off a phone, put files on it, on Windows, Linux and macOS."], ["PC link", "Two computers running NextCommander share folders over a USB or Ethernet cable or the network, encrypted, paired once with a code."], ["Trash", "The Recycle Bin in a panel: restore, copy out, delete for good."]] },
      { icon: "terminal", title: "Sessions", items: [["Remote desktop (RDP)", "In a tab of the main window on Windows and Linux, or the system's client."], ["VNC", "In the system's viewer."], ["SSH, Telnet, rsh", "In tabs of the built-in terminal."], ["Serial ports", "A console for devices on COM and tty ports."]] },
    ],
    extras: {
      title: "Around every connection",
      items: [
        { icon: "folder", title: "Connection manager", body: "Saved connections in folders with drag and drop, a wizard for new ones, a test button, import from FileZilla and WinSCP, and the machines of your local network." },
        { icon: "lock", title: "Saved passwords", body: "Kept encrypted, protected by an optional master password; host keys and certificates are pinned and asked about when they change." },
        { icon: "route", title: "Proxies and tunnels", body: "The system's proxy, HTTP, HTTPS, SOCKS4 / 4a / 5, an SSH tunnel or a PAC script — for the whole program or per connection. Local, remote and SOCKS port forwarding over SSH." },
        { icon: "log", title: "A log per connection", body: "Each connection writes what happened — connecting, login, keys, errors with their causes — never a password. \"Show the log\" is one click from an error." },
      ],
    },
  },

  plugins: {
    eyebrow: "Plugins",
    title: "Shipped as plugins, open to your own",
    lead: "Nine plugins come with NextCommander and load at start. Each can be turned off, has its settings on the Plugins page of the configuration, and brings its own translations.",
    items: [
      { name: "Git", icon: "git", needs: "git", body: "Branches, tags and commits of a repository as read-only folders; Git columns with each file's state and last commit; stage, commit, switch branches, fetch, pull, push and merge from the menu." },
      { name: "Docker", icon: "box", needs: "docker", body: "The files of containers and volumes in a panel; stopped containers read-only; start, stop, restart, pause and resume from the right click menu." },
      { name: "Kubernetes", icon: "wheel", needs: "kubectl", body: "Contexts, namespaces, pods and containers as folders; pod logs, describe, delete and deployment restart; status, ready, restarts and node columns." },
      { name: "SQLite", icon: "db", needs: "", body: "Databases open as folders of their tables; copy a table out as CSV or JSON or its schema; F3 shows tables, columns, first rows and indexes." },
      { name: "rclone", icon: "cloud", needs: "rclone", body: "Every remote of an installed rclone — dozens of cloud services — in the same panels." },
      { name: "GitHub and GitLab", icon: "code", needs: "", body: "Repositories read-only through the web APIs, without cloning: branches, tags, releases and files; GitHub Enterprise and self-hosted GitLab too; tokens kept as secrets." },
      { name: "Virtual disks", icon: "disk", needs: "", body: ".vhd, .vhdx, .vmdk, .vdi and .dmg as folders of their volumes: NTFS, FAT, ext, XFS, Btrfs, HFS+ and LVM, read-only, on every system." },
      { name: "Packages", icon: "package", needs: "", body: ".deb, .rpm, .cab, .msi and container images as folders; F3 shows name, version, dependencies or the image's configuration." },
      { name: "Viewers", icon: "eye", needs: "", body: "CSV tables, JSON / XML / YAML trees, e-mails, e-books, notebooks, certificates, programs, fonts, contacts and picture metadata in the Lister." },
    ],
    needsLabel: "Uses the installed",
    builtIn: "Nothing else needed",
    own: {
      title: "Write your own",
      lead: "A plugin is a .NET assembly in the plugins folder. It can add any of these, and the program draws its settings for it.",
      points: [
        ["File systems", "A scheme of your own (hello://) that opens in a panel; copy, view and edit work across it."],
        ["Columns", "Fields of your own in any view mode."],
        ["Viewers", "Files the Lister shows through your plugin."],
        ["Packers", "An archive format in the Pack dialog."],
        ["Actions", "Entries in the Plugins menu and the file list's menu, with prompts and confirmations."],
        ["Settings", "Text, numbers, choices, programs, folders and secrets on the Plugins page."],
      ],
    },
  },

  download: {
    eyebrow: "Download",
    title: "Self-contained, nothing else to install",
    lead: "Every package carries what it needs. Pick your system; the packages are on the latest release.",
    platforms: [
      {
        os: "windows", name: "Windows", archs: "Windows 10 and 11 · x64 and Arm64",
        files: [["-setup.exe", "Installs for your user without administrator rights, or for all users; Start menu, optional desktop icon and \"Open in NextCommander\" in Explorer."], [".zip", "Unpack anywhere and start NextCommander.exe."], ["-portable.zip", "Keeps its settings in its own folder; runs from a USB stick."]],
        command: "",
      },
      {
        os: "linux", name: "Linux", archs: "x64 and Arm64",
        files: [[".deb", "Debian, Ubuntu, Mint ..."], [".rpm", "Fedora, RHEL, openSUSE ..."], [".tar.gz", "Unpack and start NextCommander."]],
        command: "sudo apt install ./NextCommander-<version>-linux-x64.deb",
      },
      {
        os: "mac", name: "macOS", archs: "Apple silicon and Intel",
        files: [["NextCommander.app", "In a zip or a disk image; drag it to Applications."]],
        command: "",
        note: "A copy not signed by a registered developer may first need System Settings › Privacy & Security › Open Anyway.",
      },
    ],
    get: "Get it for {0}",
    yours: "Your system",
    helpers: {
      title: "Optional helpers",
      lead: "Some features use programs you may already have. Without them, only that feature is turned off.",
      items: [["7-Zip", "to create 7z archives"], ["VLC", "to play audio and video in the Lister"], ["Everything, plocate, Spotlight", "indexed search"], ["LibreOffice", "previews of doc, xls, ppt, Visio ..."], ["git, docker, kubectl, rclone", "the plugins of the same names"]],
    },
    verify: {
      title: "Check the download",
      body: "Every release has a SHA256SUMS file. Compare the checksum of the file you downloaded with the line for it:",
      windows: "Get-FileHash .\\NextCommander-*-setup.exe -Algorithm SHA256",
      unix: "sha256sum -c SHA256SUMS --ignore-missing",
    },
    after: {
      title: "After installing",
      items: [
        ["Your settings", "Live in one folder (Windows: %APPDATA%\\NextCommander, Linux: ~/.config/NextCommander, macOS: ~/Library/Application Support/NextCommander) and are backed up at every start."],
        ["Updates", "Install a new version over the old one; your settings stay. The program can also check for one at start (Configuration › Operation, off by default)."],
        ["Uninstall", "Windows: Settings › Apps. Linux: your package manager. macOS: move the app to the Bin. The settings folder stays until you delete it."],
      ],
    },
  },

  faq: {
    eyebrow: "Questions",
    title: "Questions and answers",
    lead: "Something not here? The guides go into every detail.",
    groups: [
      {
        title: "Getting started",
        items: [
          ["What is a dual-pane file manager?", "Two file lists side by side: the one you work in and the target. Copy (F5) and move (F6) go from one to the other without dialogs to pick a destination; the keyboard does the rest."],
          ["Do I need to install anything else?", "No. The packages are self-contained, .NET included. Programs such as 7-Zip, VLC, git or docker are only used by the features that need them."],
          ["Does it need administrator rights?", "No. The Windows setup installs for your user by default. When an operation needs more rights (a system folder), NextCommander asks whether to run just that operation through the system's own dialog."],
          ["Can I take it with me on a USB stick?", "Yes: the portable zip keeps its settings next to the program. Any copy becomes portable when a portable.json file is next to it."],
        ],
      },
      {
        title: "Working with it",
        items: [
          ["Can I use my own shortcuts?", "Every action is a command, and every command can have the keys you want. Menus, button bars, shortcuts and the command line all call the same commands."],
          ["Can I undo a mistake?", "Ctrl+Z undoes copy, move, rename, multi-rename, new folder and deleting into the Recycle Bin."],
          ["Does it work with large folders and slow servers?", "Operations run in the background with a queue, pause and a speed limit, so you keep working. An optional file index makes searching and folder sizes nearly instant."],
          ["Which languages are built in?", "English, Turkish, German, Spanish, French and Chinese (Simplified). A new language is a single file in the settings folder, and plugins bring their own translations."],
        ],
      },
      {
        title: "Connections and security",
        items: [
          ["Where are my passwords kept?", "Encrypted in the settings folder, protected by an optional master password. Logs never contain secrets."],
          ["How do I know I am talking to the right server?", "Host keys and certificates are pinned at the first connection; when one changes, NextCommander shows it and asks."],
          ["Can it go through a proxy?", "Yes: the system's, HTTP, HTTPS, SOCKS4 / 4a / 5, an SSH tunnel or a PAC script, for the whole program or one connection."],
          ["Does it send anything about me?", "No. It reaches the internet only for the connections you open and, if you turn it on, to check for a new version."],
        ],
      },
      {
        title: "Settings and plugins",
        items: [
          ["What if my settings get damaged?", "The last seven settings files are kept as backups. A damaged file is set aside and the newest working backup takes its place; you can also restore any backup part by part."],
          ["Can I move my settings to another computer?", "Configuration › Export writes them to a file and Import reads it on the other machine."],
          ["How do I write a plugin?", "A plugin is a .NET assembly that implements the interfaces you need: file systems, columns, viewers, packers, actions and settings. The plugins guide has a small sample."],
        ],
      },
    ],
  },

  footer: {
    tagline: "A dual-pane file manager for Windows, Linux and macOS.",
    product: "Product",
    guides: "Guides",
    project: "Project",
    guideLinks: [["getting-started", "Getting started"], ["keyboard-shortcuts", "Keyboard shortcuts"], ["tools", "Tools"], ["connections", "Connections"], ["plugins", "Plugins"], ["configuration", "Configuration"]],
    source: "Source code",
    releases: "Releases",
    issues: "Report a problem",
  },

  notFound: {
    title: "This page is not here",
    body: "The address may be old, or there may be a typo in it.",
    back: "To the home page",
  },

  root: {
    title: "NextCommander",
    choose: "Choose a language",
  },
};
