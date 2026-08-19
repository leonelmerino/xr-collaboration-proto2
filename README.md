# XR Collaboration Prototype

## Overview

Multi-user XR collaboration environment for research on immersive collaboration and human behavior analysis. Runs on a local LAN (no cloud services required).

Features:

- Multi-user session with 3 concurrent users (Host / Client / Helper) via Unity Netcode for GameObjects (NGO).
- Automatic host discovery on LAN via UDP broadcast (no manual IP configuration required).
- VR interaction with OpenXR (HTC Vive Focus Vision).
- Hand tracking with pinch, poke, and ray-grab interactions.
- Physics-based collaborative Jenga tower.
- Eye tracking integration (VIVE XR Eye Tracker).
- Structured CSV logging per session (offline pipeline).
- Optional integration with an external BioLab acquisition server (UDP), with automatic fallback to a local mock server for development.

---

## Requirements

### Hardware (per machine)

- HTC Vive Focus Vision
- Wired Streaming Kit (USB-C)
- VR-ready PC (recommended GPU: NVIDIA RTX series)

For the full multi-user setup: 3 identical machines (one per role) + optionally a 4th machine running the BioLab acquisition server.

### Software (per machine)

- Unity Hub — https://unity.com/download
- Unity Editor 2022.3 LTS (only needed on dev machines; runtime machines only need the standalone build)
- Visual Studio 2022 Community with "Game development with Unity" workload (dev only)
- Git — https://git-scm.com (dev only)
- Steam + SteamVR
- VIVE Streaming / VIVE Business Streaming

---

## Unity Installation (dev machines)

1. Open Unity Hub.
2. Install Unity 2022.3 LTS.
3. Add the module: **Windows Build Support (IL2CPP)**.

No additional platforms are required.

---

## Clone the Repository

```
git clone https://github.com/leonelmerino/xr-collaboration-proto2.git
```

---

## Open the Project

1. Open Unity Hub.
2. Click Open.
3. Select the project folder.
4. Ensure Unity version is 2022.3 LTS.
5. If prompted, select **Rebuild Library**.
6. Wait for compilation to finish.

---

## Configure OpenXR

Go to: `Edit → Project Settings → XR Plug-in Management → PC`

- Enable **OpenXR**.

Then in OpenXR settings:

- Enable **Khronos Simple Controller**.
- Enable **HTC Vive Controller** (if available).
- Enable feature set **HTC VIVE XR** (needed for VIVE Streaming).

### Eye Tracking

Under OpenXR Features:

- Enable **VIVE XR Eye Tracker**.

---

## Set OpenXR Runtime

1. Open SteamVR.
2. Go to `Settings → Developer`.
3. Click **Set SteamVR as OpenXR Runtime**.

---

## Connect HTC Vive Focus Vision (Wired)

1. Connect the headset via USB-C using the Wired Streaming Kit.
2. Open VIVE Streaming / VIVE Hub.
3. Start a streaming session.
4. Open SteamVR.

Verify the headset and controllers appear as ready (green).

---

## Network Configuration (LAN Multi-User)

The system uses UDP over the local network. Three ports need to be open on each machine:

| Port | Protocol | Used by | Purpose |
|------|----------|---------|---------|
| 7777 | UDP | NGO game traffic | Main netcode traffic (state sync, RPCs) |
| 7778 | UDP | LAN Discovery | Host broadcasts; clients listen for hosts |
| 1776 | UDP | BioLab acquisition | Only needed on the machine running the BioLab acquisition server (4th machine, if used) |

### Firewall Rules (Windows) — run once per machine as Administrator

On **all 3 VR laptops** (Host, Client, Helper):

```powershell
# NGO game port
New-NetFirewallRule -DisplayName "Unity NGO Game Port" -Direction Inbound `
    -Protocol UDP -LocalPort 7777 -Action Allow

