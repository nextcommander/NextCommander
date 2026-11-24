namespace NextCommander.App.Services;

/// <summary>The examples a new script starts with (Commands > Scripts > New script / New column script).</summary>
public static class ScriptTemplates
{
    /// <summary>A command script: what it can use, and a small example that works as it is.</summary>
    public const string Command = """
        // A NextCommander script. Run it from Commands > Scripts, or as a command: nc_RunScript script.js words to pass
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
