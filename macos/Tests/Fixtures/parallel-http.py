"""Loopback-only adversarial range server; no external network or user files."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import json
import re
import socket
import sys
import threading
import time

SIZE = 12 * 1024 * 1024 + 3
DATA = (bytes(range(251)) * (SIZE // 251 + 1))[:SIZE]
CHANGED = bytes((value + 1) % 256 for value in DATA)
lock = threading.Lock()
requests = []
attempts = {}


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def do_GET(self):
        if self.path == '/stats':
            with lock:
                content = json.dumps(requests).encode()
            self.send_response(200)
            self.send_header('Content-Length', str(len(content)))
            self.end_headers()
            self.wfile.write(content)
            return
        header = self.headers.get('Range')
        parsed = re.fullmatch(r'bytes=(\d+)-(\d*)', header or '')
        begin = int(parsed[1]) if parsed else 0
        end = int(parsed[2]) if parsed and parsed[2] else SIZE - 1
        probe = header == 'bytes=0-0'
        changed = self.path.startswith('/changed/') and not probe
        content = CHANGED if changed else DATA
        ranged = parsed is not None and not changed
        if ranged and not probe and self.headers.get('If-Range') != '"v1"':
            ranged = False
        if not ranged:
            begin, end = 0, SIZE - 1
        status = 206 if ranged else 200
        key = (self.path, header)
        with lock:
            attempts[key] = attempts.get(key, 0) + 1
            first = attempts[key] == 1
            requests.append({'path': self.path, 'range': header, 'validator': self.headers.get('If-Range'),
                             'status': status, 'begin': begin, 'end': end, 'time': time.monotonic()})
        self.send_response(status)
        self.send_header('Content-Length', str(end - begin + 1))
        self.send_header('ETag', '"v2"' if changed else '"v1"')
        if ranged:
            self.send_header('Content-Range', f'bytes {begin}-{end}/{SIZE}')
        self.end_headers()
        cut = self.path.startswith('/interrupted/') and not probe and first and begin % (SIZE // 4) == 0
        limit = min(end + 1, begin + 1024 * 1024) if cut else end + 1
        try:
            for offset in range(begin, limit, 32768):
                self.wfile.write(content[offset:min(offset + 32768, limit)])
                self.wfile.flush()
                if not probe:
                    time.sleep(0.003)
            if cut:
                self.connection.shutdown(socket.SHUT_RDWR)
                self.connection.close()
                self.close_connection = True
        except (BrokenPipeError, ConnectionResetError, OSError):
            self.close_connection = True

    def log_message(self, *_):
        pass


server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
Path(sys.argv[1]).write_text(str(server.server_port))
server.serve_forever()
