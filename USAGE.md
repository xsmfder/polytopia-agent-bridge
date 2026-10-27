# SDK 与 CLI 使用

[English](USAGE.en.md) · [安装](INSTALL.md) · [字段与边界](COMPATIBILITY.md)

## 观察、候选与动作

```python
from sdk import PolytopiaSDK
with PolytopiaSDK(player_id=1, session_path='sessions/player1.json') as sdk:
    observation = sdk.observe(details=True)
    candidates = sdk.list_actions('unit', unit='7')
    # 由调用者根据当前局势选择 eligible=True 的候选，勿照抄历史 actionId。
    chosen = next(a for a in candidates['actions'] if a['eligible'] is True)
    receipt = sdk.execute(chosen, wait=True, timeout=20)
    if receipt['status'] == 'needs_resolution':
        resolution = sdk.resolve_pending(timeout=10)
        print(resolution)
```

候选绑定 `episodeId/playerId/streamId/stateVersion`。每次动作前重新发现；旧候选拒绝后可重新读取，但超时或受理后的原动作不能重发。资金、训练、技能和建筑以当前原生菜单为准。定向 `unit/city/tile/tribe` 查询可减少整图 `all` 发现的调用量。

```powershell
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope city | Set-Content -Encoding UTF8 actions.json
python agentcli.py --player 1 --session sessions/player1.json execute actions.json '<本次候选actionId>' --wait
python agentcli.py --player 1 --session sessions/player1.json resume --timeout 30
```

`resume` 只续查持久会话的原票据，不是读档。`unknown/not_confirmed/timeout` 不自动变成成功；对已结算但效果未知的票据，只有调用者明确采用 `acknowledge_settled_unknown` 后才继续，且仍不改写历史效果结论。

## 连续升级与通知

`sdk.resolve_pending(timeout=10, max_steps=24)` 是有界推进流程：确认已知的单按钮通知，关闭由当前城市原生奖励记录证明已选择的展示，显示下一项本方待处理弹窗，续查原父/子票据。它不替玩家选择城市奖励、外交选项或多按钮弹窗。返回 `needs_resolution` 时检查 `nextStep` 与 `observation`：

- `choose_city_reward`：用新鲜 `list_actions('city')` 取得实际候选，显式选择并 `execute`，再调用本流程。
- `choose_popup`：审阅当前按钮，以 `choose_popup(index, expected_popup=...)` 明确回应。
- `observe`：等界面/队列变化后重新读取；同一状态下不会重复推进。
- `query_original_receipt/reconcile_original_request`：保留会话，查询原票据或原请求回执，勿重发。

`ready` 表示本次处理后新鲜世界允许操作且本会话已无未决票据；不把某次 UI 回调直接升级为动作效果确认。每步详情在 `steps` 内。旧版 Bridge 不提供 `rewardDisplay` 证明时流程会停下，不猜测。

只想显式显示下一项队列，可将新鲜 SDK 观察传给 `advance_pending_popup(expected_observation=...)`。要求本方可见回合、当前无弹窗、已知待处理数量大于零，且版本绑定不变。该调用是 UI 写入，会先持久化意图；回包丢失或回调抛错后保持未知，不自动重试。

```powershell
python agentcli.py --player 1 --session sessions/player1.json resolve_pending --timeout 10
python agentcli.py --player 1 --session sessions/player1.json observe | Set-Content -Encoding UTF8 current.json
python agentcli.py --player 1 --session sessions/player1.json advance_pending_popup current.json
```

## 攻击、移动预览及结果

`attack_preview(unit='7', at='(4,5)')` 返回当前原生合法目标与伤害。Exida 的 `compositeEffects.effect=PoisonSplash` 对已知可见邻军预测中毒，周围目标该分支伤害为0；主目标伤害另列。`newlyPoisoned=null`，不读取外方私人状态来证明“新中毒”。

`composite_preview(unit='7', at='(4,5)', kind='move')` 返回原生路径与落点 Stomp/Tentacles 条件伤害。`terrainEffects` 补充 Island 沿路径的 `wouldQueueAlgaeSpawn`，包括起点。它预测原生动作入队，不执行建造，也不模拟未来生长、人口和所有地形转换。未探索路径地块只给合法路径坐标，相应地形和造藻值为 null。行动后重新观察和查询原票据，避免前一步范围效果已改变后一步目标。

自然终局通过 `sdk.result()` 读取冻结结果；终局普通 `game_info` 可能因非可控回合拒绝。单位消失只表示离开观察或被移除，击杀归因仍需原生动作证据。负数反击哨兵保持 unknown，不当作免伤。

## 保存与热座

`saved_games`、`load_game`、`save_game`、`return_to_menu`、`quit_game` 属于独立管理操作，使用原操作 `wait_lifecycle` 续查。进程、存档或 episode 切换后使用新会话，保留旧会话和原票据。

热座以 `handoff_status()`、`watch_handoff()` 交接。身份或遮罩不明时只读公开交接状态。受控玩家固定在进程启动时；用户操控其他席位时暂停写入。

底层 `call/audit` 诊断不属于普通代理路径；普通使用不需要截图、鼠标模拟或读取隐藏存档。
