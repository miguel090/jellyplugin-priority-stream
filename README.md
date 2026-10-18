# Live TV Priority (Jellyfin plugin)

Tuner preemption for a **priority user** on single-tuner Live TV setups.

When a priority user starts a Live TV channel while somebody else holds the only tuner on a
**different** channel, the other session gets a message, is stopped, its live stream is closed
server-side, and the priority user's stream opens on the **same tap**: no error, no retry.
On the **same** channel nothing happens; the stream is simply shared.

Built for: IPTV provider (1 connection) → Dispatcharr → HDHomeRun emulation → Jellyfin Live TV.
Works with any client (web, Android TV, mobile, Fire TV) because everything happens on the server.

| | |
|---|---|
| Jellyfin | **12.2** (`targetAbi` 12.2.0.0) |
| .NET | **10** (what Jellyfin 12.2 is built on) |
| Plugin GUID | `443cd6fe-a768-4ae2-a39e-e87e0e8e359d` |

## How it works

I read the Jellyfin v12.2 source for this design. In 12.2 every tuner open goes through
`IMediaSourceManager.OpenLiveStream(Internal)`, which three API paths can reach:

1. `POST /Items/{id}/PlaybackInfo` with `AutoOpenLiveStream=true` (web client and most apps),
2. `POST /LiveStreams/Open`,
3. streaming endpoints without a `LiveStreamId` (`/Videos/{id}/stream`, `live.m3u8`, …), where
   `TranscodeManager` opens the stream itself.

The plugin registers a global **MVC action filter** (`IAsyncActionFilter`, added through
`IPluginServiceRegistrator` → `Configure<MvcOptions>`). It runs after authentication, so the user is
known from the token. It also runs after model binding, so the request DTOs are available. And it
runs **before** the action body that opens the tuner. Reacting to `PlaybackStart` would be too late.

For a priority user's request on a `LiveTvChannel` (guide programs are mapped to their channel):

1. Enter a server-wide lock so concurrent tune requests are serialized.
2. Work out who holds the tuner: sessions playing a channel whose live stream is still open, in-progress
   recordings, and streams that were just opened but aren't reported as playing yet.
3. If the requested channel is already tuned, do nothing (the stream is shared). If a tuner is free, do nothing.
4. Otherwise, for the channel to free: send the message (`SendMessageCommand`) and `Stop`
   (`SendPlaystateCommand`), kill the device's transcodes, and call `CloseLiveStream` until the stream
   is really gone. `CloseLiveStream` only decrements a consumer count. The close happens server-side,
   so a client that ignores `Stop` still loses the tuner.
5. Wait until the tuner is released (configurable timeout), plus a short upstream settle delay. Then
   let the request continue and open the new channel.

Every preemption is logged as a warning:

```
Live TV preemption: "dad" ("pixel-8") requested "BBC One"; stopping "kid" on "Living room TV" (Android TV) watching "Cartoon Network".
Live TV preemption: tuner released for "dad" -> "BBC One" after 512 ms.
```

## Install

### From the plugin repository (recommended)

