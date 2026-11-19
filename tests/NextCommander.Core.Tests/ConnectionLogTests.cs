using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Core.Tests;

/// <summary>The log of each connection: one file per connection, its steps and errors with their causes, no secrets.</summary>
[Collection("ConnectionLog")]
public sealed class ConnectionLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nc-connection-log-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = ConnectionLog.Folder;

    public ConnectionLogTests() => ConnectionLog.Folder = _folder;

    public void Dispose()
    {
        ConnectionLog.Folder = _previous;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // another test's connection wrote into it meanwhile
        }
    }

    [Fact]
    public void Each_connection_has_its_own_log_with_its_errors_and_their_causes()
    {
        var web = new RemoteConnection { Name = "Web: prod/1", Host = "web.example.com", User = "deploy", Port = 2222 };
        var other = new RemoteConnection { Host = "db.example.com", User = "admin", Protocol = RemoteProtocol.Ftp };
        var entries = new List<ConnectionLogEntry>();
        void Seen(object? sender, ConnectionLogEntry entry)
        {
            // the event is the whole program's: other tests' connections log at the same time
            if (entry.Connection is "Web: prod/1" or "ftp db.example.com")
            {
                lock (entries)
                {
                    entries.Add(entry);
                }
            }
        }
        ConnectionLog.Written += Seen;
        try
        {
            ConnectionLog.Info(web, "Connecting ...");
            var cause = new System.Net.Sockets.SocketException(11001);
            ConnectionLog.Error(web, "Could not connect", new IOException("Could not connect to web.example.com:2222", cause));
            ConnectionLog.Info(other, "Connected.");
        }
        finally
        {
            ConnectionLog.Written -= Seen;
        }

        // a file per connection, named after it (characters a file name cannot have replaced)
        Assert.Equal(Path.Combine(_folder, "Web_ prod_1.log"), ConnectionLog.FileOf(web));
        Assert.Equal(Path.Combine(_folder, "ftp db.example.com.log"), ConnectionLog.FileOf(other));
        var text = ConnectionLog.Read(web);
        Assert.Contains("INFO  sftp://deploy@web.example.com:2222  Connecting ...", text, StringComparison.Ordinal);
        Assert.Contains("ERROR sftp://deploy@web.example.com:2222  Could not connect", text, StringComparison.Ordinal);
        Assert.Contains("SocketException", text, StringComparison.Ordinal); // the causes under the error
        Assert.DoesNotContain("db.example.com", text, StringComparison.Ordinal);
        Assert.Contains("Connected.", ConnectionLog.Read(other), StringComparison.Ordinal);

        // the entries as they are written: the error with its chain of causes
        Assert.Equal(3, entries.Count);
        var error = entries[1];
        Assert.Equal((ConnectionLogLevel.Error, "Web: prod/1"), (error.Level, error.Connection));
        Assert.StartsWith("Could not connect to web.example.com:2222\n← ", error.Details, StringComparison.Ordinal);

        ConnectionLog.Clear(web);
        Assert.Equal(string.Empty, ConnectionLog.Read(web));
    }

    [Fact]
    public void A_long_log_is_set_aside_and_read_from_its_end()
    {
        var c = new RemoteConnection { Name = "big", Host = "h", User = "u" };
        var line = new string('x', 1000);
        for (var i = 0; i < 1100; i++)
        {
            ConnectionLog.Info(c, line);
        }
        Assert.True(File.Exists(Path.Combine(_folder, "big.old.log")));
        Assert.True(new FileInfo(ConnectionLog.FileOf(c)!).Length < ConnectionLog.MaxBytes);
        var tail = ConnectionLog.Read(c, maxChars: 5000);
        Assert.True(tail.Length <= 5000);
        Assert.Matches(@"^\d{4}-\d\d-\d\d ", tail); // whole lines only
    }

    [Fact]
    public void Causes_are_listed_once_outermost_first()
    {
        var inner = new InvalidOperationException("Authentication failed.");
        var ex = new IOException("deploy@h: login failed (Authentication failed.).", new IOException("Permission denied", inner));
        Assert.Equal("deploy@h: login failed (Authentication failed.).\n← Permission denied (IOException)", ConnectionLog.Causes(ex));
        Assert.Equal("cloud", ConnectionLog.SafeName(new RemoteConnection { Name = "cloud" }.Name));
        Assert.Equal("a_b", ConnectionLog.SafeName("a/b"));
        Assert.Equal("serial://COM3", ConnectionLog.AddressOf(new RemoteConnection { Protocol = RemoteProtocol.Serial, Host = "COM3" }));
    }
}
