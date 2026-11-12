"""Local SFTP server for the remote integration tests (asyncssh).

python tests/servers/sftp_server.py <root folder>
Listens on 127.0.0.1:2222, user "tester", password "şifre123". The host key is created next to this script on the
first start (host_key, not committed).
"""
import asyncio
import os
import sys

import asyncssh

root = sys.argv[1]
key_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "host_key")
if not os.path.exists(key_path):
    asyncssh.generate_private_key("ssh-ed25519").write_private_key(key_path)


class Server(asyncssh.SSHServer):
    def password_auth_supported(self):
        return True

    def validate_password(self, username, password):
        return username == "tester" and password == "şifre123"


class Sftp(asyncssh.SFTPServer):
    def __init__(self, chan):
        super().__init__(chan, chroot=root)


async def main():
    await asyncssh.listen("127.0.0.1", 2222, server_host_keys=[key_path], server_factory=Server, sftp_factory=Sftp)
    print("ready", flush=True)
    await asyncio.Future()


asyncio.run(main())
