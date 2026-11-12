"""Local WebDAV server for the remote integration tests (WsgiDAV + cheroot).

python tests/servers/webdav_server.py <root folder>
Listens on 127.0.0.1:8088 (plain HTTP), user "tester", password "şifre123" (Basic authentication).
"""
import sys

from cheroot import wsgi
from wsgidav.wsgidav_app import WsgiDAVApp

root = sys.argv[1]
config = {
    "host": "127.0.0.1",
    "port": 8088,
    "provider_mapping": {"/": root},
    "simple_dc": {"user_mapping": {"*": {"tester": {"password": "şifre123"}}}},
    "http_authenticator": {"accept_basic": True, "accept_digest": False, "default_to_digest": False},
    "mutable_live_props": ["{DAV:}getlastmodified"],  # times can be set (PROPPATCH), as the app does after a copy
    "verbose": 1,
    "logging": {"enable_loggers": []},
}
app = WsgiDAVApp(config)
server = wsgi.Server(("127.0.0.1", 8088), app)
print("ready", flush=True)
server.start()
