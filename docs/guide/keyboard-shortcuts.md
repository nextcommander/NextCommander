# Keyboard shortcuts

[Back to the README](../../README.md)

These are the default shortcuts. *Help > Keyboard shortcuts*
shows them inside the program too.

On **macOS**, Cmd works like Ctrl in all of these shortcuts (unless you give Cmd a shortcut of its own).

"Num" means a key on the numeric keypad.

## Panels and navigation

| Key | Command |
|---|---|
| Tab | Switch to the other panel |
| Enter | Open the folder, archive or file under the cursor |
| Backspace, Ctrl+Page Up | Parent folder |
| Ctrl+Backspace | Root folder |
| / | Root of the drive the panel is on (e.g. a disk mounted under `/media`); when `/` has no shortcut of its own and the command line is empty |
| Alt+F1 / Alt+F2 | Left / right drive list |
| Alt+Left / Alt+Right | Back / Forward (also the arrow buttons at the start of the button bar; right click on them lists the folders to jump to). Each tab keeps 20 folders each way, kept on exit; the number is *Configuration > Options > History > Back / forward steps*. |
| Alt+Down | Folder history |
| Alt+Shift+Down | Most frequently used folders |
| Ctrl+D | Hotlist |
| Ctrl+Left / Ctrl+Right | Open folder under cursor in the left / right panel |
| Ctrl+U | Source <-> Target (swap the panels) |
| Alt+D | Edit path |
| Ctrl+Alt+letter | Quick search |
| Ctrl+S | Quick filter |
| Ctrl+Y | Clear quick filter |
| F2, Ctrl+R | Reread source (refresh) |
| Alt+Shift+F11 | Focus button bar |

## Tabs

| Key | Command |
|---|---|
| Ctrl+T | New folder tab |
| Ctrl+Up | Open folder under cursor in new tab |
| Ctrl+Shift+Up | ... open in new tab in other panel |
| Ctrl+W | Close tab |
| Ctrl+Shift+W | Close other tabs |
| Ctrl+Shift+T | Reopen last closed |
| Ctrl+Tab / Ctrl+Shift+Tab | Next tab / Previous tab |
| Ctrl+1 … Ctrl+9 | Go to tab 1 … 9 |
| Ctrl+Shift+A | Tab list... |

## Selection

