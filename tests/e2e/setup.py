"""Completes the startup wizard of a FRESH Jellyfin at :8096, creates users prio/bob/carol, adds the fake tuner.

Usage: python3 setup.py <state.json>
"""
import json, time, urllib.request, sys
B = "http://127.0.0.1:8096"
def req(method, path, body=None, token=None, dev="setup"):
    h = {"Content-Type": "application/json",
         "Authorization": f'MediaBrowser Client="e2e", Device="{dev}", DeviceId="{dev}", Version="1.0"' + (f', Token="{token}"' if token else "")}
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(B + path, data=data, method=method, headers=h)
    with urllib.request.urlopen(r, timeout=60) as resp:
        t = resp.read()
        return json.loads(t) if t else None
req("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
req("GET", "/Startup/User")
req("POST", "/Startup/User", {"Name": "admin", "Password": "admin"})
req("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True})
req("POST", "/Startup/Complete")
tok = req("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})["AccessToken"]
ids = {}
for n in ["prio", "bob", "carol"]:
    u = req("POST", "/Users/New", {"Name": n, "Password": n}, tok)
    ids[n] = u["Id"]
req("POST", "/LiveTv/TunerHosts", {"Type": "hdhomerun", "Url": "http://127.0.0.1:5004", "TunerCount": 1}, tok)
tasks = req("GET", "/ScheduledTasks", token=tok)
guide = [t for t in tasks if t.get("Key") == "RefreshGuide"][0]
req("POST", f"/ScheduledTasks/Running/{guide['Id']}", token=tok)
for _ in range(60):
    time.sleep(2)
    t = req("GET", f"/ScheduledTasks/{guide['Id']}", token=tok)
    if t["State"] == "Idle" and t.get("LastExecutionResult"): break
chans = req("GET", f"/LiveTv/Channels?userId={ids['bob']}", token=tok)["Items"]
print("channels:", [(c["Name"], c["Id"]) for c in chans])
json.dump({"admin": tok, "ids": ids, "channels": {c["Name"]: c["Id"] for c in chans}}, open(sys.argv[1], "w"))
