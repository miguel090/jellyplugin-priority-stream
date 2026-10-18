# Manual test plan

**Setup.** One tuner (Dispatcharr max streams = 1). Users: **Dad** (priority) and **Kid** (normal).
In the plugin settings, select Dad, turn on *Verbose logging* and save.
Keep the server log open: `docker logs -f jellyfin 2>&1 | grep -i "live tv"`.

## (a) Non-priority on channel A, priority starts channel B

1. Kid starts channel **A** on the Android TV.
2. Dad starts channel **B** on the phone.

Expected:
- Within about 1–2 s, Kid's TV shows *"Live TV was taken over by Dad."* and playback stops.
- Dad's channel B starts on the first tap, with no error and no retry.
- The log shows `Live TV preemption: "Dad" … stopping "Kid" … watching "A"` and then `tuner released … after N ms`.
- Dispatcharr shows A stopped, then B started.

Variants worth trying: Dad starts from the web client, from a guide program instead of the channel
list, and from the Fire TV.

## (b) Both on the same channel

1. Kid starts channel **A**.
2. Dad starts channel **A**.

Expected: both play, Kid sees no message, and Dispatcharr shows one connection.
Log (verbose): `Requested channel 'A' is already tuned (shared …); nothing to preempt.`

## (c) Recording in progress

1. Schedule a recording on channel **A** that is in progress now (Guide → program → Record).
2. Dad starts channel **B**.

Expected with the default (*Active DVR recordings* off): the recording continues, and Dad gets the
normal Jellyfin "can't play" error. Verbose log:
`Channel 'A' cannot be preempted: recording '…' is in progress and recordings are protected.`

3. Turn *Active DVR recordings* on, save, and repeat step 2.

Expected: the recording stops (its partial file stays in the library), Dad's channel B plays, and the
log shows `cancelling recording …`.

Also check: Dad starts channel **A** while A is being recorded. It plays (shared) and the recording
is not affected.

## (d) Priority user on two devices

1. Dad starts channel **A** on the living-room TV.
2. Dad starts channel **B** on the phone.

Expected with the default (*own other devices* on): the TV shows the message and stops, and the phone plays B.

3. Turn *Priority user's own other devices* off, save, and repeat.

Expected: the TV keeps playing A, and the phone gets the normal error.
Verbose log: `… own device '…' is protected`.

Also check: Dad zaps A → B on the **same** device. It switches normally, and no message is shown on that device.

## (e) Normal user, tuner busy with another normal user

1. Kid starts channel **A**.
2. Mum (also a normal user) starts channel **B**.

Expected: Mum's request is refused at once. Her device shows *"Kid is watching A. You can watch A instead."*,
and the web client also shows its own "can't play" dialog. Kid is untouched, Dispatcharr never sees a
request for B, and the log shows `Live TV busy: refused …`.

3. Mum starts channel **A**: it plays (shared).

With *Refuse busy requests from normal users* off, step 2 gives Jellyfin's normal error and no message.

## (f) Exempt channels

1. Mark channel **A** as exempt and save.
2. Kid starts **A**, then Dad starts **B**.

Expected: Kid is **not** kicked (the plugin ignores A). Dad gets whatever Jellyfin does by default.

3. Mark **B** as exempt instead, and repeat with Mum starting **B** while Kid watches **A**.

Expected: Mum is not refused by the plugin (no busy message). Jellyfin's default behavior applies.

## Extra checks

- **Normal users can't preempt:** Dad watches A, Kid starts B. Kid is refused with the busy message and Dad is untouched.
- **Client ignores Stop:** use a client that doesn't react to remote control. Dad still gets B,
  because the stream is closed server-side.
- **Plugin disabled:** with *Enable preemption* off, scenario (a) gives Dad the normal error.
