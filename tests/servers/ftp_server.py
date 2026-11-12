"""Local FTP server for the remote integration tests (pyftpdlib).

python tests/servers/ftp_server.py <root folder>
Listens on 127.0.0.1:2121, user "tester", password "şifre123" (non-ASCII on purpose: tests UTF-8 logins),
passive ports 30000-30099.
"""
import sys

from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer

root = sys.argv[1]
auth = DummyAuthorizer()
auth.add_user("tester", "şifre123", root, perm="elradfmwMT")
FTPHandler.authorizer = auth
FTPHandler.encoding = "utf-8"
FTPHandler.passive_ports = range(30000, 30100)
server = FTPServer(("127.0.0.1", 2121), FTPHandler)
print("ready", flush=True)
server.serve_forever()
