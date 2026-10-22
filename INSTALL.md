# 安装、构建与回退

[English](INSTALL.en.md) | [简体中文](INSTALL.md)

## 准备游戏和加载器

需要Windows x64、自己的正常游戏安装及可用的Steam登录缓存。先用Steam官方离线模式启动原版游戏，确认能进入主菜单，再正常退出。本项目不处理原版启动或平台认证问题，在线启动和其他系统不在当前支持范围。

**MelonLoader由用户自行安装，本项目不附带、代装或自动下载加载器。** 从[官方项目](https://github.com/LavaGang/MelonLoader)获取当前已核对的[0.7.3 Open-Beta](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3)，关闭游戏后按官方安装器或[手动安装说明](https://github.com/LavaGang/MelonLoader#how-to-manually-use-melonloader)操作，目标选游戏的`Polytopia.exe`。

IL2CPP运行时按[上游依赖说明](https://github.com/LavaGang/MelonLoader/tree/v0.7.3#requirements)准备，上游列出.NET 6 Desktop Runtime x64。它与下文编译时使用的.NET Framework工具是两项不同依赖。可用`dotnet --list-runtimes`查看现有运行时；本机验收所用0.7.3包的`MelonLoader/net6/MelonLoader.runtimeconfig.json`实际请求`Microsoft.NETCore.App 6.0.0`，允许同一6.0系列的补丁更新，安装6.0.36且没有6.x Desktop Runtime时也启动成功。这是该包与本机的实测条件，不将其扩大为所有加载器版本的最低要求；仅安装.NET 7也不能据此认定满足6.0要求。

手动安装应以对应官方Release包内容为准。本次核对的0.7.3 x64包包含`version.dll`和`MelonLoader/`，没有单独的`dobby.dll`；不要为满足通用说明从其他来源另找DLL。

先启动一次加载器并等到主菜单，完成依赖生成后退出。首次过程可能联网下载依赖，“离线对局”不表示完全断网首装已经可用。

确认游戏目录存在：

```text
Polytopia.exe
GameAssembly.dll
MelonLoader/net472/MelonLoader.dll
MelonLoader/net472/0Harmony.dll
```

`net472`引用是当前构建入口的要求，游戏运行时另由MelonLoader处理。缺文件时先完成对应加载器安装，不要把本机依赖上传到源码仓库。

## 放置源码并构建

准备Python 3.10或以上，以及Windows .NET Framework编译器；源码目录、输出目录和当前用户的`$env:TEMP`需要可写。受限工具中的Low完整性令牌可能影响程序集加载和临时文件访问，应使用正常用户权限完成构建和启动。

```powershell
python --version
Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
```

将源码选集放到以下位置，避免再套一层目录：

```text
<游戏目录>/
  Polytopia.exe
  Mods/
  .offline-work/agent-bridge/
    src/
    tests/
    tools/
    sdk.py
    agentcli.py
```

游戏内队列当前固定在这个相对路径，Python默认连接自身目录中的队列。不要从另一个独立克隆期待自动连接游戏。

```powershell
$gameRoot = 'C:\Games\Polytopia'  # 修改为实际路径
Set-Location (Join-Path $gameRoot '.offline-work\agent-bridge')
python tools/verify_bridge.py --all --game-root $gameRoot --out v1
```

验证器编译Bridge并运行C#/Python、冻结恢复和工具测试，生成`v1/verification.json`；不会启动游戏、部署DLL或读取实机对局。必须检查成功状态和输入一致性，再部署。输出目录使用新的短名称，已有v1则改用v2；本机引用和输出不提交。

## 安装Bridge并启动

保持游戏关闭，先看计划再安装：

```powershell
python tools/deploy_bridge.py plan --evidence v1/verification.json
python tools/deploy_bridge.py install --evidence v1/verification.json
python tools/start_bridge.py start --player 1
```

部署工具只安装已验证的`Mods/PolytopiaAgentBridge.dll`，不安装加载器、不复制游戏程序集、不修改存档或认证。首次安装无需占位DLL，升级保留回退记录。进程状态不可读或仍有游戏实例时应停止安装，先处理原实例。

启动成功须有`ok=true`、`ready=true`、`status=ready`以及配置恢复为`restored/already_restored`。它证明Bridge加载并到达主菜单，对局仍需正常建立或恢复。

已有单人存档可用正式CLI的`load_game --wait`恢复；热座先用`saved_games --wait`打开原生存档列表，再`load_game --mode hotseat --game-id '<列表中的GUID>' --wait`。加载使用新的`--session`文件，后续先`observe`确认当前席位和可操作状态。也可通过游戏界面读档。CLI的`resume`只续查原请求，不是读档。

`save_game --wait`保存当前受控对局，`return_to_menu --wait`保存后回菜单，`quit_game --wait`保存活动对局、回菜单后请求游戏正常退出。使用同一持久会话；先通过`observe`绑定活动对局。`confirmed`保存表示原生回调后对应存档文件出现写入变化；不会读取或公开其他存档内容。退出只有客户端证明原队列所属进程已结束才确认，无法判断退出原因时不声称已经验证为无崩溃正常关闭；超时不自动重发或强制杀进程。

调用原生Quit之前，桥接先将最终进度原子落盘到`lifecycle-receipts/`。即使第一次回执仍停在回菜单阶段，之后也可延迟查询；客户端同时核对operationId、原PID、bridgeStarted和受控席位，并证明原进程已结束。文件缺失、损坏或身份不匹配不表示成功；落盘失败时停止自动退出，不通过虚报第一份回执的quitScheduled解决问题。

`start_bridge.py`仍没有`stop`命令，`restore`只恢复临时启动配置，不会停止游戏。SDK的`close()`只释放会话。部署或回退前正常保存、退出，并等待进程结束，具体命令见[接入指南](USAGE.md)。

热座按实际受控玩家启动，例如`--player 2`。席位只在启动时加载，工具临时配置后还原磁盘文件；运行中不会替换玩家，也不应同时启动第二个游戏实例。

## 回退

保留install输出中`record`指向的`completed.json`，正常保存并退出游戏后运行：

```powershell
python tools/deploy_bridge.py plan --record '<本次completed.json的实际路径>'
python tools/deploy_bridge.py rollback --record '<本次completed.json的实际路径>'
```

首次安装回退只移除本次未被修改的Bridge DLL，升级回退恢复对应旧DLL。不要删除整个Mods或UserData目录。MelonLoader本身的卸载另按上游说明进行。

## 排错

| 现象 | 核对方向 |
| --- | --- |
| 原版也不能启动 | 先恢复正常Steam官方离线启动，Bridge不处理认证 |
| 加载器卡住或下载失败 | 查看MelonLoader日志和上游依赖，首次初始化可能联网 |
| `0x80131515` / “从网络位置加载程序集” | 分别检查下载文件的`Zone.Identifier`和文件的强制完整性标签；二者不是同一种标记。见下文，不直接修改程序集加载策略 |
| 临时目录写入被拒 | 检查当前用户对`$env:TEMP`的写权限、进程令牌和具体失败路径；换用正常用户的可写临时目录后重新验证 |
| 错误1455 | 检查Windows内存/分页文件及提交额度，参考[Microsoft说明](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-) |
| Bridge未ready | 检查加载器日志、版本、PID、席位和队列路径；超时后检查原实例，不重复启动 |
| 游戏更新后字段不可用 | 对照[兼容说明](COMPATIBILITY.md)，保留unknown/partial并重新验证 |

仅在确认下载来源和SHA256后，可检查并解除该压缩包的互联网下载标记，再解压到新的目标目录：

```powershell
$sourceZip = 'C:\Downloads\polytopia-agent-bridge-source.zip'  # 改为已核验的包
Get-Item -LiteralPath $sourceZip -Stream Zone.Identifier -ErrorAction SilentlyContinue
Unblock-File -LiteralPath $sourceZip
```

[`Unblock-File`](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file)移除下载标记，不修改Low/Medium完整性标签。若仍失败，用`icacls '<日志中被拒绝的实际文件路径>'`只读检查权限和标签；本机DSH验收遇到Low标签，恢复到正常用户Medium环境后全量验证通过。不要把整个游戏目录递归改权限作为默认安装步骤，也不要为绕过沙箱直接降低系统策略。修正环境后使用新的输出目录重跑验证，保留失败日志。

此前源码选集已在本机独立原版安装目录完成官方离线首装、单人核心动作、人工保存恢复、Bridge回退与重装。后续生命周期验收是手动替换源码后的覆盖升级，不是再次从零安装；不能自动沿用旧候选的完整首装结论。该目录仍共享当前Windows用户的游戏存档；完全断网首装、其他机器和完整卸载加载器未由这些验收证明。
