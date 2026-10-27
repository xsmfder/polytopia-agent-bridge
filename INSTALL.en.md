# Installation, builds and rollback

[中文](INSTALL.md) · [Usage](USAGE.en.md)

The package version is **0.4.0**. The `.polymod` file is the in-game runtime; the source ZIP contains the Python SDK and local build tools. Install the game and official loader separately.

## Recommended: PolyMod

1. Close the game and follow the [official PolyMod instructions](https://github.com/PolyModdingTeam/PolyMod). The checked combination is PolyMod 1.2.18, BepInEx 6.0.0-be.785 and Windows x64 game 2.17.3.16375.
2. Verify the `.sha256` file and put `agent_bridge-0.4.0.polymod` in the game's `Mods` directory. It is a ZIP container; do not extract it or retain another installed package/folder with the same mod id.
3. PolyMod/BepInEx uses the root `winhttp.dll` bootstrap. This project's MelonLoader fallback uses `version.dll`. Enable one loading chain at a time; preserve the disabled bootstrap and its settings outside the game directory.
4. Initial setup may download dependencies. Start through Steam, wait for the main menu and check **Polytopia Agent Bridge 0.4.0** in the loader and `bridgeVersion=0.4.0`, `loader=PolyMod` in the ready document.

See [PolyMod publication preparation](POLYMOD_PUBLISH.en.md) for archive and manifest requirements. The package does not alter vanilla rules; `client=true` is not evidence of online multiplayer acceptance.

## SDK and startup

Use Python 3.10+ without additional pip packages. Put the source archive's project contents in `<game>/.offline-work/agent-bridge/`, with `sdk.py` and `agentcli.py` beside the runtime queues. Prebuilt-runtime users need the Python modules; build outputs, tests, reports and backups may remain on another drive.

From that source directory, in a normal user PowerShell:

```powershell
python --version
python tools/start_bridge.py start --player 1 --via steam --timeout 300
python agentcli.py --player 1 --session sessions/player1.json observe --details
```

The controlled seat is fixed at process startup. Steam synchronization/shader processing can delay startup; reconcile the original attempt after a timeout. Windows console text-selection mode can pause output: press Esc if its title indicates selection.

Check an existing ready document with actual requests before starting another process. Create hotseat matches through the game menu; the interface can take over an existing configured seat and hand it off. Keep one writer per match, without simultaneous manual gameplay.

For sources stored outside the game, specify the queue directory:

```python
from bridge_transport import Bridge
from sdk import PolytopiaSDK
bridge = Bridge(work=r'C:\Games\Polytopia\.offline-work\agent-bridge')
with PolytopiaSDK(bridge, player_id=1, session_path='sessions/player1.json') as sdk:
    print(sdk.observe())
```

## Build PolyMod from source

PowerShell 7 provides Roslyn assemblies. Run the full same-source regression before building net6.0. Each output directory must be new; previous evidence is preserved.

```powershell
$gameRoot = 'C:\Games\Polytopia'
python tools/verify_bridge.py --all --game-root $gameRoot --out v1
pwsh -File tools/build_polymod.ps1 -GameRoot $gameRoot -OutputDirectory build-polymod
python tools/package_polymod.py --build build-polymod/build.json --verification v1/verification.json --out dist-polymod
python tools/package_polymod.py --build build-polymod/build.json --verification v1/verification.json --verify-directory dist-polymod
```

Local `dotnet`, `BepInEx/core`, `BepInEx/interop` and `BepInEx/plugins/PolyMod.dll` supply references. They are excluded from the mod archive. Packaging checks the full regression, complete C# source set, build snapshots and current input hashes.

## MelonLoader fallback

Install [MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3) separately and let it generate dependencies once. Its IL2CPP environment uses the .NET 6 Desktop Runtime. Fallback compilation references local `MelonLoader/net472/MelonLoader.dll` and `0Harmony.dll`. With the game closed, disable the PolyMod bootstrap, enable this chain's `version.dll` and put same-version `PolytopiaAgentBridge.dll` in `Mods`.

```powershell
python tools/deploy_bridge.py plan --evidence v1/verification.json
python tools/deploy_bridge.py install --evidence v1/verification.json
```

This deployer handles the MelonLoader DLL; it does not produce a PolyMod archive. Preserve its `completed.json`; consult `python tools/deploy_bridge.py rollback --help` for rollback arguments. Verify the actual loaded host rather than inferring it from installed files.

Saves created with rule extensions may depend on those mods. A Bridge fallback does not guarantee that such saves work after other mods are removed. Preserve packages, bootstraps, settings and unresolved sessions. Uninstall with the game closed by moving out the Bridge package; retain other users' mods.
