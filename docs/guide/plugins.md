# Plugins

[Back to the README](../../README.md)

A plugin is a .NET assembly that NextCommander loads at start. It can add:

| What | Interface | Where it shows up |
|---|---|---|
| A file system | `IFileSystemProvider` | Paths with the plugin's scheme (`hello://host/folder`) open in a panel; copy, move, delete, view and edit work across it and every other file system. |
| A place to start | `IPluginEntryPoint` (on the file system) | An entry in the **Plugins** menu of the button bar. |
| A place for the current folder | `IPluginFolderEntry` (on the file system) | An entry at the top of the **Plugins** menu while the active panel's folder fits (e.g. *Git history* in a repository). |
| A way back up | `IOriginProvider` (on the file system) | ".." from the file system's root goes back to the folder it came from. |
| An archive format to read | `IContainerProvider` (on the file system) | Enter / Ctrl+PgDn on such a file opens it like a folder. |
| Columns | `IColumnProvider` | *Show > Display > Custom view modes > Edit view modes...*: the plugin's fields can be added to a view mode. |
| An archive format to write | `IPackerPlugin` | A packer in the Pack dialog (Alt+F5). |
| A viewer | `IViewerPlugin` | The Lister (F3) shows files it wants (key 6). |

The rclone, Git and SQLite support of NextCommander are themselves plugins
([src/Plugins/NextCommander.Plugin.Rclone](../../src/Plugins/NextCommander.Plugin.Rclone),
[src/Plugins/NextCommander.Plugin.Git](../../src/Plugins/NextCommander.Plugin.Git),
[src/Plugins/NextCommander.Plugin.Sqlite](../../src/Plugins/NextCommander.Plugin.Sqlite); see [Git](#git) and
[SQLite](#sqlite) below), and [tests/SamplePlugin](../../tests/SamplePlugin) is a small example with a column and a
`hello://` file system.

- [Git](#git)
- [SQLite](#sqlite)
- [Using plugins](#using-plugins)
- [Writing a plugin](#writing-a-plugin)
- [File systems](#file-systems)
- [Places in the Plugins menu](#places-in-the-plugins-menu)
- [Columns](#columns)
- [Packers and viewers](#packers-and-viewers)
- [Translations](#translations)
- [Testing and finding problems](#testing-and-finding-problems)
- [Shipping a plugin with NextCommander](#shipping-a-plugin-with-nextcommander)

## Git

The Git plugin comes with NextCommander and uses the installed `git` (git-scm.com; or `FM_GIT` with its path).

- **Git history:** while the active panel is in a folder of a repository, the **Plugins** menu offers *Git history*
  at its top. It opens the repository's history as read-only folders: **branches**, **tags** and **commits** (the
  newest 300 of the current branch, named `2026-11-21 23.40 1a2b3c4 subject`). Each holds the files as they are there:
  view an old version with F3, compare it with today's file, or copy it out. A slash in a branch name is shown as `⁄`
  (`feature⁄login`), so the branch stays one folder. ".." from the history's root goes back to the repository.
- **Columns:** *Git status* (Changed, Changed (staged), Added, Deleted, Renamed, Untracked, Ignored, Conflict;
  a folder: *Changes inside* or *Ignored*), *Last commit* (the subject of the last commit that changed the file or
  folder), *Commit author*, *Commit date* and *Commit*. Add them to a view mode (*Show > Display > Custom view modes >
  Edit view modes...*). They are filled in the background: one `git status` per repository every few seconds and one
  `git log` per folder.

## SQLite

The SQLite plugin comes with NextCommander and uses the system's SQLite library: `winsqlite3.dll` on Windows,
`libsqlite3.so.0` on Linux (package *libsqlite3-0* / *sqlite-libs*, nearly always there), `libsqlite3.dylib` on macOS;
`FM_SQLITE` names another library. Nothing is installed with it.

- **A database as a folder:** Enter opens a `.sqlite`, `.sqlite3`, `.db3` or `.sdb` file, and a `.db` file when it is
  a SQLite database (Windows' `Thumbs.db` is not); Ctrl+PgDn opens any file that is one. The database shows a folder
  per table and view and `schema.sql` (the statements that make the whole database). A table's folder holds
  `<table>.csv` (UTF-8, a header line; Excel opens it), `<table>.json` (an array of objects; BLOBs as base64) and its
  own `schema.sql` with its indexes and triggers. Copying a CSV or JSON out exports the table; F3 views it. A slash in
  a table's name is shown as `⁄`. ".." from the root goes back to the database file.
- **Read-only:** the database is never changed.
- **Lister (F3) on a database file:** an overview: the number of tables, views and indexes, then each table with its
  row count, its columns (type, primary key, NOT NULL, default) and its first 10 rows, and the indexes.

## Using plugins

### Installing

A plugin is a folder with a `plugin.json` and the plugin's assembly. Put the folder into one of the two plugin
folders and start NextCommander again:

| Folder | For |
|---|---|
| `plugins` in the [settings folder](settings-folder.md) (`%APPDATA%\NextCommander\plugins`, `~/.config/NextCommander/plugins`, `~/Library/Application Support/NextCommander/plugins`) | Your own plugins. They survive updates of the program. |
| `plugins` next to the program | The plugins that come with NextCommander (rclone). A setup or update replaces this folder. |

```text
plugins/
  hello/
    plugin.json
    Hello.Plugin.dll
    Hello.Plugin.deps.json      (when the plugin has dependencies)
    SomeLibrary.dll             (the plugin's own dependencies)
```

Every subfolder with a `plugin.json` is one plugin; other folders are ignored.

### Turning plugins on and off

*Configuration > Options > Operation > Plugins* lists every installed plugin with a check box. Unchecked plugins are
not loaded; the change takes effect after a restart. A plugin that is not installed any more stays in the list,
unchecked, until you check it again (the list is the `DisabledPlugins` setting).

### The Plugins menu

The **Plugins** button of the button bar (next to the connection manager) lists the places the plugins offer, such
as *rclone > rclone remotes*, and ends with **Plugins...**, the plugins window. The button shows only while
at least one plugin is loaded; *Configuration > Options > Layout > Button bars > Plugins button* hides it for good. The command
is `nc_PluginsMenu` (for a shortcut or the command line); `nc_Plugins` opens the plugins window.

### The plugins window

**Plugins...** lists every installed plugin: its name, version and description, its state (*Enabled*, *Disabled*,
*Error*, or *On / Off after a restart* when it was changed since the start), what it adds (file systems, column
providers, packers, viewers) and its folder.
- **Search** (Ctrl+F) by name, kind or folder; the chips **All**, **File systems**, **Column providers** and
  **Disabled** filter the list and show how many plugins each has.
- On a plugin: **copy its path**, **open its folder** in the panel, or **turn it on / off** (the same as
  *Configuration > Plugins*). A plugin turned off whose folder is gone can be removed from the list.
- **+** installs a plugin from a zip file (with its `plugin.json` at the top or in one folder) or from a
  `plugin.json` file: it is copied to the plugins folder of the settings folder, under its name; an older copy there is
  replaced (turn a loaded plugin off and restart before replacing it, its files are in use).
- Plugins are loaded at the start: after a change the window offers **Restart now**.
- **Open** (Enter, or a double click) opens the plugin's place when it has one (rclone remotes ...), otherwise its folder.

### When a plugin does not load

The plugins window marks such a plugin as *Error*; the reason is in the tool tip of the state, for example:

| Message | Meaning |
|---|---|
| `plugin.json: "Assembly" is missing.` | The manifest names no assembly. |
| `Assembly not found: ...` | The file named in `Assembly` is not in the folder (or points outside it, which is not allowed). |
| `No file system, column, packer or viewer found in the plugin.` | The assembly has no public class with a parameterless constructor implementing one of the interfaces. |
| A type load or missing method error | The plugin was built against another version of NextCommander. Get a version built for yours. |

The program log (`logs/nextcommander-<date>.log` in the settings folder) has a line for every plugin: what it added,
or why it was not loaded.

### Safety

A plugin runs inside NextCommander with your rights: it can read and change your files and use the network. Install
plugins only from people you trust. Plugins are not part of an exported configuration (*Export settings*); copy the
`plugins` folder yourself.

## Writing a plugin

### What you need

- The .NET SDK NextCommander is built with (see `global.json`; .NET 10).
- The NextCommander source (this repository): a plugin is compiled against `NextCommander.Core`. The published
  program is a single file, so it has no `NextCommander.Core.dll` to reference.

### The project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- writes Hello.Plugin.deps.json, which the loader uses to find the plugin's own dependencies -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- compiled against NextCommander.Core, but the application's copy is used at run time: do not copy it -->
    <ProjectReference Include="..\NextCommander\src\NextCommander.Core\NextCommander.Core.csproj" Private="false" />
  </ItemGroup>
</Project>
```

`Private="false"` keeps `NextCommander.Core.dll` out of the plugin's output. Your own NuGet packages are copied next
to the plugin and loaded from its folder. Packages of `NextCommander.Core` itself (such as `Blake3.dll`) may appear in
the output too; the program's copies are used, so they can be left out.

### plugin.json

```json
{
  "Name": "hello",
  "Assembly": "Hello.Plugin.dll",
  "Description": "Two read-only files at hello://",
  "Version": "1.0"
}
```

| Field | |
|---|---|
| `Name` | The plugin's name in the Plugins list and in *Configuration > Plugins* (turning it off is stored by this name). Without it the folder name is used. |
| `Assembly` | The main assembly, relative to the folder. Required. |
| `Description` | Optional. |
| `Version` | Optional; shown after the name in the Plugins list. |

### How it is loaded

1. At start NextCommander reads every `plugin.json` in the two plugin folders (settings folder first) and skips the
   plugins turned off in *Configuration > Plugins*.
2. The main assembly is loaded into its own load context. Its dependencies come from the plugin's folder (through its
   `.deps.json`, native libraries too); `NextCommander.Core`, `System.*` and every assembly the program has already
   loaded (Avalonia, for example) are shared with the program. That is why the plugin's types implement the program's
   interfaces, and why a plugin cannot bring its own copy of them.
3. Every **public, non-abstract class with a parameterless constructor** that implements `IFileSystemProvider`,
   `IColumnProvider`, `IPackerPlugin` or `IViewerPlugin` is created once. One class can implement several of them.
4. File systems join the program's file systems, columns the column list, packers the Pack dialog, viewers the
   Lister.
5. When the program ends, file systems that implement `IDisposable` are disposed (stop your background processes and
   connections there).

The constructor runs at start, on the way to the main window: keep it fast. Look for programs, read configuration
files and connect later, when the first call comes.

## File systems

A file system has a **scheme** (`hello`) and handles the paths with it. Paths are `VfsPath` values:
`VfsPath.Create("hello", "host", "/folder/file.txt")` is shown as `hello://host/folder/file.txt`; `Authority` is the
part after `//` (a server, an account, a device; empty is fine) and `Path` always starts with `/`.

```csharp
using System.Text;
using NextCommander.Core.Vfs;

namespace Hello.Plugin;

/// <summary>hello:/// with two read-only files.</summary>
public sealed class HelloFileSystem : IFileSystemProvider
{
    private static readonly string[] s_names = ["a.txt", "b.txt"];

    public string Scheme => "hello";

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        directory.Path == "/"
            ? Task.FromResult<IReadOnlyList<VfsEntry>>([.. s_names.Select(n => Entry(directory, n))])
            : throw new DirectoryNotFoundException(directory.ToString());

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult(path.Path == "/" ? new VfsEntry { Name = "hello", Path = path, IsDirectory = true }
            : s_names.Contains(path.Name) ? Entry(path.Parent!, path.Name) : null);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("hello from " + path.Name)));

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;

    private static VfsEntry Entry(VfsPath folder, string name) => new()
    {
        Name = name,
        Path = folder.Combine(name),
        Size = Encoding.UTF8.GetByteCount("hello from " + name),
        LastWriteTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local),
    };
}
```

Type `hello:///` in the path line (or open it from a [place in the Plugins menu](#places-in-the-plugins-menu)) to see
it in a panel.

### The members

| Member | What NextCommander expects |
|---|---|
| `Scheme` | Lower case, unique. A scheme the program already handles (`file`, `archive`, `sftp`, `ftp`, `s3`, `rclone` ...) never reaches the plugin: built-in file systems come first. |
| `Capabilities` | What the file system can do: `Read`, `Write` (new files and folders, delete), `Rename` (`MoveAsync` inside the file system), `SetTimes`, `Watch`, `Links`, `Trash`. Commands that need a missing capability are not offered or report that the file system cannot do it. |
| `CanHandle` | Default: the scheme matches. Override only to handle part of a scheme. |
| `ListAsync` | The entries of a folder, without `.` and `..`. Throw `DirectoryNotFoundException` for a folder that does not exist. |
| `GetEntryAsync` | One entry, or **null** when it does not exist (not an exception). For the root (`/`) return a folder entry. |
| `OpenReadAsync` | A readable stream. It does not need to seek; the Lister and copies read it from the start. |
| `OpenWriteAsync` | A writable stream for a new or replaced file. When the target needs the length first (an HTTP PUT, a device), return an `UploadOnCloseStream`: it collects the data in a temporary file and uploads it when the stream is closed. With `overwrite: false` and an existing file, throw `IOException`. |
| `CreateDirectoryAsync` | Create the folder and its missing parents. |
| `DeleteAsync` | A file or empty folder; with `recursive: true` a whole folder tree. |
| `MoveAsync` | Rename or move inside the file system. Throw `CrossDeviceMoveException` when it cannot be done without copying the data (another account, another drive): the program then copies and deletes. |
| `SetTimesAsync` | Set the times after a copy; do nothing when the file system cannot (and leave `SetTimes` out of `Capabilities`). |
| `Watch` | Optional: call `onChanged` when the folder changes, and stop when the returned object is disposed. The panel then refreshes by itself. Return null (the default) when you cannot. |

`VfsEntry` holds `Name`, `Path`, `IsDirectory`, `Size`, `LastWriteTime` (local time), optional `CreationTime`,
`LastAccessTime`, `Attributes` (hidden, read-only ...), `UnixMode`, `IsLink` and `LinkTarget`.

### Errors

The program shows the exception's `Message` to the user, so write messages a user can understand. Use these types:

| Exception | When | What the program does |
|---|---|---|
| `IOException` | Anything that went wrong: the server cannot be reached, the service refused, the data is damaged. | Opening a folder: a message, the panel stays where it was. In a copy or delete: the error question (retry / skip / cancel) with the message. |
| `DirectoryNotFoundException`, `FileNotFoundException` | The folder or file does not exist. | As above. |
| `UnauthorizedAccessException` | No permission for this folder or file. | A folder of a reachable server that may not be read shows `<Read error!>`; in an operation: the error question. |
| `NotSupportedException` | The file system cannot do this at all. | A message. |
| `CrossDeviceMoveException` | `MoveAsync` across accounts or drives. | Copy and delete instead. |
| `OperationCanceledException` | The user cancelled. | Nothing: honour `cancellationToken` in long calls. |

Turn the errors of the libraries you use (`HttpRequestException`, `SocketException`, JSON errors, the errors of an
SDK) into one of these, usually `IOException` with the original as the inner exception. Other exception types are not
expected from a file system and may end up as an unexpected error. The rclone plugin's `ErrorOf` is an example: it
maps the API's answers to `FileNotFoundException`, `UnauthorizedAccessException` or `IOException`.

### Threads

Every method can be called from a background thread, and several at the same time (a copy reads several files, the
panel lists while a column provider reads). Do not touch the user interface from a file system, and protect your
own shared state (a connection, a cache) with a lock or a `SemaphoreSlim`.

### Optional interfaces

| Interface | Use |
|---|---|
| `IUploadTimeSupport` | The service can only take the modification time with the upload: `PrepareUpload(path, time)` is called before `OpenWriteAsync`. |
| `IVolumeSpaceProvider` | `GetSpaceAsync(path)`: size and free space, shown in the panel's footer like a drive's. |
| `ILinkSupport`, `IAttributeSupport` | Creating symbolic links; setting attributes and Unix permissions. |
| `IContainerProvider` | Files that open like folders (an archive format): `CanOpen(file, force)` decides, `GetRoot(file)` returns the path of the archive's root folder in your scheme. `force` is true for Ctrl+PgDn (formats only opened on request). |
| `IFlushableProvider` | Changes kept in memory and written later: `FlushAsync` is called after each operation and when the program ends. |
| `IDisposable` | Called when the program ends. |

## Places in the Plugins menu

A file system can offer a place to start by also implementing `IPluginEntryPoint`
(`NextCommander.Core.Plugins`). It appears in the Plugins menu under its group:

```csharp
public sealed class HelloFileSystem : IFileSystemProvider, IPluginEntryPoint
{
    // ... the file system ...

    public string EntryName => "Hello files";

    public string EntryGroup => "hello";

    public VfsPath EntryRoot => VfsPath.Create("hello", string.Empty, "/");

    // null: the place can be opened; otherwise the message shown instead (a missing program, no configuration ...)
    public string? Unavailable => null;
}
```

Choosing the entry opens `EntryRoot` in the active panel, or shows `Unavailable` when it is not null. The rclone
plugin uses it to explain how to install rclone when it is missing.

A place that depends on the folder of the active panel implements `IPluginFolderEntry` instead: `RootFor(folder)`
returns the folder to open, or null when the entry does not fit. It is called each time the menu opens, so keep it
cheap (the Git plugin looks for a `.git` folder above). Such entries come first in the Plugins menu.

```csharp
public string EntryName => "Git history";

public string EntryGroup => "Git";

public VfsPath? RootFor(VfsPath folder) =>
    folder.IsLocal && RepositoryOf(folder.Path) is { } repository ? RootOf(repository) : null;
```

With `IOriginProvider`, ".." from a root of the file system goes back where it came from: `OriginOf(root)` returns
that folder and the name to put the cursor on (or null).

## Columns

A column provider offers fields that the user can add to a view mode (*Show > Display > Custom view modes > Edit view modes...*).

```csharp
using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

public sealed class ReversedNameColumns : IColumnProvider
{
    public string Id => "sample";

    public string DisplayName => "Sample plugin";

    // field ids are "<provider id>.<field>"
    public IReadOnlyList<ColumnField> Fields { get; } = [new("sample.reversed", "Reversed name")];

    public bool Supports(VfsEntry entry) => !entry.IsDirectory;

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds,
        ProviderRegistry registry, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["sample.reversed"] = new string(entry.Name.Reverse().ToArray()) });
}
```

- `Supports` is called for every row: keep it cheap (the extension, folder or not). `GetValuesAsync` runs in the
  background; return null for a value that is not available.
- `ColumnKind` (`Text`, `Number`, `Size`, `Date`) of a field tells what kind of value it is (a size is shown in the
  size format, a date in the date format).
- To read the file, use `registry.Resolve(entry.Path).OpenReadAsync(entry.Path, cancellationToken)`: that works on
  every file system, not just the local disk.

## Packers and viewers

### Packers (Alt+F5)

```csharp
public sealed class CpioPacker : IPackerPlugin
{
    public string FormatName => "cpio";

    public string Extension => "cpio";

    public bool SupportsPassword => false;

    public async Task PackAsync(IReadOnlyList<PackSource> sources, Stream output, string? password,
        IProgress<long> progress, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            // source.Name uses '/' and is relative to the archive root; folders have IsDirectory
            if (!source.IsDirectory)
            {
                await using var data = await source.OpenAsync(cancellationToken);
                // ... write a header and copy data to output, reporting the bytes read
            }
        }
    }
}
```

The format appears among the packers of the Pack dialog. Reading such archives is a separate job: an
`IContainerProvider` file system in the same plugin.

### Viewers (Lister, F3)

```csharp
public sealed class LogViewer : IViewerPlugin
{
    public string Name => "Log viewer";

    // the file name and its first bytes (at most 4 KB)
    public bool CanView(string fileName, ReadOnlySpan<byte> header) => fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new ViewerContent { Html = $"<pre>{System.Net.WebUtility.HtmlEncode(text)}</pre>", Status = "Log" };
    }
}
```

`ViewerContent` holds HTML (shown like the document preview), plain `Text`, or an Avalonia `Control` of your own,
plus a `Status` line. `dark` tells whether the program shows a dark theme. A viewer that returns a `Control`
references Avalonia: use the same Avalonia version as NextCommander (`Directory.Packages.props`) and do not copy it
(`Private="false"`); the program's copy is used. The first viewer whose `CanView` says yes gets the file; the Lister
opens it in its plugin mode (key 6).

## Translations

A plugin brings its own translations in a `languages` folder next to its assembly, in the same form as the program's
language files ([Languages](languages.md)): `en.json` maps a key to each English text of the plugin, and every other
language file maps the same keys to its translations.

```text
plugins/hello/
  Hello.Plugin.dll
  plugin.json
  languages/
    en.json     { "Hello.Places": "Hello places", "Hello.NotFound": "Not found: {0}" }
    tr.json     { "Hello.Places": "Hello yerleri", "Hello.NotFound": "Bulunamadı: {0}" }
```

- Write the texts in the code in English, as they are in `en.json`; nothing else is needed in the code. What the
  program shows (menu entries, column names, messages of exceptions) is translated through these files. A text the
  plugin translates itself, e.g. a column's value, uses `NextCommander.Core.Localization.Localizer.T("Text")`.
- `{0}`, `{1}` stand for the parts that change (a name, a number); every translation keeps them.
- When a language is loaded, the files of the plugins that are turned on are joined to the program's. When the program
  has the same English text, its own translation is used, so a text of the plugin that means something else needs
  English of its own (the Git plugin says *Changed*, since *Modified* is the program's date column).
- A plugin without a file for a language shows its texts in English in that language.

## Testing and finding problems

- **Unit tests:** create your classes directly in a test project and call them; the file system interface needs no
  program around it. The program's own tests do the same (`tests/NextCommander.Core.Tests/RcloneTests.cs` runs the
  rclone plugin against a fake API).
- **Loading test:** `PluginLoader.LoadAll([folder])` loads a plugin folder exactly as the program does and fills
  `LoadedPlugin.Error` when something is wrong (`RcloneTests.Loads_as_a_plugin_with_its_entry_point`).
- **In the program:** build the plugin, copy the output and `plugin.json` into `<settings folder>/plugins/<name>`,
  start NextCommander, then check **Plugins...** and the log. A post-build step that copies the files saves time
  (Windows settings folder; on Linux `$(HOME)/.config/NextCommander/plugins/hello`):

  ```xml
  <Target Name="CopyToNextCommander" AfterTargets="Build">
    <ItemGroup>
      <PluginFiles Include="$(OutDir)**\*" />
      <PluginFiles Include="plugin.json" />
    </ItemGroup>
    <Copy SourceFiles="@(PluginFiles)" DestinationFolder="$(APPDATA)\NextCommander\plugins\hello\%(RecursiveDir)" />
  </Target>
  ```

- **Debugging:** start NextCommander from your IDE (or attach the debugger to the running `NextCommander` process);
  breakpoints in the plugin work as usual.
- **Updating a plugin:** the program keeps the plugin's assembly open while it runs. Close NextCommander, replace the
  files, start it again.

## Shipping a plugin with NextCommander

Plugins that come with the program live in `src/Plugins/<Project>` and are built with it:

1. Make the project as above, in `src/Plugins/NextCommander.Plugin.<Name>`, with its `plugin.json` in the project
   folder. Add it to `NextCommander.slnx`.
2. In `src/NextCommander.App/NextCommander.App.csproj`, add it like the rclone plugin:

   ```xml
   <ProjectReference Include="..\Plugins\NextCommander.Plugin.Hello\NextCommander.Plugin.Hello.csproj"
                     ReferenceOutputAssembly="false" OutputItemType="NcShippedPlugin" PluginFolder="hello" />
   ```

   The application does not reference the assembly. The build copies it with the `plugin.json` of the project
   folder to `plugins/hello` next to the program, in the build output and in the publish folder (outside the single
   file), so the zip, the setup and the Linux and macOS packages contain it.
3. Texts the user sees (`EntryName`, `Unavailable`, error messages) are written in English; put their
   translations into the plugin's own `languages` folder (see [Translations](#translations)), with a file for every
   language the program ships (en, tr, de, es, fr, zh). The build copies the folder to `plugins/hello/languages`.
4. Add tests to `tests/NextCommander.Core.Tests` (reference the plugin project there).
