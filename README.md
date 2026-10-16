# PlaytestOps Unity packages

Install Git on your machine and make it available on `PATH`. In Unity, open **Window > Package Management > Package Manager**, choose **Install package from git URL**, and enter the URL for the package you need.

Editor integration (Unity 6000.6+):

```text
https://github.com/zleodai/PlaytestOps-UnityPackage.git?path=/Packages/com.playtestops.editor#v0.5.1
```

Optional Windows player streaming and virtual input:

```text
https://github.com/zleodai/PlaytestOps-UnityPackage.git?path=/Packages/com.playtestops.runtime#v0.5.1
```

Install the two packages separately. The Editor package does not require the runtime package. These URLs pin the `v0.5.1` distribution release (Editor 0.5.1 and runtime 0.1.0). To select another release, replace the anchor with an existing release tag or full commit hash. Commit the project's `Packages/manifest.json` and `Packages/packages-lock.json` for reproducible installs.

The dashboard application is not included. Run a compatible PlaytestOps dashboard separately. Package-specific requirements and usage are in [Editor package](Packages/com.playtestops.editor/README.md) and [runtime package](Packages/com.playtestops.runtime/README.md).

See [Unity's Git dependency instructions](https://docs.unity.com/en-us/engine/6000.6/manual/packages-list/upm-git-dependencies/upm-git) for Git authentication and updating a locked Git package.
