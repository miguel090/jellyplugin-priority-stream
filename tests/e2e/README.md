# End-to-end test harness

Drives a real Jellyfin 12.2 server through the test plan, against a fake HDHomeRun that (like a
one-connection IPTV account) refuses a second concurrent stream with HTTP 503.

Use a **throwaway** server: `setup.py` runs the startup wizard and creates users.

```bash
pip install websocket-client
ffmpeg -f lavfi -i testsrc=size=320x240:rate=25 -f lavfi -i sine -t 20 \
       -c:v libx264 -preset ultrafast -g 25 -c:a aac -f mpegts test.ts
python3 fakehdhr.py test.ts hdhr.log &                 # listens on 127.0.0.1:5004

# Fresh Jellyfin 12.2 on 127.0.0.1:8096 with the plugin dll in <data>/plugins/LiveTvPriority_1.0.0.0/
# (from source: dotnet publish Jellyfin.Server -o jf && dotnet jf/jellyfin.dll --datadir ... --nowebclient)
# or Docker with host networking so the server can reach 127.0.0.1:5004.

python3 setup.py state.json
python3 scenarios.py state.json hdhr.log
```

Each check prints `PASS`/`FAIL` with the upstream log, e.g. `['OPEN ch1', 'CLOSE ch1', 'OPEN ch2']`
proves the old channel was released before the priority channel was tuned, with no 503 in between.
DVR recordings are not covered here (the fake tuner has no guide data); see the unit tests.
