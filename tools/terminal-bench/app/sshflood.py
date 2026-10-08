# Flood SSH server for in-app benchmarks: no authentication, the username names a workload
# (<workload dir>/<user>.bin) whose raw bytes are streamed into the shell channel,
# then the channel idles like a quiet shell until the client closes it. No pty emulation or
# line editing: the bytes reach the client exactly as stored (the workloads are CRLF already).
# Its host key is written to the data dir's known_hosts.json so no trust dialog appears.
import asyncio
import base64
import hashlib
import json
import os
import sys

import asyncssh

# python sshflood.py <workload dir> <resesh --data-dir> [state dir]   (pip install asyncssh)
WORKLOADS = sys.argv[1]
DATA_DIR = sys.argv[2]
STATE = sys.argv[3] if len(sys.argv) > 3 else DATA_DIR
PORT = 2223
KEY_PATH = os.path.join(STATE, 'sshflood-hostkey')


class Server(asyncssh.SSHServer):
    def begin_auth(self, username):
        return False  # no authentication required


async def probe(process):
    # Protocol probe: asks XTVERSION, enables focus reporting, reports progress and sends a
    # notification, then logs every byte the terminal sends back to <state>/probe.log.
    log = open(os.path.join(STATE, 'probe.log'), 'ab')
    process.stdout.write(b'probe: XTVERSION, focus 1004, OSC 9;4 progress, OSC 777 notify\r\n')
    process.stdout.write(b'\x1b[>q\x1b[?1004h\x1b]9;4;1;42\x07\x1b]777;notify;Probe;Hello from probe\x07')
    process.stdout.write(b'\x1b]9;plain osc 9 notification\x07')
    await process.stdout.drain()
    try:
        while True:
            data = await process.stdin.read(4096)
            if not data:
                break
            log.write(repr(data).encode() + b'\n')
            log.flush()
    except Exception:
        pass
    log.close()
    process.exit(0)


STYLES = (
    '\x1b[4:1msingle\x1b[0m \x1b[4:2mdouble\x1b[0m \x1b[4:3mcurly\x1b[0m \x1b[4:4mdotted\x1b[0m '
    '\x1b[4:5mdashed\x1b[0m \x1b[4:3;58:2::255:85:85mred curly\x1b[0m \x1b[53moverline\x1b[0m '
    '\x1b[9mstrike\x1b[0m \x1b[2;4mfaint underline\x1b[0m\r\n'
    + ''.join(f'\x1b[3{i}mN{i}\x1b[0m ' for i in range(8)) + '\r\n'
    + ''.join(f'\x1b[1;3{i}mB{i}\x1b[0m ' for i in range(8)) + '\r\n'
    'link: https://example.com/a/very/long/path?q=1 and \x1b]8;;https://ghostty.org\x07OSC 8 text\x1b]8;;\x07\r\n'
)


async def handle(process):
    name = process.get_extra_info('username')
    if name == 'probe':
        await probe(process)
        return
    if name == 'styles':
        process.stdout.write(STYLES.encode())
    path = os.path.join(WORKLOADS, name + '.bin')
    if os.path.exists(path):
        with open(path, 'rb') as f:
            data = f.read()
        for i in range(0, len(data), 32768):
            process.stdout.write(data[i:i + 32768])
            await process.stdout.drain()
    elif name != 'styles':
        process.stdout.write(f'no workload {name}\r\n'.encode())
    # Idle like a quiet shell until the client closes; a resize or break is not the end.
    while True:
        try:
            if not await process.stdin.read(4096):
                break
        except (asyncssh.BreakReceived, asyncssh.TerminalSizeChanged):
            continue
        except Exception:
            break
    process.exit(0)


def trust_host_key(key):
    blob = key.public_data
    # SshTerminalSession: base64 SHA-256 of the host key blob, padding trimmed
    sha = base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip('=')
    path = os.path.join(DATA_DIR, 'known_hosts.json')
    entries = {}
    if os.path.exists(path):
        with open(path, encoding='utf-8-sig') as f:
            entries = json.load(f)
    entries[f'127.0.0.1:{PORT}'] = {'KeyType': key.get_algorithm(), 'Sha256': sha}
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(entries, f, indent=2)
    print('trusted', key.get_algorithm(), sha, flush=True)


async def main():
    if not os.path.exists(KEY_PATH):
        asyncssh.generate_private_key('ssh-ed25519').write_private_key(KEY_PATH)
    key = asyncssh.read_private_key(KEY_PATH)
    trust_host_key(key)
    await asyncssh.create_server(Server, '127.0.0.1', PORT, server_host_keys=[key],
                                 process_factory=handle, encoding=None, line_editor=False)
    print('listening', PORT, flush=True)
    await asyncio.Event().wait()


if __name__ == '__main__':
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        sys.exit(0)