1. Dashboard → **Plugins** → **Repositories** → **+**
   - Name: `Live TV Priority`
   - URL: `https://raw.githubusercontent.com/miguel090/jellyplugin-priority-stream/main/manifest.json`
     (replace `main` with the repository's default branch if it is different)
2. **Catalog** → *Live TV Priority* → **Install**.
3. Restart Jellyfin (`docker restart jellyfin`).
4. Dashboard → Plugins → *Live TV Priority*: pick the priority user(s) and save.

New releases then show up as updates in the same place.

### Manually

Download `live-tv-priority_X.Y.Z.0.zip` from the GitHub releases and unzip it into a folder in the
server's plugin directory, then restart:

- official `jellyfin/jellyfin` image: `/config/plugins/LiveTvPriority/`
- linuxserver.io image: `/config/data/plugins/LiveTvPriority/`

## Configuration

| Setting | Default | Notes |
|---|---|---|
| Enable preemption | on | Off = behavior exactly as without the plugin. |
| Priority users | none | Multi-select from Jellyfin users. Nobody else can preempt. |
| Show a message | on | Uses `DisplayMessage`. Clients that don't support it just stop. |
| Message title / text | `Live TV` / `Live TV was taken over by {user}.` | `{user}` = the priority user's name. |
| Message display time | 8000 ms | 0 = client default. |
| Priority user's own other devices | **on** | E.g. the TV is stopped when the same user starts another channel on the phone. |
| Active DVR recordings | **off** | On: an in-progress recording on another channel is cancelled. |
| Priority users can preempt each other | off | Off: a watching priority user is never kicked by another one. |
| Tuner release timeout | 5 s | Max wait for the tuner to be freed. After that the request continues anyway. |
| Upstream settle delay | 500 ms | Extra pause after the release so Dispatcharr/the provider can drop the old connection. |
| Preempt at PlaybackInfo | on | Starts freeing the tuner at the earliest request. |
| Number of tuners | 0 (auto) | 0 = sum of `TunerCount` from the Live TV tuner settings (1 if unknown). Preemption only happens when every tuner is busy. |
| Verbose logging | off | Logs every decision at Information level. Preemptions are always logged. |

## Dispatcharr / upstream tuning

Jellyfin releases its tuner instantly. The **upstream** connection is a separate matter: with a
one-connection IPTV account, the provider rejects the new channel while Dispatcharr still holds the
old one.

- In Dispatcharr's proxy settings, set the **channel shutdown delay** (the linger time after the last
  client disconnects; called *Channel Shutdown Delay* in recent versions) to **0–1 s**. Long
  linger times are the most common reason the new channel fails right after a preemption.
- Set the M3U account's max streams to **1** and the HDHomeRun tuner count Dispatcharr advertises to **1**.
  Jellyfin then sees one tuner, which is what this plugin assumes.
- If takeovers still fail sometimes, raise *Upstream settle delay* (e.g. 1500 ms) before raising the timeout.

## Build from source

```bash
dotnet test                                   # unit tests
dotnet publish src/Jellyfin.Plugin.LiveTvPriority -c Release -o out
# -> out/Jellyfin.Plugin.LiveTvPriority.dll   (the only file the server needs)
```

Requires the .NET 10 SDK. The Jellyfin packages (`Jellyfin.Controller`/`Jellyfin.Model` 12.2.0) are
compile-time only and are not copied to the output.

`tests/e2e/` holds the end-to-end harness used during development: a fake one-connection HDHomeRun
plus a script that drives a real Jellyfin 12.2 server through the test plan.

## Releasing

Push a tag: `git tag v1.0.0 && git push origin v1.0.0` (or run the *Release* workflow manually).
`.github/workflows/release.yml` then:

1. runs the tests and builds with version `1.0.0.0`,
2. zips the dll with a `meta.json`, computes its MD5 and attaches the zip to a GitHub release,
3. adds the version to `manifest.json` on the default branch, which is what Jellyfin reads.

## Known limitations

- **Single tuner assumed.** With more tuners, the plugin frees only as many channels as needed, using
  the tuner count from Jellyfin's tuner settings or the override.
- **The tuner holder is detected from sessions.** A client that never reports playback, or reports it
  without a `LiveStreamId`, is still stopped and has its transcodes killed. But without a stream id,
  its stream may only close when its transcoder dies. In that case the wait can run until the timeout.
- **Messages are best effort.** Some clients ignore `DisplayMessage`, or stop before showing it.
- **Cancelled recordings stay cancelled.** The partial file is kept; the recording is not resumed afterwards.
- **A preempted client may try again.** If it re-requests its channel while the takeover is still in
  flight, that request waits behind the priority request and then fails the normal way (tuner busy).
- **Upstream delays are outside Jellyfin.** If Dispatcharr or the provider keeps the old connection
  open longer than timeout + settle delay, the new channel can still fail. See the tuning section.
- **The coordination is per server.** The lock is in-memory, which is fine for one Jellyfin instance.

## Manual test plan

See [docs/TEST_PLAN.md](docs/TEST_PLAN.md).
