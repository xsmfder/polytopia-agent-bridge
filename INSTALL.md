# 安装、构建与回退

[English](INSTALL.en.md) · [使用指南](USAGE.md)

当前包版本 **0.4.0**。运行包、源码/SDK包用途不同：`.polymod` 是游戏内运行组件，源码 ZIP 包含 Python SDK 和本机构建入口。游戏与加载器由用户从官方渠道安装，不随本项目分发。

## 推荐：PolyMod

1. 退出游戏，按 [PolyMod 官方说明](https://github.com/PolyModdingTeam/PolyMod)安装。已核对组合是 PolyMod 1.2.18、BepInEx 6.0.0-be.785、Windows x64 游戏 2.17.3.16375。
2. 核对 `.sha256` 文件，将 `agent_bridge-0.4.0.polymod` 放入游戏根目录的 `Mods`。该文件本身是 ZIP；无需解压，不要再保留一个同 id 的旧版包或文件夹。
3. PolyMod/BepInEx 使用根目录 `winhttp.dll` 启动代理。本项目的 MelonLoader 回退使用 `version.dll`；同一时间只启用一条加载链。将停用代理备份到游戏目录之外，保留原有配置以便回退。
4. 第一次初始化可能需要联网获取依赖。通过 Steam 正常启动并等待主菜单，确认加载器显示 **Polytopia Agent Bridge 0.4.0**，ready 信息含 `bridgeVersion=0.4.0`、`loader=PolyMod`。

包结构和清单要求见 [PolyMod 发布准备](POLYMOD_PUBLISH.md)。本版本没有修改原版规则；`client=true` 不表示已经验收联网对战。

## SDK 与启动

Python 3.10+，无额外 pip 依赖。将源码 ZIP 内的项目内容放到 `<游戏目录>/.offline-work/agent-bridge/`，使 `sdk.py`、`agentcli.py` 与运行队列在同一目录。只使用预编译运行包时，至少保留 Python 模块；构建、测试、报告和备份可放在其他盘。

从该源码目录用普通用户权限的 PowerShell 执行：

```powershell
python --version
python tools/start_bridge.py start --player 1 --via steam --timeout 300
python agentcli.py --player 1 --session sessions/player1.json observe --details
```

受控席位在启动时固定；不要在同一进程改成另一玩家。Steam 可能因同步或着色器处理延迟启动；超时后先核对原次启动，不重复点击。控制台文字选择可能暂停进程输出，标题若以“选择”开头，按 Esc 退出选择。

已有游戏时先检查当前 ready 与真实请求，避免再启动。多人热座创建仍由游戏菜单完成，接口可以接管既有席位和交接。一个对局保持一个写入者，勿与手动操作同时发动作。

源码在游戏目录之外时，可显式指定队列路径：

```python
from bridge_transport import Bridge
from sdk import PolytopiaSDK
bridge = Bridge(work=r'C:\Games\Polytopia\.offline-work\agent-bridge')
with PolytopiaSDK(bridge, player_id=1, session_path='sessions/player1.json') as sdk:
    print(sdk.observe())
```

## 从源码构建 PolyMod

使用 PowerShell 7（提供 Roslyn 程序集），先通过同源完整回归，再构建 net6.0。输出目录必须是新的目录，历史证据不会覆盖。

```powershell
$gameRoot = 'C:\Games\Polytopia'
python tools/verify_bridge.py --all --game-root $gameRoot --out v1
pwsh -File tools/build_polymod.ps1 -GameRoot $gameRoot -OutputDirectory build-polymod
python tools/package_polymod.py --build build-polymod/build.json --verification v1/verification.json --out dist-polymod
python tools/package_polymod.py --build build-polymod/build.json --verification v1/verification.json --verify-directory dist-polymod
```

构建读取用户本机 `dotnet`、`BepInEx/core`、`BepInEx/interop` 和 `BepInEx/plugins/PolyMod.dll`。这些依赖只用于引用，不放进本项目包。打包器核对完整回归、全部 C# 来源、构建输入和当前输入哈希。

## MelonLoader 回退

独立安装 [MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3)，完成一次依赖生成。其 IL2CPP 环境使用 .NET 6 Desktop Runtime；C# 回退构建另引用本机 `MelonLoader/net472/MelonLoader.dll` 和 `0Harmony.dll`。退出游戏后停用 PolyMod 的启动代理，启用该加载链的 `version.dll`，并将同版本 `PolytopiaAgentBridge.dll` 放入 `Mods`。

```powershell
python tools/deploy_bridge.py plan --evidence v1/verification.json
python tools/deploy_bridge.py install --evidence v1/verification.json
```

该部署器处理 MelonLoader DLL，不会把它转换成 PolyMod 包。保留部署返回的 `completed.json`，回退按 `python tools/deploy_bridge.py rollback --help` 的参数执行。切换前核对启动代理和实际宿主，不以文件存在当作加载成功。

规则扩展模组的存档可能依赖那些模组；不能因 Bridge 有回退入口就认为卸下其他模组后存档仍可读取。备份原包、代理和设置；不删除存档或未决会话来解锁。卸载先正常退出游戏，再移出 Bridge 包；保留其他用户模组。
