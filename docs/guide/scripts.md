# Macros and scripts

NextCommander can repeat what you do (macros) and run small programs written in JavaScript (scripts): as commands,
as columns of the file list, or as file systems (places opened in a panel).

## Macros

1. **Tools > Record macro** starts recording. The window title starts with ● while it records.
2. Use the program as usual. Every command run from a menu, a key, a button or the command line is noted.
   Moving the cursor or typing in a dialog is not a command and is not recorded.
3. **Tools > Record macro** again stops. Give the macro a name: it is saved as the user command `uc_<name>`.

Give the macro a key in **Configuration > Commands and shortcuts**, or a button with the button bar's editor. A macro
is a list of commands separated by commas, so a command whose parameter contains a comma cannot be part of it; the
program says how many were left out.

## Scripts as commands

Scripts are the `.js` files of the `scripts` folder in the [settings folder](settings-folder.md).

- **Tools > Scripts** lists them and runs one with a click.
- **New script...** writes an example (it lists what a script can use) and opens it in the editor.
- As a command (for a key, a button or a user command): `nc_RunScript name.js words to pass`. The `.js` can be left out;
  a full path runs a script from another folder.

A script runs in the background: the window stays usable. Errors are shown with their line. `nc.log(...)` writes to
`logs/scripts.log`.

| What | Use |
| --- | --- |
| The words after the script's name | `nc.args` |
| Run commands of the program | `nc.run("nc_GoToRoot")`, several: `nc.run("nc_A, nc_B parameter")`; true when all are known |
| The active / other panel | `nc.panel()`, `nc.otherPanel()`: `{ path, cursor, selected, files, folders }` |
| The selected files (or the one under the cursor) | `nc.selected()`: full paths |
| Go to a folder, read it again | `nc.navigate(path)`, `nc.refresh()` |
| Dialogs | `nc.message(text)`, `nc.confirm(text)` (true / false), `nc.prompt(text, value)` (text or null) |
| Files of this computer | `nc.readText(path)`, `nc.writeText(path, text)`, `nc.appendText(path, text)`, `nc.exists(path)`, `nc.list(path)` |

Example: the names of the selected files to a text file in the other panel.

```js
var files = nc.selected();
if (files.length > 0 && nc.confirm("Write " + files.length + " names?")) {
  nc.writeText(nc.otherPanel().path + "/files.txt", files.join("\n") + "\n");
  nc.run("nc_RereadSource");
}
```

## Scripts as columns

The `.js` files of `scripts/columns` give columns for custom column sets (**Show > Display > Edit view modes...**). The
program reads them when it starts; the field of a column is `js_<file name>.<id>`. **Tools > Scripts > New column
script...** writes an example that counts the lines of text files.

```js
function columns() {                      // the columns: kind "text", "number", "size" or "date"
  return [{ id: "lines", name: "Lines", kind: "number" }];
}
function supports(file) {                 // optional: false = no values for this file
  return !file.isDir && file.isLocal && file.ext === "txt";
}
function value(file, id) {                // file = { name, ext, path, size, modified, isDir, isLocal }
  var text = readText(file.path, 1000000);
  return text === null ? null : text.split("\n").length;
}
```

A column script gets 5 seconds for each value; a value that fails stays empty (the program's log names the script).

## Scripts as file systems

The `.js` files of `scripts/filesystems` are places like a server's: they are listed with the saved connections
(**Connection > Saved connections**, group *JavaScript*) and open in a panel, where copy, move, view and edit work as
everywhere. The program reads them when it starts. **Tools > Scripts > New file system script...** writes an
example that keeps its files in memory and has every function a file system can have.

| Function | What it does |
| --- | --- |
| `list(path)` | The entries of a folder: `[{ name, isDir, size, modified }]` (`modified`: ISO text or milliseconds). Needed. |
| `read(path)` | A file's content: a text, or `{ base64: "..." }`. Needed. |
| `write(path, data)` | Writes a file (`data.text`, `data.base64`). Without it the place is read-only. |
| `mkdir(path)`, `remove(path, isDir)`, `rename(from, to)` | Folders, deleting, renaming (without `rename` a move copies and deletes). |
| `var title = "..."` | The name in the menu (else the file's name). |

Paths are separated by `/` and start with `/`. Throw an `Error` to report a problem: its message is shown. The
program gives these functions to the script:

- `http(method, url, body, headers)` → `{ status, body, headers }` (header names in lower case), for a web service's
  files: `http("GET", "https://example.com/api/files", null, { Authorization: "Bearer ..." })`.
- `toBase64(text)`, `fromBase64(base64)`, `readText(localPath, maxChars)`.

Each call gets 60 seconds; one call of a script runs at a time.