| Key | Command |
|---|---|
| Insert, Space | Mark the file under the cursor (Space also calculates a folder's size) |
| Shift+Up / Shift+Down | Mark while moving |
| Ctrl+A, Ctrl+Num + | Select all |
| Ctrl+Num - | Unselect all |
| Num + | Select group... |
| Num - | Unselect group... |
| Num * | Invert selection |
| Alt+Num + | Select all with the same extension |
| Num /, Ctrl+Shift+R | Restore selection |
| Shift+F2 | Compare folders |

Select all, Num +, Num - and Num * take files only by default. *Configuration > Options >
Operation > Selection (with Num +)* > *Also select directories* makes them take folders too. A pattern ending in `\`
(for example `src\`) selects folders either way.

## File operations

| Key | Command |
|---|---|
| F3 | View |
| F4 | Edit |
| Shift+F4 | New file |
| F5 | Copy |
| F6 | Move / rename |
| Shift+F6 | Rename in place |
| F7 | New folder |
| Shift+F7 | New folder in target panel |
| F8, Delete | Delete |
| Shift+F8, Shift+Delete | Delete permanently |
| Shift+Enter | Open with... |
| Alt+Enter | Properties... |
| Ctrl+L | Calculate occupied space... |
| Alt+Shift+Enter | Calculate folder sizes |
| Ctrl+Z | Edit comment... |
| Ctrl+M | Multi-Rename Tool... |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy to clipboard / Cut to clipboard / Paste from clipboard |
| Alt+F4 | Exit |

## Archives

| Key | Command |
|---|---|
| Alt+F5 | Pack... |
| Alt+F9 | Unpack files... |
| Alt+Shift+F9 | Test archives |
| Ctrl+Page Down | Open as archive |

## View

| Key | Command |
|---|---|
| Ctrl+F1 | Brief |
| Ctrl+F2 | Full |
| Ctrl+Shift+F2 | Comments |
| Ctrl+Shift+F1 | Thumbnails view |
| Shift+F1 | Custom columns mode |
| Ctrl+F3 | Sort by name |
| Ctrl+F4 | Sort by extension |
| Ctrl+F5 | Sort by time |
| Ctrl+F6 | Sort by size |
| Ctrl+F7 | Unsorted |
| Ctrl+F10 | All files |
| Ctrl+F11 | Programs |
| Ctrl+F12 | Custom file filter... |
| Ctrl+H | Hidden and system files |
| Ctrl+B | Branch View (with subfolders) |
| Ctrl+Shift+B | Branch View of selected folders |
| Ctrl+F8 | Show / hide tree panel |
| Alt+F10 | Folder tree window... |
| Ctrl+Q | Quick view panel |
| Ctrl+Shift+D | Dark theme <-> system colors |

## Search and tools

| Key | Command |
|---|---|
| Alt+F7 | Find files... |
| Alt+Shift+F7 | Find files in separate process... |
| Ctrl+F | Connection manager... |
| Ctrl+N | Connect by address... |
| Ctrl+Shift+F | Disconnect |
| Ctrl+` | Internal terminal |
| Ctrl+Alt+T | Open command prompt window |
| Ctrl+Shift+E | Show in system file manager |
| Ctrl+Alt+S | Options... (the Configuration window) |
| Ctrl+Shift+F12 | Commands and shortcuts... |

## Command line and clipboard text

| Key | Command |
|---|---|
| Ctrl+Enter | Add name to command line |
| Ctrl+Shift+Enter | Add full path to command line |
| Ctrl+P | Add folder path to command line |
| Alt+F8 | Show command line history |
| Ctrl+Alt+C | Copy selected names to clipboard |
| Ctrl+Shift+C | Copy names with paths to the clipboard |
| Ctrl+Alt+D | Copy to clipboard with all details |
| Ctrl+Shift+P | Copy folder path to the clipboard |

## Inside Lister (F3)

| Key | What it does |
|---|---|
| 1 … 7 | Text, binary, hex, image, preview (PDF / Office / HTML / Markdown / RTF), plugin, media |
| W | Word wrap on / off |
| A / S / U / 8 | Show as ANSI / DOS / UTF-16 / UTF-8 text |
| L / R | Rotate an image left / right |
| N / P | Next / previous file of the panel |
| F7 | Find; F3 / Shift+F3 find next / previous |
| F4 | Edit the file |
| F2 | Reload |
| Ctrl+mouse wheel | Font size |
| Esc | Close the find bar, otherwise Lister |

## Inside the editor (F4)

| Key | What it does |
|---|---|
| Ctrl+S | Save |
| Ctrl+F | Find |
| Ctrl+H | Replace |
| Ctrl+G | Go to line |
| Esc | Close the find panel, otherwise the editor (it asks about unsaved changes) |

## Changing shortcuts

Open *Configuration > Commands and shortcuts...* (Ctrl+Shift+F12).

- **On the left**, the categories: *All commands* (grouped by the main menu they are in), *Favorites*,
  *Recently used* (run from this window), *Modified* (shortcuts that differ from the defaults), then one per
  menu; commands in no menu are under *Other*.
- **At the top**, the search: words in any order over names, descriptions and shortcuts (`files hidden`,
  `F5`). **By shortcut** turns the box into a key catcher: press a combination to see which command uses it.
- **In the middle**, the commands with their description, name and shortcuts; a star marks favorites, a dot
  changed shortcuts. ↑ ↓ move, Enter (or a double click) runs the command, F2 adds a shortcut.
- **On the right**, the chosen command: its shortcuts (*Default* or *Custom*) with ✕ to remove one,
  **Add a shortcut** (press the keys; Esc cancels) and **Default** to go back to the original ones. When the
  keys are used by another command, a warning names it: **Use it here instead** moves them. Below are the
  category, where else the command is (button bars, the function key bar), **Run now** and **Add to button
  bar**; the star adds it to the favorites and the copy button copies its name.

Changes are saved at once. If you remove a default shortcut, the key does nothing in the panels any more.

Back to: [Getting started](getting-started.md)
