namespace NextCommander.App.Services;

/// <summary>The examples a new script starts with (Tools > Scripts > New script / New column script).</summary>
public static class ScriptTemplates
{
    /// <summary>A command script: what it can use, and a small example that works as it is.</summary>
    public const string Command = """
        // A NextCommander script. Run it from Tools > Scripts, or as a command: nc_RunScript script.js words to pass
        // (a button, a key or a user command can run that command line too).
        //
        //   nc.args                  the words after the script's name
        //   nc.run("nc_GoToRoot")    runs commands of the program (several: "nc_A, nc_B param"); true when all are known
        //   nc.panel()               the active panel: { path, cursor, selected, files, folders }
        //   nc.otherPanel()          the other panel, the same way
        //   nc.selected()            full paths of the selected files (or the one under the cursor)
        //   nc.navigate(path)        the active panel goes to a folder; nc.refresh() reads it again
        //   nc.message(text)         a message; nc.confirm(text) -> true / false; nc.prompt(text, value) -> text or null
        //   nc.readText(path)        a file's text; nc.writeText(path, text); nc.appendText(path, text)
        //   nc.exists(path)          a file or folder is there; nc.list(path) -> its names
        //   nc.log(text)             a line in logs/scripts.log of the settings folder

        var files = nc.selected();
        if (files.length === 0) {
          nc.message("Select some files first.");
        } else {
          var list = files.join("\n");
          if (nc.confirm("Write the list of the " + files.length + " selected files to files.txt in the other panel?\n\n" + list)) {
            var target = nc.otherPanel().path;
            nc.writeText(target + "/files.txt", list + "\n");
            nc.log("wrote " + files.length + " names to " + target);
            nc.run("nc_RereadSource");
          }
        }
        """;

    /// <summary>A file system script: a scratch place kept in memory (it shows every function a file system can have).</summary>
    public const string FileSystem = """
        // A file system in JavaScript. It is opened from Connection > Saved connections (group "JavaScript") or the
        // button bar's connections menu; the program reads the scripts of this folder when it starts.
        //
        //   list(path)             the entries of a folder: [{ name, isDir, size, modified }]  (modified: ISO text or ms)
        //   read(path)             a file's content: a text, or { base64: "..." }
        //   write(path, data)      (optional) data.text / data.base64: without it the place is read-only
        //   mkdir(path), remove(path, isDir), rename(from, to)   (optional)
        //
        // Paths are "/" separated and start with "/". The program gives:
        //   http(method, url, body, headers) -> { status, body, headers }   e.g. http("GET", "https://example.com/api")
        //   toBase64(text), fromBase64(base64), readText(localPath, maxChars)
        // Throw an Error to report a problem: its message is shown.

        var title = "Scratch (in memory)";
        var files = { "/": { dir: true }, "/readme.txt": { text: "Files written here stay until the program closes.\n" } };

        function parentOf(path) { return path.substring(0, path.lastIndexOf("/")) || "/"; }

        function list(path) {
          var result = [];
          for (var key in files) {
            if (key !== "/" && parentOf(key) === path) {
              var f = files[key];
              result.push({ name: key.substring(key.lastIndexOf("/") + 1), isDir: !!f.dir, size: f.dir ? 0 : f.text.length, modified: f.modified });
            }
          }
          return result;
        }

        function read(path) {
          if (!files[path] || files[path].dir) throw new Error("No such file: " + path);
          return files[path].text;
        }

        function write(path, data) { files[path] = { text: data.text, modified: new Date().toISOString() }; }

        function mkdir(path) { files[path] = { dir: true }; }

        function remove(path, isDir) {
          for (var key in files) {
            if (key === path || key.indexOf(path + "/") === 0) delete files[key];
          }
        }

        function rename(from, to) {
          for (var key in files) {
            if (key === from || key.indexOf(from + "/") === 0) {
              files[to + key.substring(from.length)] = files[key];
              delete files[key];
            }
          }
        }
        """;

    /// <summary>A column script: the number of lines of text files.</summary>
    public const string Column = """
        // A custom column in JavaScript. Choose it in Show > Display > Edit view modes... (field "js_<file name>.<id>");
        // the program reads the scripts of this folder when it starts.
        //
        //   columns()          the columns: [{ id, name, kind }], kind "text", "number", "size" or "date"
        //   value(file, id)    the value of one column for one file; null: empty
        //   supports(file)     (optional) false: the file gets no values
        //
        // file = { name, ext, path, size, modified, isDir, isLocal }; readText(path, maxChars) reads a local file.

        function columns() {
          return [{ id: "lines", name: "Lines", kind: "number" }];
        }

        function supports(file) {
          return !file.isDir && file.isLocal && ["txt", "md", "cs", "js", "json", "xml", "csv", "log"].indexOf(file.ext.toLowerCase()) >= 0;
        }

        function value(file, id) {
          var text = readText(file.path, 1000000);
          return text === null ? null : text.split("\n").length;
        }
        """;
}