# LAN Discovery
New-NetFirewallRule -DisplayName "Unity LAN Discovery" -Direction Inbound `
    -Protocol UDP -LocalPort 7778 -Action Allow
```

On the **4th machine** (only if using an external BioLab acquisition server):

```powershell
New-NetFirewallRule -DisplayName "BioLab Acquisition" -Direction Inbound `
    -Protocol UDP -LocalPort 1776 -Action Allow
```

Verify: `Get-NetFirewallRule -DisplayName "Unity*"` should list the two Unity rules.

### Network topology

All machines must be on the same LAN (same subnet). No internet is required. Wired ethernet recommended for stability during experiments.

---

## BioLab Acquisition Server (Optional)

If you are recording physiological data via an external BioLab acquisition server, configure it as follows.

### 1. Configure the acquisition endpoint in the scene

In Unity, open `Assets/Scenes/Room.unity` and select the GameObject that has the `AcquisitionNodeConfig` component (its `Node Id` field is `VR_HOST`).

In the Inspector:

- **Acquisition Ip**: set to the LAN IP address of the machine running the BioLab acquisition server (e.g. `192.168.1.42`). Find it with `ipconfig` on that machine.
- **Acquisition Port**: `1776` (default; must match the server).
- **Use Embedded Acquisition Mock**: leave **checked (✓)**.
- **Require Acquisition For Session Start**: leave **unchecked** unless you want the experiment to abort if the server is unreachable.

### 2. Mock fallback behavior

The embedded mock is a **fallback**, not an override. Behavior:

- On session start, each VR machine pings the configured `Acquisition Ip` at port `Acquisition Port`.
- If the ping returns `PONG` → the machine talks to the real acquisition server for the rest of the session.
- If the ping fails **and** `Use Embedded Acquisition Mock` is checked → the machine starts a local mock server on loopback (`127.0.0.1:1776`) and continues in dev mode. Events are absorbed by the mock but not forwarded anywhere.
- If the ping fails and mock is unchecked → the machine continues with `SESSION_START_NO_ACQ` (events are only logged locally, not sent).

In production sessions, keep the mock checked. It's transparent when the real server is up, and prevents runtime blocking when it's down.

### 3. Diagnostic logs

At session start, each machine logs one of:

```
[AcquisitionEventManager] Ping response (192.168.1.42:1776): PONG
```

means it's talking to the real server. Or:

```
[AcquisitionEventManager] Ping response (192.168.1.42:1776): TIMEOUT
[AcquisitionEventManager] Server real no responde. Fallback: arrancando mock local en loopback.
```

means the fallback kicked in. If you expected the real server to be reachable, check: server is running, IP is correct in the Inspector, firewall rule on port 1776 is in place on the server machine.

---

## Data Logging

The system automatically logs eye tracking data and interaction events during runtime.

Data is stored at:

```
<Application.persistentDataPath>/EyeTrackingLogs/<participant>/<session>/
```

(On Windows: `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\EyeTrackingLogs\...`)

Each run generates a new CSV file (no overwrite; auto-increments a numeric suffix).

### CSV categories

- **Gaze CSV** — per-frame left/right/combined gaze, pupil diameter, head pose, timestamps.
- **Events CSV** — discrete events (grab, release, task_start, session_start, interaction milestones), timestamps, node_id.

### Session ID hierarchy

The event payload carries a 4-level hierarchy:

| Field | Meaning | Example |
|-------|---------|---------|
| `participantId` | Human subject | `P001`, `P002` |
| `sessionId` | Block of work with the subject (typically one day) | `S001` first visit, `S002` retest |
| `taskId` | Experimental phase with a specific goal | `jenga_colab`, `warmup`, `debriefing` |
| `trialId` | Repeatable instance of a task (one task → N trials) | `trial_01`, `trial_02` |

Currently `participantId`, `sessionId`, `taskId` are read from the `ExperimentEventLogger` component in the scene at start. `taskId` is also updated at runtime via `BeginTask(id)` calls. `trialId` is currently a static field (no runtime API to change it). See bitácora entry `2026-08-18` for the semantic breakdown.

Configure these fields in `Room.unity` → GameObject with `ExperimentEventLogger` before building.

---

## Eye Tracking Setup

Before running experiments:

1. Put on the headset.
2. Open device settings.
3. Run eye tracking calibration.

Eye tracking must be calibrated per user. Data may be invalid if calibration is not performed.

---

## Running the Project

### From Unity Editor (dev)

1. Ensure SteamVR is running.
2. Press Play in Unity.
3. In the game window, press **H** to start as Host, or **C** to start as a Client.

The scene should appear in the headset. Status HUD at the bottom shows connection state.

### From standalone build (production)

See "Building a Standalone Executable" below.

Once the executable is running on each machine:

1. On the machine that should be the Host: press **H**. The HUD shows `[HOST] Host activo — anunciando en LAN puerto 7777`.
2. On the other two machines: press **C**. The HUD shows `[BUSCANDO] Buscando host en LAN...` → `[CONECTANDO] ...` → `[CONECTADO]`.
3. The role of each player (Host / Client / Helper — which determines avatar color, spawn position, sub-mesh) is assigned automatically by the server in connection order:
   - 1st connection (the Host machine itself) → role Host.
   - 2nd connection → role Client.
   - 3rd connection → role Helper.

**Note**: the player who wants to be the Helper must be the **last** to press `C`. There is no other configuration needed per machine (the same build runs on all three).

---

## Building a Standalone Executable

A single build runs on all 3 machines. The Host/Client role is chosen at runtime via keyboard (H/C).

### Pre-build checks (critical for VR-enabled builds)

The project ships with a deliberate setup that **disables XR in standalone builds by default**. This was done to support server-only / audit builds that share hardware with an active Editor session. For a normal VR build that must drive the headset, you have to revert this setup:

1. `Edit → Project Settings → XR Plug-in Management` → tab **PC, Mac & Linux Standalone** (the Windows icon tab, not Android):
   - **✓ Initialize XR on Startup** must be **checked**. If it's unchecked, the build will launch but the headset will not be driven.
   - **✓ OpenXR** must be enabled in that same tab.

2. In `Assets/Scenes/Room.unity`, locate the GameObject that carries the `BuildXRDisabler` component (it lives on the `_NetworkAudit` / bootstrap GameObject, alongside `NetworkAuditLogger`, `PerformanceHUD`, etc.):
   - Uncheck **Disable XR On Build** on the `Build XR Disabler` component. This script runs only in standalone builds and, if left with `disableXROnBuild = true`, will call `StopSubsystems()` + `DeinitializeLoader()` at Awake and disable every `TrackedPoseDriver` in the scene. Result: static camera and no headset.
   - Optionally uncheck **Disable Tracked Pose Drivers** too (redundant once the first flag is off, but tidier).

`EditorXRBootstrap` (on the same GameObject) can stay enabled — it only runs inside the Editor and does not affect builds.

If you ever need to make an audit/server-only build (running on a laptop without a headset), invert both: check `Disable XR On Build` and uncheck `Initialize XR on Startup`.

### Build settings

In Unity, go to `File → Build Settings`:

1. **Platform**: PC, Mac & Linux Standalone.
2. **Target Platform**: Windows.
3. **Architecture**: x86_64.
4. **Scenes in Build**: only `Assets/Scenes/Room.unity` should be checked.
5. Click **Player Settings...** and verify:
   - **XR Plug-in Management** (Standalone tab): OpenXR enabled + Initialize XR on Startup checked (see above).
   - **OpenXR**: HTC VIVE XR feature set enabled.
   - **Api Compatibility Level**: .NET Standard 2.1 (or whatever is already configured).
6. Back in Build Settings, click **Build**. Choose a destination folder.

Output: `xr-collaboration-proto2.exe` + `xr-collaboration-proto2_Data/` folder + supporting DLLs.

### Deploy to each laptop

1. Copy the entire build folder (all files, not just the .exe) to each of the 3 VR laptops.
2. Ensure the firewall rules from the "Network Configuration" section above are in place on each machine (one-time setup per machine).
3. Ensure SteamVR + VIVE Streaming session is active before launching the .exe.
4. Launch `xr-collaboration-proto2.exe` on each machine.
5. Follow the H/C keypress sequence described in "Running the Project → From standalone build".

### Known caveat: node_id / role for BioLab telemetry

Currently all 3 builds carry the same `nodeId: VR_HOST` and `role: Host` values from the scene, which means BioLab events from all 3 machines arrive with identical labels. Data still reaches the acquisition server, but you cannot distinguish origin without cross-referencing local logs. See TODO in `Documentation/dev_log.md` (`2026-08-18`) for the planned fix. Until then, options:

- Edit `Room.unity` per build (change `AcquisitionNodeConfig.role` and `nodeId` to `VR_CLIENT` / `VR_HELPER` before building each variant). Ugly but functional.
- Wait for the runtime override implementation in `NetworkLauncher` (pending session).

---

## Project Structure

- `Assets/Multiplayer/` — networking, role assignment, avatar sync (NGO)
- `Assets/Jenga/` — Jenga tower generator, blocks, interactions
- `Assets/EyeTracking/` — VIVE eye tracking provider and CSV logger
- `Assets/BiolabUDPSync/` — external acquisition server client + local mock server + event manager
- `Assets/NetworkAudit/` — HUD overlays for network state, performance
- `Assets/Scenes/` — main scene (`Room.unity`)
- `Documentation/` — dev log (`dev_log.md`), session notes (`bitacora.txt`)

---

## Troubleshooting

### Client cannot find Host on LAN

- Verify firewall rules on both machines (see "Network Configuration").
- Check both machines are on the same subnet (same first three octets of the IP).
- Wired ethernet is more reliable than Wi-Fi for discovery.
- Wait up to 6 seconds after pressing `C` (LAN Discovery poll interval).

### `[Netcode] All socket receive requests were marked as failed`

Firewall is blocking UDP 7777. Add the firewall rule and restart the build.

### BioLab events don't reach the acquisition server

- Verify the `Acquisition Ip` in the scene matches the LAN IP of the acquisition server machine.
- Check firewall on the acquisition server machine allows UDP 1776 inbound.
- Look for `Ping response (...): PONG` in the console. If you see `TIMEOUT` followed by `MOCK_FALLBACK_ACTIVATED`, the real server is unreachable.

### Standalone build launches but the headset shows nothing / camera is static

The project has two pieces that intentionally disable XR in standalone builds by default:

- `Assets/NetworkAudit/BuildXRDisabler.cs` — stops XR subsystems and disables `TrackedPoseDriver`s at build startup when `disableXROnBuild = true`.
- `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` — has `Initialize XR on Startup` unchecked for Standalone.

For a VR-enabled build, revert both as described in "Pre-build checks" under "Building a Standalone Executable" above.

### Avatar arms don't follow the hand (some roles only)

- Open `Assets/AvatarHumanoid.prefab` in Prefab mode.
- Compare the `TwoBoneIKConstraint` slots of the sub-mesh corresponding to the failing role vs. the working ones.
- Confirm the Target slot points to the correct `IKTarget_*Hand` object (not to the Hint object). See bitácora `2026-08-18` for the diagnostic history.

---

## Expected Outcome

After setup, the system should:

- Run the XR environment in the headset with 3 concurrent users.
- Support hand interaction and physics for all 3 users equally.
- Sync all avatars, hands, ray visualizations, and Jenga block state via LAN.
- Capture eye tracking data locally on each machine.
- Optionally forward interaction events to the BioLab acquisition server on the LAN.
- Generate structured CSV logs for offline analysis.
