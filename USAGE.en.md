# Integration guide

[English](USAGE.en.md) | [简体中文](USAGE.md)

## Getting started

Complete [installation](INSTALL.en.md) and enter the local match for the configured seat before using gameplay calls. New solo matches use the setup interface; existing saves use `load_game`. CLI `resume` queries original operations, and SDK `close` only releases the local session.

## Load, save, and exit

These PowerShell 7 commands restore the latest solo save from the main menu using a new session file:

```powershell
python agentcli.py --player 1 --session sessions/recovered-player1.json load_game --wait
python agentcli.py --player 1 --session sessions/recovered-player1.json observe --details
python agentcli.py --player 1 --session sessions/recovered-player1.json save_game --wait
python agentcli.py --player 1 --session sessions/recovered-player1.json return_to_menu --wait
python agentcli.py --player 1 --session sessions/recovered-player1.json quit_game --wait
```

`save_game` stays on the current map. `return_to_menu` saves before leaving it. `quit_game` saves an active match, returns to the menu, and requests normal exit; from a menu it requests exit directly. First observe an active match to bind its identity. These operations do not bypass another player's hotseat turn, unresolved choices, or a busy action pipeline. Rejections explain why nothing was submitted.

For hotseat, `saved_games --wait` explicitly opens the native local game list. Read displayed IDs and labels from `native.savedGames.games`, then call `load_game --mode hotseat --game-id '<GUID>' --wait`. A confirmed load means the requested match was restored, not that a unit can act. `native.phase` may still be `handoff_overlay`, `human_turn`, or `needs_decision`; continue the appropriate handoff or popup flow.

Every lifecycle operation has an `operationId`. `submitted` and `quitScheduled` do not mean completion. `wait_lifecycle <original receipt JSON> --timeout 30`, or `resume` with the same persistent session, queries the original operation without resending it. The session stores the operation ID before writing, so even a lost reply can be queried.

The bridge writes final quit progress to `lifecycle-receipts/` before invoking Quit. This lets a later CLI process query the original operation after the game has already exited, even if the initial reply stopped at `returning_to_menu` or `saving`. The transport accepts only the matching operation, queue-owner PID and process-start marker; the SDK also checks the controlled seat. A durable intent alone is not exit: the original process must also be proven gone. Missing/malformed records, access-denied probes, or transport failure do not become success. `exitCause=not_verified` means normal exit versus crash was not diagnosed independently. Keep runtime receipts with the local installation; do not commit them.

A confirmed save requires a changed, nonempty target save file after the native save callback, with native validation enabled. A void return alone is insufficient. Unchanged files, read/write failures, or a save-error popup prevent automatic return/quit. This is not a power-loss durability guarantee.

Load into a new SDK session so old episodes, tickets, and enemy memory are not reused. Loading does not change the fixed startup seat. Existing native solo/hotseat recovery remains the underlying implementation; public callers need no raw/audit command.

## Python SDK

```python
from sdk import PolytopiaSDK

with PolytopiaSDK(player_id=1, session_path="sessions/player1.json") as sdk:
    observation = sdk.observe(details=True)
    if observation.get("status") == "observed":
        candidates = sdk.list_actions("all")
        eligible = [row for row in candidates.get("actions", [])
                    if row.get("eligible") is True]
        # Your agent chooses from these candidates; this example does not act.
        for row in eligible:
            print(row["actionId"], row["args"])
```

After selecting a current candidate `chosen`, call `sdk.execute(chosen, wait=True, timeout=20)`. Discovery and execution revalidate match identity and version; an old descriptor is not a permanent action ID.

## Persistent sessions and recovery

Each CLI invocation starts a new process. Use the same `--session` file for one match. Output is UTF-8. PowerShell 7 can run these examples directly. In PowerShell 5.1, first set native-output decoding and avoid its default UTF-16 `>` redirection for JSON:

```powershell
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
```

