# Polytopia Agent Bridge

[English](README.en.md) | [简体中文](README.md)

[Repository](https://github.com/xsmfder/polytopia-agent-bridge) · [Issues](https://github.com/xsmfder/polytopia-agent-bridge/issues) · [CI](https://github.com/xsmfder/polytopia-agent-bridge/actions)

A structured local interface for observing and playing **The Battle of Polytopia** from an external program. It consists of an in-game C# mod, a Python SDK, and a JSON CLI. Communication uses local file queues; the game keeps its normal visible window.

The agent decides where to move, what to research or build, and when to attack or negotiate. The bridge reads player-accessible information, discovers current native actions, submits commands, and reports their actual effects. It includes no combat policy, trained model, or model API calls.

## Features

| Area | Information and operations |
| --- | --- |
| Observation | Explored tiles, visible units, own resources and technologies, city population and production, tasks, and public rankings; terrain style is separate from territorial ownership |
| Action discovery | Current native candidates, arguments, targets, and eligibility for movement, combat, research, training, construction, and unit abilities; no fixed exhaustive ability list |
| Execution | Original receipts, effect confirmation, parent/child interruptions such as city upgrades, and recovery without automatically resubmitting timed-out commands |
| Cities and economy | Construction, destruction, terrain changes, upgrade rewards, neighboring-building counts, and observed population/production/score changes |
| Previews | Legal attack previews, own movement effects, visible enemy attributes, and limited threat geometry; predictions remain separate from actual results |
| Notifications | Player-visible changes, diplomacy, nearby invisible-threat hints, and paginated events with explicit gaps and unknown values |
| Local hotseat | A fixed controlled seat, takeover from the handoff curtain, and suspended private observation during the human player's turn |
| Game lifecycle | Native save loading, saving, save-and-return-to-menu, normal quit requests, and original-operation queries |
| Final feedback | Native-readable Perfection scores, Domination ratings and components, with frozen final results separate from live statistics |

The normal decision loop is `observe → list_actions → execute → wait_result / resume → observe`. The caller resolves choices; native game rules determine costs and eligibility.

## Supported environment

- Windows x64, local single-player and Pass and Play, using Steam's official offline mode. Online play and online-login launch are outside the supported scope.
- Checked game build: **2.17.3.16375**, Unity **6000.3.17f1**, MelonLoader **0.7.3 Open-Beta**. Revalidate after game updates; matching names do not establish compatibility.
- Python **3.10+**; the SDK uses only the standard library. Native builds and regression tests additionally require local game/loader references and the Windows .NET Framework compiler.
- The hotseat seat is fixed at process startup. Loading a save does not change the controlled player.

See [compatibility and interface conventions](COMPATIBILITY.en.md) for details.

## Information boundaries

Observations are limited to information available to the controlled player. Unexplored terrain, hidden enemies' current positions, and resources not yet unlocked by technology are not published early. Enemy memory freezes at the last visible observation; disappearance does not imply death or permit hidden updates.

Native public exceptions have separate representations. A siege fire in fog exposes only its coordinate and siege flag, not city level, participants, or eventual ownership. An Elyrion rainbow identifies that tribe's visible ruin marker without revealing the tile or its neighbors. Nearby invisible-threat hints identify only the detecting friendly unit and a boolean hint, not the hidden enemy's location or count. Threat estimates do not run enemy pathfinding through hidden infrastructure.

`unknown`, `null`, and `partial` are different from zero, false, and a complete empty list. A discovered candidate does not imply that every execution or conditional effect is supported. Cymanti, ice breaking, complete special-tribe coverage, and live ruins-to-city validation are not included in the first release's accepted coverage.

## Installation

**Install MelonLoader yourself from its [official project](https://github.com/LavaGang/MelonLoader), using the checked [0.7.3 release](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3). This project does not bundle or automatically download the loader, game files, or generated dependencies, and does not modify platform authentication.**

Initial loader setup may download dependencies. Local offline gameplay does not mean that a fully disconnected first installation is supported. First confirm that your original game starts normally through Steam's official offline mode.

Place the repository contents in `<game>/.offline-work/agent-bridge`. See the [installation guide](INSTALL.en.md) for dependencies, the exact directory layout, building, deployment, rollback, and troubleshooting. Run these commands from the source directory:

```powershell
$gameRoot = 'C:\Games\Polytopia'  # Set your actual game directory
python tools/verify_bridge.py --all --game-root $gameRoot --out v1
python tools/deploy_bridge.py plan --evidence v1/verification.json
python tools/deploy_bridge.py install --evidence v1/verification.json
python tools/start_bridge.py start --player 1
```

Inspect verification and plan output before installing. Close the game before deployment. Use a new output name if `v1` exists. Keep the returned `completed.json` installation record for rollback.

## Usage

Create a solo match through the setup interface, or load a local save with `load_game`. `save_game` saves without leaving the map; `return_to_menu` saves and returns to the menu; `quit_game` saves an active match before requesting normal exit. Lifecycle operations have their own `operationId` and are queried with `wait_lifecycle` or the persistent session's `resume`. The CLI command `resume` is not a save-loading command.

These PowerShell 7 examples use player 1 and one persistent session. A loaded save needs a new session file; never carry an old episode binding into a load. PowerShell 5.1 encoding instructions and full examples are in the [usage guide](USAGE.en.md).

```powershell
python agentcli.py --player 1 --session sessions/player1.json observe --details
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope all | Set-Content -Encoding UTF8 actions.json
python agentcli.py --player 1 --session sessions/player1.json execute actions.json '<actionId from these candidates>' --wait
python agentcli.py --player 1 --session sessions/player1.json resume --timeout 30
```

Choose a current candidate with `eligible=true`. `submitted` or `accepted` means acceptance, not successful completion. Use the original receipt's `confirmed` status and domain effects. After timeout or disconnection, query the original operation instead of resending it. Keep one writer per match and avoid simultaneous manual and interface actions.

The diagnostic channel is not a remote permission sandbox. Do not expose it directly as a network service.

## Repository and development

- `src/`: in-game bridge, projections, native commands, and settlement.
- `sdk.py`, `sdk_*.py`, `agentcli.py`: public Python SDK and JSON CLI.
- `bridge_transport.py`: local queues; `observation.py` and `sightings.py`: normalized observations and enemy memory.
- `tests/`: C#/Python regression and minimal frozen fixtures.
- `tools/`: verification, deployment/rollback, visible startup, archiving, and explicit source export.

The repository excludes game assets, decompiled dumps, personal saves, runtime queues, and generated local references. See [source provenance](SOURCE_PROVENANCE.en.md).

```powershell
python -m unittest discover -s tests -p "test*.py" -v
python tests/fixtures/recovery/test_settled_unknown.py
python -m unittest discover -s tools -p "test*.py" -v
```

Native regression uses the `verify_bridge.py --all` command above and your own game installation. CI runs tests that do not require the game; a workflow file alone is not evidence of a successful GitHub run. See [contributing](CONTRIBUTING.en.md).

Project code is licensed under [MIT](LICENSE). Game content and external components retain their own rights and licenses. Publication status and remaining acceptance gates are tracked in the [release checklist](RELEASE_CHECKLIST.en.md).
