"""Local S3 server for the remote integration tests (moto).

python tests/servers/s3_server.py
Listens on 127.0.0.1:5000 (plain HTTP). Any access key / secret is accepted; buckets live in memory.
"""
import time

from moto.server import ThreadedMotoServer

server = ThreadedMotoServer(ip_address="127.0.0.1", port=5000)
server.start()
print("ready", flush=True)
while True:
    time.sleep(3600)
