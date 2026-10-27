# Polytopia Agent Bridge

[中文](README.md) · [Installation](INSTALL.en.md) · [Usage](USAGE.en.md) · [Compatibility](COMPATIBILITY.en.md)

A local observation and native-action interface for AI agents in The Battle of Polytopia. The current version is **0.4.0**, with GitHub tag **v0.4.0**. The PolyMod manifest, MelonLoader entry and Bridge ready document share this version. Downloads are available on the [GitHub v0.4.0 Release](https://github.com/xsmfder/polytopia-agent-bridge/releases/tag/v0.4.0).

Bridge reads information available to the controlled player on the game thread, discovers legal actions from native menus, invokes native commands and records effect receipts. The Python SDK/JSON CLI implements `observe → list_actions → execute → wait_result`, plus popups, saves, hotseat handoff and natural match results. A decision-making agent must be connected separately.

Maintenance focuses on **observation, execution and effect confirmation under vanilla rules**. Technologies, units, buildings and candidate actions come from current native catalogs. Controlled extensions have passed ordinary research, training, building and save/reload checks. Arbitrary new mod abilities may lack effect observation or settlement coverage. Universal rule adaptation and individual acceptance of every third-party mod are outside the maintenance scope.

## Changes in 0.4.0

- Native PolyMod PolyScript and a thin MelonLoader entry share the core, with one-host-per-process and unload protections.
- Live samples cover major Aquarion, Elyrion, Polaris and Cymanti mechanics, ordinary-tribe Drain, and hotseat privacy/handoff. See [compatibility](COMPATIBILITY.en.md) for coverage limits.
- Selected city-reward displays can close while another city has a pending choice. `resolve_pending()` and `advance_pending_popup()` advance displays and query original tickets, stopping at actual reward choices.
- Encounter acknowledgement no longer rejects a known auxiliary-text sampling change. A known null pending-trigger collection is consistently treated as empty.
- Exida preview follows the native poison-only splash branch. Island movement includes conditional algae-spawn queue predictions across its full path. Stomp, fog and future-state simulation limits remain explicit.

## Install and use

The recommended runtime is [official PolyMod](https://polymod.dev/) with `agent_bridge-0.4.0.polymod` in the game's `Mods` directory. Install the SDK from the source archive, which also contains C# sources, tools and documentation. MelonLoader is an independent fallback; enable only one loader bootstrap. Follow the [installation guide](INSTALL.en.md).

```powershell
python agentcli.py --player 1 --session sessions/player1.json observe --details
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope unit --unit 7
python agentcli.py --player 1 --session sessions/player1.json resolve_pending
```

`accepted/submitted` means submission; `confirmed` requires effect evidence from the original ticket. Query original requests after timeouts or unknown results, without replaying actions. `unknown/null/partial` differ from zero, false and complete empty lists. Hidden hotseat seats are not sampled; unexplored terrain and invisible enemies are withheld. Diagnostic channels are not a network permission sandbox.

## Environment, source and contributors

Checked: Windows x64, game 2.17.3.16375 (Unity 6000.3.17f1), PolyMod 1.2.18 / BepInEx 6.0.0-be.785, and a separate MelonLoader 0.7.3 fallback. The SDK needs Python 3.10+ and its standard library. Native builds require the user's own game and loader dependencies.

Project code is [MIT licensed](LICENSE). The source archive contains owned code, tests, minimal fixtures and bilingual documentation, excluding game/loader binaries, generated interop, private campaigns and runtime queues. See [provenance](SOURCE_PROVENANCE.en.md), [release checklist](RELEASE_CHECKLIST.en.md), [PolyMod publication preparation](POLYMOD_PUBLISH.en.md) and [announcement](RELEASE_NOTES.en.md).

- **xsmfder**: project direction, information boundaries, assisted testing and acceptance.
- **Codex (OpenAI)**: implementation, integration, maintenance, regression and release preparation.
- **DSH agent**: collaborative development and independent installation/live acceptance.

Development combines human and AI-agent contributions. Report issues through [GitHub Issues](https://github.com/xsmfder/polytopia-agent-bridge/issues).
