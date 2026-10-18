"""Fake HDHomeRun with ONE upstream connection (like a 1-connection IPTV account behind Dispatcharr).

A second concurrent stream gets HTTP 503. Every OPEN / CLOSE / REJECT is appended to the log file.
Set FAKE_MODEL=HDTC-2US to emulate a model that offers several stream variants.
Usage: python3 fakehdhr.py <stream.ts> <log file>
"""
import json, sys, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
PORT = 5004
import os
MODEL = os.environ.get("FAKE_MODEL", "HDHR5-4US")
BASE = f"http://127.0.0.1:{PORT}"
DATA = open(sys.argv[1], "rb").read()
lock = threading.Lock()
active = {}   # connection id -> channel
log = open(sys.argv[2], "a", buffering=1)
def L(msg): log.write(f"{time.strftime('%H:%M:%S')} {msg}\n")
class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def j(self, obj):
        b = json.dumps(obj).encode(); self.send_response(200)
        self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        p = self.path.split("?")[0]
        if p == "/discover.json":
            return self.j({"FriendlyName": "FakeHDHR", "ModelNumber": MODEL, "FirmwareName": "hdhomerun5_atsc", "FirmwareVersion": "20200101",
                           "DeviceID": "1234ABCD", "DeviceAuth": "x", "BaseURL": BASE, "LineupURL": BASE + "/lineup.json", "TunerCount": 1})
        if p == "/lineup.json":
            return self.j([{"GuideNumber": "1", "GuideName": "Chan A", "URL": BASE + "/auto/v1"},
                           {"GuideNumber": "2", "GuideName": "Chan B", "URL": BASE + "/auto/v2"}])
        if p == "/lineup_status.json":
            return self.j({"ScanInProgress": 0, "ScanPossible": 1, "Source": "Cable", "SourceList": ["Cable"]})
        if p.startswith("/auto/v"):
            ch = p[len("/auto/v"):]
            me = object()
            with lock:
                if active:
                    L(f"REJECT ch{ch} (busy: {sorted(active.values())})")
                    self.send_response(503); self.end_headers(); return
                active[me] = ch
            L(f"OPEN ch{ch}")
            try:
                self.send_response(200); self.send_header("Content-Type", "video/mp2t"); self.end_headers()
                chunk = 188 * 70
                while True:
                    for i in range(0, len(DATA), chunk):
                        self.wfile.write(DATA[i:i+chunk]); time.sleep(0.01)
            except Exception:
                pass
            finally:
                with lock: active.pop(me, None)
                L(f"CLOSE ch{ch}")
            return
        self.send_response(404); self.end_headers()
ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
