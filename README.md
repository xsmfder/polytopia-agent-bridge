# Polytopia Agent Bridge

[English](README.en.md) | [简体中文](README.md)

[GitHub仓库](https://github.com/xsmfder/polytopia-agent-bridge) · [问题反馈](https://github.com/xsmfder/polytopia-agent-bridge/issues) · [CI](https://github.com/xsmfder/polytopia-agent-bridge/actions)

让外部程序通过结构化接口观察和操作《The Battle of Polytopia》的本地对局。项目由游戏内C# Mod、Python SDK和命令行工具组成，通过本地文件队列通信，游戏保留正常可见窗口。

Agent负责根据观察决定移动、攻击、研究、建设和外交；Bridge负责读取游戏提供的信息、发现当前合法动作、提交命令并报告实际结果。项目不包含自动作战策略、训练模型或模型API调用。

## 功能

| 能力 | 提供的信息或操作 |
| --- | --- |
| 对局观察 | 已探索地图、当前可见单位、己方科技与资源、城市人口与产出、任务和公开榜单；地块种族风格与领土归属分开 |
| 动作发现 | 从游戏接口读取当前候选、参数、目标和资格，覆盖移动、战斗、科技、训练、建设与单位特殊行动，不维护固定技能全集 |
| 执行与结算 | 单次提交、原票据续查、实际效果确认；城市升级等中断保留父子关系，超时不自动重发 |
| 城市与建设 | 建造、拆除、地形改造、升级奖励和邻近建筑统计；实际人口、产出与分数变化 |
| 战斗与移动预览 | 合法攻击预览、己方移动复合效果、可见敌军属性和有限威胁几何；预览与实际效果分开 |
| 通知与事件 | 玩家可见的行动变化、外交消息、隐身威胁提示和分页事件日志，保留观察缺口与未知状态 |
| 本地热座 | 固定受控席位，从交接暗幕开始接管，在人工玩家回合暂停私有观察 |
| 终局反馈 | Perfection分数、Domination评级及原生可读分项，终局冻结结果与进行中统计分开 |

典型调用循环：`observe → list_actions → execute → wait_result / resume → observe`。需要选择的弹窗交由调用者决定；游戏规则、费用和合法性以原生接口为准。

## 支持范围

- Windows x64上的本地单人和Pass and Play。Steam使用官方离线模式；在线对局与在线登录启动不在当前支持范围。
- 当前核对环境为游戏2.17.3.16375、Unity 6000.3.17f1、MelonLoader 0.7.3 Open-Beta。游戏更新后应重新验证，不能按名称相同推定兼容。
- Python 3.10及以上，SDK只使用标准库。C#构建与回归另需本机游戏、MelonLoader引用和Windows .NET Framework编译器。
- 热座席位在进程启动时固定，不能在同一进程中动态切换为另一玩家。

完整环境、schema和状态约定见[兼容性说明](COMPATIBILITY.md)。

## 信息边界

观察以受控玩家可以获得的信息为边界。未探索地形、不可见敌军当前位置和尚未通过科技解锁的资源不提前公开。离开视野的敌军只保留最后可见记录，不认定死亡，也不继续更新隐藏状态。

原生界面允许的例外独立表达：雾中城市战火只给坐标与占领标记，不附城市等级、交战双方或最终归属；Elyrion彩虹仅表示该种族可见的遗迹标记；邻近隐身威胁只关联己方探测单位和布尔提示，不给隐藏敌人的位置或数量。威胁信息不调用依赖隐藏设施的敌方真实寻路。

`unknown`、`null`、`partial`与0、false、完整空列表有不同含义。候选出现不保证所有执行和效果分支均已适配，未知能力或条件效果明确返回不可用或部分结果。Cymanti、破冰、特殊种族完整覆盖和遗迹产城实机仍不在首版已验收范围。

## 安装

**请自行从[MelonLoader官方项目](https://github.com/LavaGang/MelonLoader)安装加载器，当前已核对版本为[0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3)。本项目不打包或自动下载MelonLoader、游戏文件或生成依赖，也不修改平台认证。**

首次加载器初始化可能需要联网下载依赖；“本地离线对局”不表示支持完全断网首装。先确认自己的原版游戏能够通过Steam官方离线模式正常启动。

源码安装时，将本仓库内容放到游戏目录的`.offline-work/agent-bridge`。依赖安装、构建、部署、回退与排错步骤见[安装指南](INSTALL.md)。从源码目录运行：

```powershell
$gameRoot = 'C:\Games\Polytopia'  # 改为包含Polytopia.exe的实际目录
python tools/verify_bridge.py --all --game-root $gameRoot --out v1
python tools/deploy_bridge.py plan --evidence v1/verification.json
python tools/deploy_bridge.py install --evidence v1/verification.json
python tools/start_bridge.py start --player 1
```

先检查验证和plan输出，再执行install。部署时游戏必须关闭。`v1`必须是新的输出目录，已存在则统一改用`v2`等新名称；保留安装返回的`completed.json`用于回退。

## 使用

启动后可通过单人建局接口建立新局，或用`load_game`恢复本地存档；`save_game`保存，`return_to_menu`保存并返回菜单，`quit_game`保存活动对局后正常退出。它们是独立管理操作，通过`wait_lifecycle`或持久会话的`resume`续查；CLI的`resume`本身不是读档。以下PowerShell 7示例沿用启动时的player 1，每次调用使用同一持久会话；恢复存档使用新的会话文件，不能沿用旧对局绑定。完整说明见[使用指南](USAGE.md)。

```powershell
python agentcli.py --player 1 --session sessions/player1.json observe --details
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope all | Set-Content -Encoding UTF8 actions.json
python agentcli.py --player 1 --session sessions/player1.json execute actions.json '<从本次候选选择的actionId>' --wait
python agentcli.py --player 1 --session sessions/player1.json resume --timeout 30
```

选择`eligible=true`的当前候选。`submitted/accepted`只表示受理，实际效果以原票据的`confirmed`及领域结果为准。超时、断连或效果未知时查询原票据，不重发命令。一个对局保持一个写入者，避免手动操作与接口同时提交动作。

Python接入、弹窗、热座交接和终局读取示例见[使用指南](USAGE.md)，逐字段约定见[COMPATIBILITY.md](COMPATIBILITY.md)。底层诊断通道不属于远程权限沙箱，不应直接暴露为网络服务。

## 仓库内容与开发

- `src/`：游戏内Bridge、观察投影、原生命令与效果回执。
- `sdk.py`、`sdk_*.py`、`agentcli.py`：Python SDK与JSON CLI。
- `bridge_transport.py`：本地队列传输；`observation.py`、`sightings.py`：规范观察和敌军记忆。
- `tests/`：C#/Python回归与最小冻结夹具。
- `tools/`：验证、部署/回退、可见启动、归档和源码导出。

源码选集、排除项和依赖来源见[SOURCE_PROVENANCE.md](SOURCE_PROVENANCE.md)。仓库不包含游戏资产、反编译转储、个人存档、运行队列或本机生成依赖。

```powershell
python -m unittest discover -s tests -p "test*.py" -v
python tests/fixtures/recovery/test_settled_unknown.py
python -m unittest discover -s tools -p "test*.py" -v
```

原生完整验证另需本机游戏，使用安装章节的`verify_bridge.py --all`。CI仅运行不依赖游戏的测试，配置存在不代表已在GitHub运行。贡献方式见[CONTRIBUTING.md](CONTRIBUTING.md)。

本项目代码采用[MIT许可证](LICENSE)。游戏内容和外部组件不在该授权范围内，各自遵循其权利和许可。源码托管于上述仓库，正式发行以[Releases](https://github.com/xsmfder/polytopia-agent-bridge/releases)与tag为准；范围与验收事项见[RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md)。
