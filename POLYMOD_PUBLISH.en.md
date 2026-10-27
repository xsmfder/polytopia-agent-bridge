# PolyMod publication requirements and preparation

[中文](POLYMOD_PUBLISH.md) · [Installation](INSTALL.en.md) · [Release checklist](RELEASE_CHECKLIST.en.md)

Checked on 2026-10-27 for PolyMod 1.2.18. Confirmed package-format requirements are separate from website submission, which has not been performed. This document does not imply catalogue acceptance or verification.

## Container and manifest

The official Wiki describes `.polymod` as a ZIP container for distribution. Put `manifest.json` and the DLL at the archive root. Current loader code also accepts directories and `.zip`; this project distributes `.polymod`. See the [official Mod documentation](https://github.com/PolyModdingTeam/PolyMod/wiki/1.-Mod) and [current loader](https://github.com/PolyModdingTeam/PolyMod/blob/main/src/Loader.cs).

```text
agent_bridge-0.4.0.polymod
  manifest.json
  PolytopiaAgentBridge.PolyMod.dll
  LICENSE
```

Do not add an enclosing project folder. `id=agent_bridge` satisfies the native lowercase-letter/underscore constraint. `version=0.4.0` uses the numeric format accepted by current `System.Version`; `v0.4.0` and `0.4.0-beta.1` must not be copied into that field. Retain name, description, authors, dependencies and client. The [manifest template](polymod/manifest.json) pins the checked game dependency. See the [current manifest definition](https://github.com/PolyModdingTeam/PolyMod/blob/main/src/Mod.cs).

`client=true` indicates that this package does not rewrite game-rule catalogs. It grants no online-operation permission and establishes no online multiplayer compatibility. Maintenance follows local vanilla rules; temporary rule-extension probes are excluded.

## PolyScript and version alignment

The Wiki's static Load example is older than the current `PolyScriptBase` Load/Unload API. This entry inherits `PolyMod.Api.PolyScript` and targets net6.0, using local BepInEx/HarmonyX and interop references. Follow the installed/current API rather than treating the older example as its only signature. See [PolyScripts documentation](https://github.com/PolyModdingTeam/PolyMod/wiki/4.-PolyScripts) and the [current API](https://github.com/PolyModdingTeam/PolyMod/blob/main/src/Api/PolyScript.cs).

`VERSION`, C# BridgeRelease, PolyMod manifest, MelonInfo, ready metadata and announcements use `0.4.0`. The GitHub tag is `v0.4.0`, with v as its prefix. AssemblyVersion is `0.4.0.0`; InformationalVersion is `0.4.0`. Use GitHub's prerelease flag rather than an unsupported manifest suffix.

## Prepared submission materials

- `agent_bridge-0.4.0.polymod` and SHA256, containing only the manifest, owned Bridge DLL and LICENSE.
- Source/SDK ZIP, source manifest, MIT license and bilingual installation, scope and announcement documents.
- Repository/version links, contributor list and Chinese/English descriptions from the documentation.
- Local regression, build provenance and candidate live-test evidence retained outside public artifacts.

Submit through the actual account and form on [polymod.dev](https://polymod.dev/). The public site/Wiki confirms packaging; this preparation did not access an authenticated submission form or establish account permissions, image requirements, size limits or moderation rules. Check current form requirements at submission and rely on the site's actual acknowledgement. No upload, submission or Verified claim has been made here.

Exclude game binaries, generated interop, loader dependencies, decompiled native dumps, private saves, runtime queues and credentials. Tools explicitly select owned components. See [compatibility](COMPATIBILITY.en.md) for historical vanilla-mechanic samples and the limits of the new-fix regression.