```powershell
python agentcli.py --player 1 --session sessions/player1.json observe --details
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope all | Set-Content -Encoding UTF8 actions.json
python agentcli.py --player 1 --session sessions/player1.json execute actions.json '<actual actionId>' --wait
python agentcli.py --player 1 --session sessions/player1.json resume --timeout 30
```

`resume` calls `resume_pending()` and queries the original write. Observe again after confirmation before deciding the next action. Do not delete a pending session to unlock it. After restart, loading, or changing matches, use a new session for the new scope and retain old receipts for reconciliation.

| Status | Caller behavior |
| --- | --- |
| observed/discovered | Read/discovery succeeded; no action completion is implied |
| submitted/timeout | Keep the original receipt and query it; do not resend |
| needs_resolution | Read the real popup or upgrade candidates, choose explicitly, then query the parent ticket |
| confirmed | The requested effect is confirmed; still inspect partial domain coverage |
| unknown/not_confirmed | The effect was not proven; do not assume failure or retry |
| human_turn/handoff | Respect the handoff boundary and wait for the controlled seat |
| state_changed | Discard and reread the whole observation frame |

Actual ruin rewards are under `native.ruinsOutcome`; city rewards use `native.cityRewardOutcome`. `settlementComplete` describes pipeline completion; `effectConfirmation` and `rewardApplied` describe effects. Check parent and reward-child receipts separately.

## Popups

Obtain the current popup with `observe()`, then explicitly call `choose_popup(button_id, expected_popup=observation["popup"])`. This is a native button ID, not necessarily an array index. Never guess from duplicate IDs or incomplete lists. Prefer current domain-action candidates for decisions such as diplomacy.

A chosen city reward's remaining display is different from an unresolved choice. Only when observation proves the reward was selected but its display remains open, call `dismiss_resolved_reward(reward, expected_popup=popup)`, then `resume_pending()`. The bridge does not choose upgrades or accept treaties on the agent's behalf.

## Solo setup

`setup_status()` reads the native menu. Choose an enabled, executable ID from `native.actions`, then call `setup_action(action_id, expected_setup=menu, wait=True)`. Reread the menu and revision at every step: new game, mode, tribe, settings, continue.

For `needs_resolution`, handle the actual opening popup, then query the original receipt with `wait_setup(receipt)` until `game_ready`. Do not submit continue twice. Setup is separate administration and does not replace an active match. Only native menu options are exposed; private zero-bot test settings are not formal setup candidates.

## Pass and Play

Start the intended seat, for example `start_bridge.py start --player 2`, and use a separate player-2 session:

```powershell
python agentcli.py --player 2 --session sessions/player2.json watch_handoff --timeout 30
python agentcli.py --player 2 --session sessions/player2.json observe --details
```

The handoff controller invokes the normal start-turn flow from the controlled seat's curtain, then waits for native recap or choices. It does not skip the human turn. Human-turn responses have `world=null`; another seat is not operated. The fixed seat cannot change during the process. Leaving a Python `with` block releases the session, not the game.

## Events and final results

`events(after_event_id, stream_id=..., limit=...)` and `notifications(...)` use separate streams and cursors. Page until `hasMore=false`. `historyGap` requires observation resynchronization, not replaying game actions.

Invisible-threat events identify friendly detecting units only. Enemy memory stores the last visible state; current invisibility does not establish death. A true hint does not reveal hidden counts, locations, or identities. `captureComplete=false` or a nonzero `unreadableNoticeCount` must not be interpreted as no notifications.

```powershell
python agentcli.py --player 1 --session sessions/player1.json score_snapshot
python agentcli.py --player 1 --session sessions/player1.json result
```

Live scores and frozen final results are separate. Preserve native units and score components. Domination percent-point ratings are not probabilities on a zero-to-one scale. See [COMPATIBILITY.en.md](COMPATIBILITY.en.md) for fields, errors, and the remaining public methods.
