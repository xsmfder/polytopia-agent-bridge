# 接入指南

[English](USAGE.en.md) | [简体中文](USAGE.md)

## 开始使用

在[安装](INSTALL.md)完成、对应席位的本地对局已进入地图后使用以下示例。新单人局可走建局接口，已有存档可用`load_game`；CLI的`resume`查询原请求，`close`只释放SDK会话，不退出游戏。

## 恢复、保存与退出

以下命令使用PowerShell 7。程序启动到主菜单后，用新的会话文件恢复最近的单人存档：

```powershell
python agentcli.py --player 1 --session sessions/recovered-player1.json load_game --wait
python agentcli.py --player 1 --session sessions/recovered-player1.json observe --details
python agentcli.py --player 1 --session sessions/recovered-player1.json save_game --wait
python agentcli.py --player 1 --session sessions/recovered-player1.json return_to_menu --wait
python agentcli.py --player 1 --session sessions/recovered-player1.json quit_game --wait
```

`save_game`保留当前地图；`return_to_menu`先保存再回菜单；活动对局中的`quit_game`先保存、回菜单，再请求游戏正常退出，菜单中则直接请求退出。活动对局先`observe`绑定身份。自动保存不会覆盖另一热座玩家的未决回合、跳过选择框或强行处理尚未结束的动作；条件不满足会拒绝并给原因。

热座先使用`saved_games --wait`显式打开游戏原生列表，从`native.savedGames.games`读取显示的存档ID和标签，再用`load_game --mode hotseat --game-id '<GUID>' --wait`加载。加载完成的`confirmed`只证明请求的对局已恢复，`native.phase`可能仍为`handoff_overlay`、`human_turn`或`needs_decision`；需要继续现有交接或弹窗流程，不把恢复成功当作可以行动。

每次管理操作分配`operationId`。`submitted`和`quitScheduled`都不是完成；`wait_lifecycle <原始回执JSON> --timeout 30`或同一持久会话的`resume`只查询原操作。即使原回复丢失，也用会话中先行保存的operationId查询，不重发。退出必须有可信的原进程结束证据，传输断开和超时均不算退出成功；`exitCause=not_verified`表示没有诊断进程是正常结束还是崩溃。

最终退出进度会在调用原生Quit前落盘到`lifecycle-receipts/`，因此原进程已结束后也能查询，初始回执停在saving或returning_to_menu不会要求重新提交。只接受operationId、原PID、启动标记一致的文件，SDK还核对受控席位；仍须证明原进程结束，不能只凭文件或断线确认退出。缺失或损坏的文件和不可读的进程状态保留未知。此目录属于本机运行数据，不提交源码仓库。

保存确认依据原生保存调用后的目标文件写入变化，并保持原生验证开启；不会假装void回调返回就是写盘成功。若文件未变、读写失败或出现保存错误弹窗，不继续回菜单或退出。它不等价于操作系统断电耐久性保证。

恢复存档时使用全新SDK会话，防止旧episode、票据和敌军记忆跨读档复用。启动后固定席位保持不变，读档不会替你切换受控玩家。旧`GameManager.ResumeSingleplayerGame`及热座恢复通道仍是底层实现；正式SDK不需要调用raw/audit命令。

## Python SDK

```python
from sdk import PolytopiaSDK

with PolytopiaSDK(player_id=1, session_path="sessions/player1.json") as sdk:
    observation = sdk.observe(details=True)
    if observation.get("status") == "observed":
        candidates = sdk.list_actions("all")
        eligible = [row for row in candidates.get("actions", [])
                    if row.get("eligible") is True]
        # Agent根据observation自行选择eligible中的动作；此示例不自动执行。
        for row in eligible:
            print(row["actionId"], row["args"])
```

选定本次候选`chosen`后调用`sdk.execute(chosen, wait=True, timeout=20)`。查询和执行会重新验证对局身份与版本，不能把旧描述符当作永久动作ID。

## 持久会话与恢复

CLI每次是新进程，同一个对局使用同一`--session`文件：

