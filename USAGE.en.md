# SDK and CLI usage

[中文](USAGE.md) · [Installation](INSTALL.en.md) · [Fields and boundaries](COMPATIBILITY.en.md)

## Observe, discover and act

```python
from sdk import PolytopiaSDK
with PolytopiaSDK(player_id=1, session_path='sessions/player1.json') as sdk:
    observation = sdk.observe(details=True)
    candidates = sdk.list_actions('unit', unit='7')
    # The caller must choose an eligible action for the actual position.
    chosen = next(a for a in candidates['actions'] if a['eligible'] is True)
    receipt = sdk.execute(chosen, wait=True, timeout=20)
    if receipt['status'] == 'needs_resolution':
        print(sdk.resolve_pending(timeout=10))
```

Descriptors bind to `episodeId/playerId/streamId/stateVersion`. Discover fresh candidates before actions. Reread rejected stale descriptors; never replay an accepted or timed-out write. Costs, training, skills and buildings follow current native menus. Targeted `unit/city/tile/tribe` queries avoid the cost of full-map `all` discovery.

```powershell
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope city | Set-Content -Encoding UTF8 actions.json
python agentcli.py --player 1 --session sessions/player1.json execute actions.json '<fresh actionId>' --wait
python agentcli.py --player 1 --session sessions/player1.json resume --timeout 30
```

`resume` queries original saved receipts; it does not load a save. `unknown/not_confirmed/timeout` are not success. An explicitly acknowledged settled unknown effect remains unknown in the historical receipt.

## Chained city upgrades and notifications

`sdk.resolve_pending(timeout=10, max_steps=24)` is a bounded driver. It acknowledges known single-button notices, closes selected reward displays proven by the current city's native records, requests the next owned pending display, and queries original parent/child tickets. It leaves actual city-reward, diplomatic and multiple-button choices to the caller. For `needs_resolution`, inspect `nextStep` and `observation`:

- `choose_city_reward`: discover fresh `list_actions('city')`, explicitly execute a real offered reward, then call the driver again.
- `choose_popup`: review buttons and call `choose_popup(index, expected_popup=...)` explicitly.
- `observe`: reread after the UI/queue changes; the same state is not advanced repeatedly.
- `query_original_receipt/reconcile_original_request`: retain the session and reconcile the original ticket/request without replaying it.

`ready` requires a fresh actionable world and no pending receipts in this session. A UI callback is not promoted to confirmed gameplay. `steps` records operations. Older Bridge versions without `rewardDisplay` proof cause a stop rather than a guess.

For one explicit queue request, call `advance_pending_popup(expected_observation=...)` with a fresh SDK observation. It requires an owned visible turn, no current popup, a known positive owned pending count and matching binding. This is a UI write with durable intent before dispatch. Lost replies and callback exceptions remain unknown and are not automatically retried.

```powershell
python agentcli.py --player 1 --session sessions/player1.json resolve_pending --timeout 10
python agentcli.py --player 1 --session sessions/player1.json observe | Set-Content -Encoding UTF8 current.json
python agentcli.py --player 1 --session sessions/player1.json advance_pending_popup current.json
```

## Attack/movement previews and results

`attack_preview(unit='7', at='(4,5)')` reads legal native targets and damage. Exida's `compositeEffects.effect=PoisonSplash` predicts poison for visible hostile neighbors, with zero neighboring HP damage on that branch. Primary-target damage is separate. `newlyPoisoned=null` avoids inspecting private enemy effects.

`composite_preview(unit='7', at='(4,5)', kind='move')` returns the native path and conditional destination Stomp/Tentacles damage. `terrainEffects` adds Island `wouldQueueAlgaeSpawn` predictions along the path, including its origin. These predict native queue entries; they execute no builds and simulate no future growth, population or complete terrain conversions. Unexplored path tiles expose legal coordinates only, with null terrain/algae predictions. Reobserve and query original receipts after actions because an earlier area effect may invalidate the next target.

Read natural terminal truth through frozen `sdk.result()`. Ordinary `game_info` may reject reads after the controllable turn ends. Unit disappearance alone cannot identify a kill or its attacker. Negative retaliation sentinels remain unknown rather than zero damage.

## Saves and hotseat

`saved_games`, `load_game`, `save_game`, `return_to_menu` and `quit_game` are separate administration operations. Query their original operation with `wait_lifecycle`. After process/save/episode changes, use a new session and preserve old receipts.

Use `handoff_status()` and `watch_handoff()` for hotseat. Unknown identity or a curtain permits public handoff metadata only. The controlled seat is fixed at process startup; stop writing while a human operates another seat.

Raw `call/audit` diagnostics are outside the normal agent path. Ordinary operation needs no screenshots, mouse simulation or hidden-save decoding.
