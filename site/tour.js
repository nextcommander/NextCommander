// The feature tour: a file list with a cursor on the left, the line under the cursor shown on the right.
(function () {
  const features = [
    { id: "panels", name: "Two panels and tabs", key: "Tab", shot: "main-light.png",
      body: "Two file lists side by side, each with folder tabs. Copy, move and compare between them with one key; split a side into two panels, up to four." },
    { id: "quickview", name: "Quick View", key: "Ctrl+Q", shot: "quick-view-right.png",
      body: "The file under the cursor shown next to the list: pictures, documents, PDF pages, code with syntax colors, as you move." },
    { id: "palette", name: "Command palette", key: "Ctrl+Shift+P", shot: "command-palette.png",
      body: "Any command, folder, Hotlist entry or saved connection, found by typing a few letters." },
    { id: "lister", name: "Viewer", key: "F3", shot: "lister.png",
      body: "Text with syntax colors, hex, pictures with camera RAW, audio and video, Markdown, Office documents without Office." },
    { id: "metadata", name: "Picture metadata", key: "F3, 6", shot: "picture-metadata.png",
      body: "Camera, lens, exposure, GPS position with a map link, IPTC and XMP, and every tag of a photo." },
    { id: "archives", name: "Archives", key: "Enter", shot: "archive.png",
      body: "zip, 7z, rar, tar, iso, virtual machine disks, .deb, .rpm and .msi packages open like folders." },
    { id: "find", name: "Find files", key: "Alt+F7", shot: "find-files.png",
      body: "By name, content, size, date and attributes, also inside Office files. A saved search opens like a folder of its results." },
    { id: "rename", name: "Multi-Rename Tool", key: "Ctrl+M", shot: "multi-rename.png",
      body: "Rename many files at once with patterns, counters, search and replace, and a preview of every new name." },
    { id: "compare", name: "Compare and merge", where: "Files menu", shot: "compare.png",
      body: "Two files side by side, edited in place; copy differences across, merge three files, make and apply patches." },
    { id: "sync", name: "Synchronize directories", where: "Tools menu", shot: "synchronize.png",
      body: "Compare two folders by date, size or content and make them equal in one direction or both." },
    { id: "sizes", name: "Folder sizes", where: "Tools menu", shot: "disk-usage.png",
      body: "Where the space went: a sortable tree, a treemap and the file types, with delete right there." },
    { id: "connections", name: "Connection manager", key: "Ctrl+F", shot: "connections.png",
      body: "Saved servers, clouds and sessions in folders, with the options of each type and a log per connection." },
    { id: "permissions", name: "Permissions", where: "Files menu", shot: "permissions.png",
      body: "Unix mode, owner and group, also over SFTP, and Windows access lists in one dialog." },
    { id: "config", name: "Configuration", where: "Configuration", shot: "configuration.png",
      body: "Every option in one window, with settings backups you can restore part by part." },
  ];

  const list = document.getElementById("features");
  const img = document.getElementById("preview-img");
  const title = document.getElementById("preview-title");
  const body = document.getElementById("preview-body");
  let current = 0;

  features.forEach((f, i) => {
    const row = document.createElement("div");
    row.className = "row";
    row.id = "f-" + f.id;
    row.setAttribute("role", "option");
    row.innerHTML = f.key ? "<span></span><kbd></kbd>" : "<span></span><small></small>";
    row.firstChild.textContent = f.name;
    row.lastChild.textContent = f.key || f.where;
    row.addEventListener("click", () => { select(i); list.focus({ preventScroll: true }); });
    list.appendChild(row);
    new Image().src = "screenshots/" + f.shot; // ready before the cursor gets there
  });

  function select(i) {
    current = Math.max(0, Math.min(features.length - 1, i)); // stops at the ends, like the file list

    const f = features[current];
    list.querySelectorAll(".row").forEach((r, n) => r.setAttribute("aria-selected", String(n === current)));
    list.setAttribute("aria-activedescendant", "f-" + f.id);
    img.src = "screenshots/" + f.shot;
    img.alt = f.name;
    title.textContent = f.name;
    body.textContent = f.body;
    const row = list.children[current + 1];
    if (row.offsetTop < list.scrollTop || row.offsetTop + row.offsetHeight > list.scrollTop + list.clientHeight) {
      row.scrollIntoView({ block: "nearest" });
    }
  }

  list.addEventListener("keydown", (e) => {
    const moves = { ArrowDown: 1, ArrowUp: -1, Home: -current, End: features.length - 1 - current };
    if (e.key in moves) {
      e.preventDefault();
      select(current + moves[e.key]);
    }
  });

  select(0);
})();
