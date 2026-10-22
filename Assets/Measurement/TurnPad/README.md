# TurnPad: the participant marks their own turn

A 9 cm disc fixed in the room, always **horizontal (face up)**, like a button lying on the table (passthrough
measurement mode only). The label lies flat beyond the disc, readable from where the participant stands. The
participant presses it from above with the tip of the index finger (hand tracking, no controllers) to mark
**turn start**, and again for **turn end**.

## Look and motion (`TurnPadVisual`)
Sober and short, with no bounce:
- **Appear:** the ring draws clockwise, then the disc grows from 55 % with an ease-out.
- **Hold:** a progress arc fills clockwise from 12 o'clock.
- **Turn change:** the colour fades through a light tint, from green (waiting) to orange (in turn, the panel's
  colour), and a ripple leaves the edge.
- **Move (pinch):** the disc travels along an arch while two arc segments orbit its ring, with a ripple where it
  leaves and one where it lands.
- **Pinch:** a progress ring at the fingertips, which closes onto them when it completes.

Batch preview with no headset: `FeedbackCuePreview.RenderPad` (Assets/Editor).

## Gesture (why it doesn't fire by accident)
- Only the **index fingertip** counts, and only with the index **extended** (pointing, ≤ 50° bend).
- The finger has to come **from the front** of the disc (it must pass in front of the face first).
- If 2+ other fingertips are on the disc too (palm, fist, a block in the hand), it doesn't count.
- **Hold 0.45 s**: a light ring fills while holding; then a sound plays and the label flips.
- After each change the finger must leave the disc before it can toggle again (1 s cooldown).

## Events (official path, Documentation/architecture.md)
Each change calls `AcquisitionEventManager.EmitInteractionEvent("TURN_START" | "TURN_END", "turn", ...)`:
- `events.csv` (row `INTERACTION`) on this PC;
- BioLab over UDP from this PC: `E:VR_<PC>,TURN_START|participant=…|session=…|task=…|trial=…|node=VR_<PC>|t_local=…|t_host=…|sync=…|turn=3|source=hand_right,0`.
  `TURN_END` also carries `duration_s=12.345`. `t_host` is the host clock (NetworkClockSync), like every other event.
- If the acquisition session hasn't started yet, the row is `INTERACTION_REJECTED` (same as grabs).

It also writes a mark in the video's `marks.csv` (`TURN_START turno=3 source=hand_right`), and reports
`"turn":{visible,placed,state,n,secs,hover,source}` in the app's status (session panel).

## One turn at a time across PCs (`TurnCoordinator`, 2026-10-22)
Only one participant can hold the turn. When someone starts a turn (disc, gesture or panel `turn start`), the turn in
progress on any other PC ends by itself: `TURN_END` with `source=handover|next=<PC>` on that PC (events.csv, BioLab,
marks.csv), so the next player only has to press their own button.
- **Rule (`Core/TurnLedger.cs`, `Core/TurnSync.cs`, no Unity, EditMode tests):** every PC keeps the "newest turn known"
  (PC, key). A turn start gets key = max(UTC ticks, newest known key + 1); the bigger key wins (tie: PC name). So a
  press made after another one this PC knew about always wins, even with a wrong clock. Two presses that didn't know
  about each other (simultaneous, or one PC cut off) are ordered by the clocks (ntp.uc.cl, ms apart): the later wins.
  Merging is taking the max, so order, duplicates and path don't matter: everyone converges to the same owner.
- **Network (`TurnCoordinator`):** the Netcode link host↔clients, one named message `XRC.TurnLatest` (PC, key).
  - A client sends the host its newest turn when it starts a turn, when it (re)connects, and whenever the host knows
    something older than it does. The host merges and, if it changed, tells everyone.
  - The host repeats it every 2 s, so a lost message, a reconnecting client or a restarted app catches up by itself.
- **Panel backup (`turn_guard_loop` in tools/lab-dashboard):** if an app still has an older "newest turn" for 2 checks in
  a row (host down or restarting, client cut off from the host), the panel sends it `turn latest PC KEY` over the
  control channel. That works without the host. The app status shows `turn.me`, `turn.latestPc`, `turn.latestKey`.