命令行输出为UTF-8。PowerShell 7可直接使用下例；PowerShell 5.1先设置当前窗口的原生输出解码，避免本地化部落名乱码，也不要用其默认UTF-16的`>`写JSON：

```powershell
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
```

```powershell
python agentcli.py --player 1 --session sessions/player1.json observe --details
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope all | Set-Content -Encoding UTF8 actions.json
python agentcli.py --player 1 --session sessions/player1.json execute actions.json '<实际actionId>' --wait
python agentcli.py --player 1 --session sessions/player1.json resume --timeout 30
```

`resume`对应Python的`resume_pending()`，只查询原写入，不重复操作。确认成功后重新观察再作下一次决定。不要删除未决会话来解锁；游戏重启、保存恢复或换局后，新作用域使用新会话，保留旧收据。

| 状态 | 调用者应做什么 |
| --- | --- |
| observed/discovered | 读取成功或已获得候选，不代表执行过行动 |
| submitted/timeout | 保留原收据，查询原票据，不重发 |
| needs_resolution | 观察真实弹窗或升级候选，明确选择后续查原父票据 |
| confirmed | 请求的实际效果已确认，仍需检查领域结果中的部分覆盖 |
| unknown/not_confirmed | 未能证明实际效果；不据此假定失败或重发 |
| human_turn/handoff | 交接边界，等待受控玩家可接管阶段 |
| state_changed | 观察中状态改变，整帧重新读取 |

实际遗迹奖励读`native.ruinsOutcome`，城市奖励读`native.cityRewardOutcome`。`settlementComplete`表达流程结束，`effectConfirmation`及`rewardApplied`等表达效果；父票据和奖励子票据分别核对。

## 弹窗

先`observe()`取得真实popup，再明确调用`choose_popup(button_id, expected_popup=observation["popup"])`。参数是原生按钮ID，不保证等于数组下标；重复ID或列表不完整时不能猜按钮。外交等领域决策优先使用当前动态动作候选。

已选择的城市奖励展示与未作出的选择是不同阶段。只有观察证明奖励已选择、展示仍打开时，调用`dismiss_resolved_reward(reward, expected_popup=popup)`，然后`resume_pending()`。不会自动替agent接受和约或选择升级。

## 单人建局

`setup_status()`读取原生菜单；从其`native.actions`选择enabled且executable的actionId，调用`setup_action(action_id, expected_setup=menu, wait=True)`。每步重新读取菜单和版本，依次选择新游戏、模式、部落、设置、continue。

`needs_resolution`时处理实际开场弹窗，再用原收据`wait_setup(receipt)`确认game_ready；不要重复continue。建局是独立管理接口，不会自行替换活动对局。SDK仅暴露原生菜单选项，私人测试用的0-bot设置不属于正式建局候选。

## Pass and Play

按对应席位启动，例如`start_bridge.py start --player 2`，随后使用player 2及独立会话：

```powershell
python agentcli.py --player 2 --session sessions/player2.json watch_handoff --timeout 30
python agentcli.py --player 2 --session sessions/player2.json observe --details
```

交接器从受控暗幕开始调用正常开始回合流程，等原生回顾或待决策弹窗，不跳过人工玩家。人工回合返回world=null，接口不会代操作另一个席位。固定座位不能在运行中改变。退出SDK的with上下文只释放会话锁，不退出游戏。

## 事件与终局

`events(after_event_id, stream_id=..., limit=...)`与`notifications(...)`分别使用自己的流和游标。分页直到hasMore=false；historyGap意味着历史有缺口，重新同步观察，不能补发游戏动作。

邻近隐身威胁事件只指己方探测单位。敌军记忆记录最后见到的状态，当前不可见不等于死亡；提示true不说明隐藏敌人数、位置或身份。通知captureComplete=false或unreadableNoticeCount非零也不能理解为没有通知。

```powershell
python agentcli.py --player 1 --session sessions/player1.json score_snapshot
python agentcli.py --player 1 --session sessions/player1.json result
```

进行中分数与冻结终局结果分开。保留原生单位和分项，Domination百分点评级不要当成0到1概率。字段、错误码及其他公开方法见[COMPATIBILITY.md](COMPATIBILITY.md)。
