# PlaytestOps Runtime Streaming

Optional player-side framebuffer video, virtual keyboard/mouse input, and bounded runtime log forwarding for a compatible PlaytestOps dashboard. It is separate from the Editor integration.

## Install and requirements

Install this Git URL through Unity's Package Manager:

```text
https://github.com/zleodai/PlaytestOps-UnityPackage.git?path=/Packages/com.playtestops.runtime#v0.5.1
```

The manifest minimum is Unity 6000.0; earlier Unity 6 versions still require independent compatibility validation. Current streaming support is limited to **Windows x64 players**, a real graphics device, and a visible player window. macOS, Linux, mobile, headless players, and multiple simultaneous clients are not supported.

Dependencies are resolved from Unity's registry: WebRTC 3.0.0, Input System 1.14.0 or a compatible project-selected version, and the Screen Capture, IMGUI, and JSON Serialization engine modules. Set **Player Settings > Other Settings > Configuration > Active Input Handling** to **Input System Package (New)** or **Both** and restart Unity when prompted. Installing the dependency alone does not enable it; Windows builds are rejected when Input System is disabled.

## Use

1. Install the separate PlaytestOps Editor package and connect it to a compatible local dashboard.
2. Build a Windows x64 player from the dashboard. The package writes `playtestops-streaming.json` beside supported output so the dashboard can validate streaming capability.
3. On the dashboard's **Streaming** page, select that build, choose its window resolution, and start the stream. Default launch size is 1280 x 720; video uses the actual player framebuffer, not desktop capture.
4. Take control explicitly before sending keyboard/mouse input. Stop the session from the dashboard when finished.

Normal player launches do not start streaming. Runtime startup requires explicit, session-scoped configuration supplied by the dashboard through `PLAYTESTOPS_STREAM_CONFIG`. That configuration is consumed at startup; do not place credentials in source control or command-line arguments. Runtime signaling accepts loopback server origins only.

## Limits

- Virtual input uses Unity's Input System, not legacy `UnityEngine.Input` or operating-system input injection. Games that restrict Input Actions to particular devices/users may need an explicit integration.
- Input uses a separate reliable ordered WebRTC data channel; reliability does not guarantee zero delay or a game action for every input during stalls/disconnection.
- A diagnostic input-to-video marker is optional and visible in the captured framebuffer when enabled. Disable it for ordinary viewing.
- Runtime log capture is bounded and may truncate during an error flood. Logs can contain sensitive game data.
- The package does not provide public remote access, dashboard authentication, NAT/TURN hosting, multiplayer orchestration, or game-specific test qualification.
