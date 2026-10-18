"""Runs the manual test plan automatically against the server prepared by setup.py.

Usage: python3 scenarios.py <state.json> <fake tuner log>   (needs: pip install websocket-client)
"""
import json, time, threading, urllib.request, urllib.error, sys
import websocket
B = "http://127.0.0.1:8096"
ST = json.load(open(sys.argv[1]))
HDHR_LOG = sys.argv[2]
PLUGIN = "443cd6fe-a768-4ae2-a39e-e87e0e8e359d"
A, Bc = ST["channels"]["Chan A"], ST["channels"]["Chan B"]
RESULTS = []

def http(method, path, body=None, token=None, dev="x"):
    h = {"Content-Type": "application/json",
         "Authorization": f'MediaBrowser Client="e2e", Device="{dev}", DeviceId="{dev}", Version="1.0"' + (f', Token="{token}"' if token else "")}
    r = urllib.request.Request(B + path, data=json.dumps(body).encode() if body is not None else None, method=method, headers=h)
    try:
        with urllib.request.urlopen(r, timeout=90) as resp:
            t = resp.read(); return resp.status, (json.loads(t) if t else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")[:300]

class Client:
    def __init__(self, user, dev, ignore_stop=False):
        self.user, self.dev, self.ignore_stop = user, dev, ignore_stop
        _, a = http("POST", "/Users/AuthenticateByName", {"Username": user, "Pw": user}, dev=dev)
        self.token, self.uid = a["AccessToken"], a["User"]["Id"]
        self.msgs, self.playing = [], None
        self.ws = websocket.WebSocketApp("ws://127.0.0.1:8096/socket", header=[f'Authorization: MediaBrowser Client="e2e", Device="{dev}", DeviceId="{dev}", Version="1.0", Token="{self.token}"'], on_message=self._on)
        threading.Thread(target=self.ws.run_forever, daemon=True).start()
        time.sleep(0.7)
    def _on(self, ws, m):
        m = json.loads(m)
        if m.get("MessageType") in ("KeepAlive", "ForceKeepAlive"): return
        self.msgs.append(m)
        if m.get("MessageType") == "Playstate" and m["Data"].get("Command") == "Stop" and not self.ignore_stop:
            threading.Thread(target=self.stop, daemon=True).start()
    def kinds(self):
        out = []
        for m in self.msgs:
            if m["MessageType"] == "GeneralCommand": out.append(("Message", m["Data"]["Arguments"].get("Text")))
            elif m["MessageType"] == "Playstate": out.append(("Playstate", m["Data"]["Command"]))
        return out
    def play(self, ch, mode="autoopen"):
        t0 = time.time()
        body = {"UserId": self.uid, "AutoOpenLiveStream": mode == "autoopen"}
        code, info = http("POST", f"/Items/{ch}/PlaybackInfo", body, self.token, self.dev)
        if code != 200 or info.get("ErrorCode"):
            return False, f"PlaybackInfo {code} {info if code != 200 else info.get('ErrorCode')}", time.time() - t0
        ms = info["MediaSources"][0]
        psid = info["PlaySessionId"]
        if mode == "open":
            code, res = http("POST", f"/LiveStreams/Open?playSessionId={psid}&itemId={ch}", {"OpenToken": ms["OpenToken"], "UserId": self.uid}, self.token, self.dev)
            if code != 200: return False, f"LiveStreams/Open {code} {res}", time.time() - t0
            ms = res["MediaSource"]
        if not ms.get("LiveStreamId"):
            return False, "no LiveStreamId", time.time() - t0
        self.playing = {"ItemId": ch, "MediaSourceId": ms["Id"], "LiveStreamId": ms["LiveStreamId"], "PlaySessionId": psid}
        http("POST", "/Sessions/Playing", dict(self.playing, PlayMethod="DirectStream", CanSeek=False), self.token, self.dev)
        return True, ms["LiveStreamId"], time.time() - t0
    def stop(self):
        if self.playing:
            p, self.playing = self.playing, None
            http("POST", "/Sessions/Playing/Stopped", p, self.token, self.dev)
    def close(self):
        self.stop(); self.ws.close()

def config(**kw):
    tok = ST["admin"]
    _, c = http("GET", f"/Plugins/{PLUGIN}/Configuration", token=tok, dev="setup")
    c.update({"Enabled": True, "PriorityUserIds": [ST["ids"]["prio"]], "VerboseLogging": True, "PreemptOnPlaybackInfo": True,
              "PreemptOwnDevices": True, "TunerReleaseTimeoutSeconds": 5, "UpstreamSettleDelayMs": 500,
              "BlockWhenBusy": True, "ExemptChannelIds": []})
    c.update(kw)
    code, _ = http("POST", f"/Plugins/{PLUGIN}/Configuration", c, tok, "setup")
    assert code in (200, 204), code

def hdhr_mark():
    return len(open(HDHR_LOG).read().splitlines())
def hdhr_since(mark):
    return [l.split(" ", 1)[1] for l in open(HDHR_LOG).read().splitlines()[mark:]]

def check(name, cond, detail):
    RESULTS.append((name, cond))
    print(("PASS " if cond else "FAIL ") + name + " :: " + detail, flush=True)

def scenario(name, fn, **cfg):
    config(**cfg)
    clients = []
    mark = hdhr_mark()
    try:
        fn(clients, mark)
    finally:
        for c in clients: c.close()
        time.sleep(1.5)

def control(clients, mark):
    bob = Client("bob", "bob-tv"); prio = Client("prio", "prio-phone"); clients += [bob, prio]
    ok_b, _, _ = bob.play(A)
    ok_p, err, _ = prio.play(Bc)
    check("control (plugin disabled): priority request fails while bob holds the tuner", ok_b and not ok_p, f"bob={ok_b} prio={ok_p} err={err!r} hdhr={hdhr_since(mark)}")

def a_autoopen(clients, mark):
    bob = Client("bob", "bob-tv"); prio = Client("prio", "prio-phone"); clients += [bob, prio]
    ok_b, _, _ = bob.play(A)
    ok_p, res, dt = prio.play(Bc)
    time.sleep(0.3)
    log = hdhr_since(mark)
    check("(a) PlaybackInfo+AutoOpen: priority gets channel B", ok_b and ok_p, f"prio={ok_p} {res} in {dt:.2f}s")
    check("(a) bob got the message with {user} replaced and a Stop", ("Message", "Live TV was taken over by prio.") in bob.kinds() and ("Playstate", "Stop") in bob.kinds(), str(bob.kinds()))
    check("(a) upstream: ch1 closed before ch2 opened, no 503", log == ["OPEN ch1", "CLOSE ch1", "OPEN ch2"], str(log))

def a_openpath(clients, mark):
    bob = Client("bob", "bob-tv"); prio = Client("prio", "prio-phone"); clients += [bob, prio]
    bob.play(A, mode="open")
    ok_p, res, dt = prio.play(Bc, mode="open")
    check("(a') LiveStreams/Open path: priority gets channel B", ok_p, f"{res} in {dt:.2f}s; bob={bob.kinds()} hdhr={hdhr_since(mark)}")

def a_stubborn(clients, mark):
    bob = Client("bob", "bob-firestick", ignore_stop=True); prio = Client("prio", "prio-phone"); clients += [bob, prio]
    bob.play(A)
    ok_p, res, dt = prio.play(Bc)
    check("(a'') client ignoring Stop: tuner still released server-side", ok_p and "OPEN ch2" in hdhr_since(mark), f"{res} in {dt:.2f}s hdhr={hdhr_since(mark)}")

def b_same(clients, mark):
    bob = Client("bob", "bob-tv"); prio = Client("prio", "prio-phone"); clients += [bob, prio]
    bob.play(A)
    ok_p, res, _ = prio.play(A)
    time.sleep(0.3)
    check("(b) same channel: shared, bob not kicked, one upstream connection", ok_p and bob.kinds() == [] and hdhr_since(mark) == ["OPEN ch1"], f"prio={ok_p} bob={bob.kinds()} hdhr={hdhr_since(mark)}")

def d_two_devices(clients, mark):
    tv = Client("prio", "prio-tv"); phone = Client("prio", "prio-phone"); clients += [tv, phone]
    tv.play(A)
    ok, res, _ = phone.play(Bc)
    time.sleep(0.3)
    check("(d) priority user on two devices: other device preempted", ok and ("Playstate", "Stop") in tv.kinds(), f"phone={ok} tv={tv.kinds()}")

def d_protected(clients, mark):
    tv = Client("prio", "prio-tv"); phone = Client("prio", "prio-phone"); clients += [tv, phone]
    tv.play(A)
    ok, res, _ = phone.play(Bc)
    check("(d') own devices protected: default behavior (request fails, tv untouched)", not ok and tv.kinds() == [], f"phone={ok} {res!r} tv={tv.kinds()}")

def same_device_switch(clients, mark):
    phone = Client("prio", "prio-phone"); clients += [phone]
    phone.play(A)
    ok, res, _ = phone.play(Bc)
    check("same device channel switch A->B: old stream released silently", ok and phone.kinds() == [], f"ok={ok} {res} msgs={phone.kinds()} hdhr={hdhr_since(mark)}")

def race(clients, mark):
    bob = Client("bob", "bob-tv"); carol = Client("carol", "carol-tv"); prio = Client("prio", "prio-phone"); clients += [bob, carol, prio]
    bob.play(A)
    out = {}
    tp = threading.Thread(target=lambda: out.__setitem__("prio", prio.play(Bc)))
    tc = threading.Thread(target=lambda: (time.sleep(0.15), out.__setitem__("carol", carol.play(A))))
    tp.start(); tc.start(); tp.join(); tc.join()
    check("race: non-priority re-tune during preemption does not steal the tuner", out["prio"][0] and not out["carol"][0],
          f"prio={out['prio'][:2]} carol={out['carol'][:2]} hdhr={hdhr_since(mark)}")

def race_open_in_progress(clients, mark):
    carol = Client("carol", "carol-tv"); prio = Client("prio", "prio-phone"); clients += [carol, prio]
    out = {}
    tc = threading.Thread(target=lambda: out.__setitem__("carol", carol.play(A)))
    tp = threading.Thread(target=lambda: (time.sleep(0.05), out.__setitem__("prio", prio.play(Bc))))
    tc.start(); tp.start(); tc.join(); tp.join()
    time.sleep(0.3)
    log = hdhr_since(mark)
    check("race: priority request while a non-priority open is still in progress", out["prio"][0] and ("Playstate", "Stop") in carol.kinds() and not any("REJECT" in l for l in log),
          f"carol={out['carol'][:2]} prio={out['prio'][:2]} carolmsgs={carol.kinds()} hdhr={log}")

def two_priority_simultaneous(clients, mark):
    bob = Client("bob", "bob-tv"); p1 = Client("prio", "prio-phone"); p2 = Client("prio", "prio-tablet"); clients += [bob, p1, p2]
    bob.play(A)
    out = {}
    t1 = threading.Thread(target=lambda: out.__setitem__("p1", p1.play(Bc)))
    t2 = threading.Thread(target=lambda: out.__setitem__("p2", p2.play(Bc)))
    t1.start(); t2.start(); t1.join(); t2.join()
    log = hdhr_since(mark)
    check("two simultaneous priority requests for the same channel: both succeed, one upstream connection", out["p1"][0] and out["p2"][0] and log.count("OPEN ch2") == 1 and not any("REJECT" in l for l in log),
          f"p1={out['p1'][:2]} p2={out['p2'][:2]} hdhr={log}")

def busy_block(clients, mark):
    bob = Client("bob", "bob-tv"); carol = Client("carol", "carol-tv"); clients += [bob, carol]
    bob.play(A)
    ok, res, _ = carol.play(Bc)
    time.sleep(0.3)
    log = hdhr_since(mark)
    check("busy: normal user refused with the channel they can watch instead, no upstream attempt",
          not ok and "NotAllowed" in str(res) and ("Message", "bob is watching Chan A. You can watch Chan A instead.") in carol.kinds()
          and bob.kinds() == [] and log == ["OPEN ch1"], f"carol={ok} {res!r} msgs={carol.kinds()} bob={bob.kinds()} hdhr={log}")
    ok2, res2, _ = carol.play(A)
    check("busy: joining the suggested channel works (shared)", ok2 and hdhr_since(mark) == ["OPEN ch1"], f"carol={ok2} {res2!r} hdhr={hdhr_since(mark)}")

def busy_open_path(clients, mark):
    bob = Client("bob", "bob-tv"); carol = Client("carol", "carol-tv"); clients += [bob, carol]
    bob.play(A, mode="open")
    ok, res, _ = carol.play(Bc, mode="open")
    check("busy via LiveStreams/Open: refused with 409 and the message", not ok and "409" in str(res) and any(k[0] == "Message" for k in carol.kinds()),
          f"carol={ok} {str(res)[:160]!r} msgs={carol.kinds()}")

def busy_disabled(clients, mark):
    bob = Client("bob", "bob-tv"); carol = Client("carol", "carol-tv"); clients += [bob, carol]
    bob.play(A)
    ok, res, _ = carol.play(Bc)
    check("busy blocking off: default Jellyfin failure, no message", not ok and "500" in str(res) and carol.kinds() == [], f"carol={ok} {res!r} msgs={carol.kinds()}")

def exempt_viewer(clients, mark):
    bob = Client("bob", "bob-tv"); prio = Client("prio", "prio-phone"); clients += [bob, prio]
    bob.play(A)
    ok, res, _ = prio.play(Bc)
    check("exempt channel A: its viewer is not kicked by the priority user (plugin stays out)", not ok and bob.kinds() == [], f"prio={ok} {res!r} bob={bob.kinds()}")

def exempt_request(clients, mark):
    bob = Client("bob", "bob-tv"); carol = Client("carol", "carol-tv"); prio = Client("prio", "prio-phone"); clients += [bob, carol, prio]
    bob.play(A)
    ok, res, _ = carol.play(Bc)
    okp, resp, _ = prio.play(Bc)
    check("exempt channel B: normal user not blocked by the plugin, priority user does not preempt", not ok and "500" in str(res) and carol.kinds() == []
          and not okp and bob.kinds() == [], f"carol={ok} {res!r} prio={okp} bob={bob.kinds()}")

def hls_path(clients, mark):
    bob = Client("bob", "bob-tv"); prio = Client("prio", "prio-web"); clients += [bob, prio]
    bob.play(A)
    t0 = time.time()
    code, info = http("POST", f"/Items/{Bc}/PlaybackInfo", {"UserId": prio.uid, "AutoOpenLiveStream": False}, prio.token, prio.dev)
    ms = info["MediaSources"][0]
    hdr = {"Authorization": f'MediaBrowser Client="e2e", Device="prio-web", DeviceId="prio-web", Version="1.0", Token="{prio.token}"'}
    def get(path):
        with urllib.request.urlopen(urllib.request.Request(B + path, headers=hdr), timeout=90) as r: return r.status, r.read()
    q = f"MediaSourceId={ms['Id']}&PlaySessionId={info['PlaySessionId']}&VideoCodec=h264&AudioCodec=aac&SegmentContainer=ts&TranscodingMaxAudioChannels=2"
    st, master = get(f"/Videos/{Bc}/master.m3u8?{q}")
    variant = [l for l in master.decode().splitlines() if l and not l.startswith("#")][0]
    # live.m3u8 is what opens the tuner (TranscodeManager). ffmpeg then fails on stock 12.2 with this fake
    # tuner even without the plugin, so only the tuner hand-over is asserted here.
    try:
        get(f"/Videos/{Bc}/{variant}")
    except Exception:
        pass
    time.sleep(0.5)
    log = hdhr_since(mark)
    check("HLS live.m3u8 without LiveStreamId: bob kicked and tuner handed to channel B", ("Playstate", "Stop") in bob.kinds()
          and log[:3] == ["OPEN ch1", "CLOSE ch1", "OPEN ch2"] and not any("REJECT" in l for l in log),
          f"variant={variant.split('?')[0]} in {time.time()-t0:.2f}s bob={bob.kinds()} hdhr={log}")
    http("DELETE", f"/Videos/ActiveEncodings?deviceId=prio-web&playSessionId={info['PlaySessionId']}", token=prio.token, dev="prio-web")

def nonpriority_unchanged(clients, mark):
    prio = Client("prio", "prio-tv"); bob = Client("bob", "bob-phone"); clients += [prio, bob]
    prio.play(A)
    ok, res, _ = bob.play(Bc)
    check("non-priority user cannot preempt (default behavior)", not ok and prio.kinds() == [], f"bob={ok} {res!r} prio={prio.kinds()}")

scenario("control", control, Enabled=False)
scenario("a", a_autoopen)
scenario("a'", a_openpath, PreemptOnPlaybackInfo=False)
scenario("a''", a_stubborn)
scenario("b", b_same)
scenario("d", d_two_devices)
scenario("d'", d_protected, PreemptOwnDevices=False)
scenario("switch", same_device_switch)
scenario("race", race)
scenario("two-prio", two_priority_simultaneous)
scenario("race-open", race_open_in_progress)
scenario("nonprio", nonpriority_unchanged)
scenario("hls", hls_path, PreemptOnPlaybackInfo=False)
scenario("busy", busy_block)
scenario("busy-open", busy_open_path, PreemptOnPlaybackInfo=False)
scenario("busy-off", busy_disabled, BlockWhenBusy=False)
scenario("exempt-viewer", exempt_viewer, ExemptChannelIds=[A])
scenario("exempt-request", exempt_request, ExemptChannelIds=[Bc])
print(f"\n{sum(1 for _, ok in RESULTS if ok)}/{len(RESULTS)} checks passed")
