"""Local SMB2 server for the remote integration tests (impacket).

python tests/servers/smb_server.py <root folder>
Listens on 127.0.0.1:4445 (port 445 belongs to the system), share "TEST" = the root folder,
user "tester", password "şifre123" (NTLM).
"""
import sys

from impacket import smbserver
from impacket.ntlm import compute_lmhash, compute_nthash

root = sys.argv[1]
password = "şifre123"
server = smbserver.SimpleSMBServer(listenAddress="127.0.0.1", listenPort=4445)
server.addShare("TEST", root, "test share")
server.setSMB2Support(True)
server.addCredential("tester", 1000, compute_lmhash(password).hex(), compute_nthash(password).hex())
server.setLogFile("")
print("ready", flush=True)
server.start()