- **App restarts (`turn-state.txt` in persistentDataPath):** the turn in progress, the turn number (per day) and the
  newest known turn are saved on every change (tmp + replace). On startup a turn that began < 30 min ago is resumed
  (`TURN_PAD RESUMED` in events.csv, no new TURN_START); an older one is not (`TURN_PAD STALE_NOT_RESUMED`). If
  someone else took the turn meanwhile, the resumed turn ends as soon as the app hears about it, and the TURN_END
  carries `next_start_utc` = when the other turn really started.
- No host and no panel: each PC keeps its local turn, and everything reconciles when either comes back.

## Placement
- **Automatic** the first time the headset is worn (FOCUSED 3 s, looking roughly level): 40 cm ahead, 24 cm to
  the side of the hand (`-turnpad-side left|right`, default right), 40 cm below the eyes, facing the eyes.
- **`turn place`** (panel button «Colocar»): at the tip of the participant's index finger (they point where they
  want it), or the default spot if no hand is tracked. It stays fixed in the room after that.
- **Pinch to move** (participant, no operator needed): an "OK" pinch (thumb tip on index tip, middle/ring/little
  extended) held still for 0.8 s moves the disc to the pinch point. A blue ring at the fingers shrinks while holding.
  To avoid moving it while handling blocks: the pinch must close from an open hand (> 4.5 cm apart within 0.6 s),
  the other three fingers must stay straight (≤ 50° bend), and the pinch must stay within 3 cm. Thresholds follow
  Ultraleap's pinch detector (on 2.2 cm / off 3.5 cm). `-noturnpad-pinch` disables it. Logged as `TURN_PAD MOVED`.

## Control channel (127.0.0.1:47811)
`turn start|end` (operator fallback, source `panel`), `turn place`, `turn show|hide`. `-noturnpad` disables it.

## Design variants and turn gesture (2026-10-15)
Without depth on VIVE PC streaming, the hand cut-out trails behind, so the hand can look like it is *under* the disc.
- `turn style field|solid|ghost|fade|ring` (start: `-turnpad-style X`):
  - **field** (default since 2026-10-15): no disc at all. A thin glowing outline plus ~450 light dots that flow along the
    field lines of a magnet in the pad centre (rise from the middle, arc up, land on the rim), all computed in one
    vertex shader (`Shaders/PadField.shader`, one draw call, additive light). Hand near: dots speed up and are pulled
    toward the index tip; holding: they collapse onto the outline (the ring charges); toggle: flash. Nothing solid,
    so the hand never looks like it is under the button and no occlusion is needed.
  - **solid**: the opaque metal disc (before).
  - **ghost**: always semi-transparent (face 45 %, base 30 %).
  - **fade**: opaque when the hand is far; translucent (≈ 18 %) as any fingertip or the palm comes within 15 → 3 cm.
  - **ring**: like fade, but the disc vanishes completely near the hand, leaving only the ring and the hold arc.
- **Hand covers the pad (all styles, 2026-10-15):** hand occlusion was removed, so instead, every frame ~16 hand
  joints are projected from the eye onto the pad plane. If any joint in front of the pad lands on the disc or its
  label (+1.2 cm margin), the pad, ring and text fade to 12 % in ~35 ms and come back in ~170 ms; solid switches
  to its translucent material meanwhile. The hold-progress arc stays fully visible. Cost: a few dozen vector ops.
- `turn mode pad|gesture|both` (start: `-turn-mode X`): **gesture** = thumbs-up (thumb ≤ ~41° from vertical and
  straight, the other four fingers curled against the palm, thumb tip ≥ 4 cm above the palm), hand still (≤ 3 cm) for
  0.6 s; the same progress ring fills at the thumb. 1.5 s cooldown and the hand must leave the pose before the next.
  Source is `gesture_left|gesture_right`; it emits the same `TURN_START|TURN_END` through `EmitInteractionEvent`.
  In `gesture` mode the disc is hidden.
- Both show up in the status JSON (`turn.style`, `turn.mode`) and are logged as `TURN_PAD STYLE …|MODE …`.
