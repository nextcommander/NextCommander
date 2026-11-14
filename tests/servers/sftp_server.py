"""Local SFTP server for the remote integration tests (asyncssh).

python tests/servers/sftp_server.py <root folder> [authorized_keys file] [port]
Listens on 127.0.0.1:2222 (or the port given), user "tester", password "şifre123"; with an authorized_keys file the
keys in it log in too (the SSH agent test). Port forwarding is allowed, so the server is its own jump host (the
jump host test). The host key is created next to this script on the first start (host_key, not committed).
"""
import asyncio
import os
import sys

import asyncssh

root = sys.argv[1]
authorized = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] else None
port = int(sys.argv[3]) if len(sys.argv) > 3 else 2222
key_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "host_key")
if not os.path.exists(key_path):
    asyncssh.generate_private_key("ssh-ed25519").write_private_key(key_path)


class Server(asyncssh.SSHServer):
    def password_auth_supported(self):
        return True

    def validate_password(self, username, password):
        return username == "tester" and password == "şifre123"

    def connection_requested(self, dest_host, dest_port, orig_host, orig_port):
        return True  # local port forwarding: the jump host test


class Sftp(asyncssh.SFTPServer):
    def __init__(self, chan):
        super().__init__(chan, chroot=root)


async def main():
    await asyncssh.listen("127.0.0.1", port, server_host_keys=[key_path], server_factory=Server, sftp_factory=Sftp,
                          authorized_client_keys=authorized)
    print("ready", flush=True)
    await asyncio.Future()


asyncio.run(main())
