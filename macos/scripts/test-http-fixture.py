#!/usr/bin/env python3
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from pathlib import Path
import sys
DATA=bytes(i%251 for i in range(512*1024))
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/module-bytes' and self.headers.get('User-Agent') != 'DropSpace-official-module/1 macOS':
            self.send_response(400); self.send_header('Content-Length','0'); self.end_headers(); return
        if self.path=='/bad-range':
            self.send_response(206);self.send_header('Content-Length','1');self.send_header('Content-Range','bytes 1-1/2');self.end_headers();self.wfile.write(b'x');return
        offset=0
        if self.headers.get('Range') and self.headers.get('If-Range')=='"fixture-v1"':offset=int(self.headers['Range'][6:-1])
        self.send_response(206 if offset else 200)
        self.send_header('Content-Length',str(len(DATA)-offset));self.send_header('ETag','"fixture-v1"')
        if offset:self.send_header('Content-Range',f'bytes {offset}-{len(DATA)-1}/{len(DATA)}')
        self.end_headers();self.wfile.write(DATA[offset:])
    def log_message(self,fmt,*args): print(fmt%args,flush=True)
server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
Path(sys.argv[1]).write_text(str(server.server_port))
server.serve_forever()
