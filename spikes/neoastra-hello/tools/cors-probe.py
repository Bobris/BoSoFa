#!/usr/bin/env python3
"""A stand-in for a CORS-enabled BoSoFa server (spike 3): what does the WKWebView page on app://bosofa send?

GET /set-cookie sets three HttpOnly cookies (SameSite Lax, None+Secure, unspecified); GET /echo answers the Origin,
Cookie and Authorization headers it received. Every response allows the request's Origin with credentials.
Run: python3 tools/cors-probe.py  (listens on 127.0.0.1:5099)
"""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def cors(self):
        origin = self.headers.get("Origin")
        if origin:
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Access-Control-Allow-Credentials", "true")
            self.send_header("Vary", "Origin")

    def do_OPTIONS(self):
        print(f"PREFLIGHT {self.path} origin={self.headers.get('Origin')} "
              f"method={self.headers.get('Access-Control-Request-Method')} "
              f"headers={self.headers.get('Access-Control-Request-Headers')}", flush=True)
        self.send_response(204)
        self.cors()
        self.send_header("Access-Control-Allow-Methods", "GET, POST")
        self.send_header("Access-Control-Allow-Headers", "authorization, content-type")
        self.send_header("Access-Control-Max-Age", "600")
        self.end_headers()

    def do_GET(self):
        seen = {k: self.headers.get(k) for k in ("Origin", "Cookie", "Authorization", "Sec-Fetch-Site")}
        print(f"GET {self.path} {seen}", flush=True)
        body = json.dumps(seen).encode()
        self.send_response(200)
        self.cors()
        if self.path == "/set-cookie":
            self.send_header("Set-Cookie", "lax=1; Path=/; HttpOnly; SameSite=Lax")
            self.send_header("Set-Cookie", "none=1; Path=/; HttpOnly; SameSite=None; Secure")
            self.send_header("Set-Cookie", "plain=1; Path=/; HttpOnly")
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


ThreadingHTTPServer(("127.0.0.1", 5099), Handler).serve_forever()
