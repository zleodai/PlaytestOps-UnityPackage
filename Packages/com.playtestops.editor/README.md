# PlaytestOps Editor

Editor-only Unity Package Manager integration for a separately running PlaytestOps dashboard: Edit Mode/Play Mode tests and logs, read-only authored scripts and Unity Version Control metadata, Windows build settings, and preserved Windows player builds with live logs.

## Install and support

Requires **Unity 6000.6+** and Unity Test Framework **1.8.0**. Windows build settings, player builds, and the UVCS adapter currently require a Windows Editor host. Other Unity versions and host platforms require independent validation.

In Unity's Package Manager, choose **Install package from git URL**:

```text
https://github.com/zleodai/PlaytestOps-UnityPackage.git?path=/Packages/com.playtestops.editor#v0.5.1
```

The package depends only on Unity Test Framework. Player streaming is an optional separate `com.playtestops.runtime` install. No Unity Pipeline/CLI installation is needed for dashboard pairing.

## Connect and run tests

1. Run a compatible PlaytestOps dashboard locally and open its **Editors** page.
2. Generate a pairing code. In Unity, open **Tools > PlaytestOps > Open PlaytestOps**, enter the dashboard origin and code, then connect. Match the port to your dashboard; the package's default is `http://localhost:5282`.
3. Choose **Discover / refresh tests** in Unity and refresh the dashboard. Queue a discovered test from the dashboard or run one selected case in Unity.
4. Read the result and captured debug, warning, error, assert, and exception logs. Local JSON/XML reports are stored under `Library/PlaytestOps/Runs` in the Unity project.

One Editor owns a dashboard instance at a time. Disconnect it on the dashboard's **Editors** page before switching Editors. Closing the Unity panel does not disconnect; use its **Disconnect** action to stop the connection. Editor/server restarts require pairing again; ordinary script reloads retain the session. Pairing tokens stay in Unity `SessionState`; `ProjectSettings/PlaytestOps.json` contains only a non-secret project identity.

Package Manager's **Samples** section offers **Bridge Smoke Tests** and **Connection Log Smoke Tests**. These are transport/runner placeholders, not gameplay qualification. Some samples deliberately emit expected errors or expose an explicitly selected failing test.

## Scripts, UVCS, and builds

- **Scripts:** Saved C# files are read on demand. Defaults are `Assets/Scripts` and `Assets/Tests`; configure explicit authored folders in the Unity panel. Package/library/engine/vendor/sample paths are excluded. Unsaved IDE buffers are not read or changed.
- **Version control:** Install and authenticate the UVCS client under the account running Unity. The adapter accepts `PlasticSCM5/client/cm.exe` only beneath standard Program Files locations. It reads saved pending changes, supported same-branch incoming changesets, and changeset history; no check-in, update, merge, or branch switching is performed. Unsupported workspace/selector types report incoming information unavailable.
- **Build settings:** The active Windows profile and supported Player Settings are used. Settings shared with other platforms can affect those platforms too. Save scenes and choose the intended profile in Unity first.
- **Builds:** Each request creates a unique `Builds/PlaytestOps/<build-id>` output folder. Existing output is not overwritten. Build status/logs return to the dashboard; an interrupted attempt is not a successful build.

## Safety and limits

- Use a matching dashboard release. Older packages may not understand newer dashboard requests.
- Tests/builds are rejected while Unity is busy or required scenes are unsaved. Dashboard timeouts do not cancel Unity execution. Test cancellation and automatic reruns are not implemented.
- PlaytestOps temporarily enables domain reload for owned Play Mode tests, then restores the original options. Recovery state remains in `Library/PlaytestOps` until cleanup succeeds.
- Source/VCS reads and log capture have explicit size/time bounds. Error floods and crashes can leave incomplete logs or interrupted attempts; do not treat a stale running state as success.
- Read/run/build pages are intended for the local operator. Pairing alone is not dashboard user authentication. Do not publicly expose the dashboard or confidential source/logs.
