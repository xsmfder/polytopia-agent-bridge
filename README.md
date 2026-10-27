# Polytopia Agent Bridge

[English](README.en.md) · [安装](INSTALL.md) · [使用](USAGE.md) · [兼容范围](COMPATIBILITY.md)

面向本地 AI 代理的《The Battle of Polytopia》观察和原生动作接口。当前版本为 **0.4.0**，对应 GitHub tag **v0.4.0**；PolyMod 清单、MelonLoader 入口和 Bridge ready 信息使用同一版号。下载见[GitHub v0.4.0 Release](https://github.com/xsmfder/polytopia-agent-bridge/releases/tag/v0.4.0)。

Bridge 在游戏主线程读取受控玩家可获得的信息，从原生菜单提供合法候选，执行原生命令并记录动作票据。Python SDK/JSON CLI 完成 `observe → list_actions → execute → wait_result`，处理弹窗、保存恢复、热座交接及自然终局。它提供接口，需要另行接入决策代理。

维护主线是**原版规则下的观察、执行和效果确认闭环**。科技、单位、建筑及候选动作读取当前原生目录，受控扩展样例已走通普通研究、训练、建造及保存恢复。第三方新能力可能缺少效果观察和结算覆盖；不承诺任意规则通用适配，也不逐个验收第三方模组。

## 0.4.0 的变化

- PolyMod 原生 PolyScript 与 MelonLoader 薄入口复用核心，支持单进程单宿主及卸载保护。
- Aquarion、Elyrion、Polaris、Cymanti 的主要机制有实机样例，包括普通族 Drain、热座私有门禁和交接。覆盖情况及剩余边界见[兼容范围](COMPATIBILITY.md)。
- 修复连续升级中另一座城的待选奖励阻挡关闭本城已选展示的问题；提供 `resolve_pending()` 和 `advance_pending_popup()`，推进展示、续查原票据，在真正奖励选择处停下。
- 修复相遇通知辅助文本采样变化造成的误拒绝，以及已知空待处理队列被城市接口误报为不可枚举。
- Exida 毒雾预览遵循原生中毒分支；Island 移动预览补充整条路径的条件造藻队列，保留踩踏、迷雾和未来状态模拟边界。

## 安装与使用

推荐使用[官方 PolyMod](https://polymod.dev/)及 `agent_bridge-0.4.0.polymod`，将运行包放入游戏 `Mods` 文件夹。SDK 从源码包安装；C# 源码、工具和文档也在源码包内。MelonLoader 作为独立回退方案，两个加载器启动代理不能同时启用。详细步骤见[安装指南](INSTALL.md)。

```powershell
python agentcli.py --player 1 --session sessions/player1.json observe --details
python agentcli.py --player 1 --session sessions/player1.json list_actions --scope unit --unit 7
python agentcli.py --player 1 --session sessions/player1.json resolve_pending
```

`accepted/submitted` 表示受理；`confirmed` 需要原票据的效果证据。超时或未知时查询原请求，不重发动作。`unknown/null/partial` 不等于零、false 或完整空列表。热座隐藏席位不读取私有世界，未探索地形和不可见敌军不提前公开。底层诊断通道不是网络权限沙箱，不应直接暴露成远程服务。

## 环境、源码与贡献

已核对 Windows x64、游戏 2.17.3.16375（Unity 6000.3.17f1）、PolyMod 1.2.18 / BepInEx 6.0.0-be.785，以及独立的 MelonLoader 0.7.3 回退。SDK 使用 Python 3.10+ 标准库。原生构建另需用户自己的游戏和加载器依赖。

代码使用 [MIT](LICENSE)。源码包仅含项目代码、测试、最小夹具和双语文档，不含游戏、加载器、生成 interop、私人对局或运行队列。见[来源清单](SOURCE_PROVENANCE.md)、[发布清单](RELEASE_CHECKLIST.md)、[PolyMod 发布准备](POLYMOD_PUBLISH.md)及[更新公告](RELEASE_NOTES.md)。

- **xsmfder**：项目方向、信息边界、人工协同测试与验收。
- **Codex（OpenAI）**：接口实现、整合维护、回归与发布准备。
- **DSH agent**：协作开发及独立安装、实机验收。

项目采用人与 AI 代理协作开发。反馈见[GitHub Issues](https://github.com/xsmfder/polytopia-agent-bridge/issues)。
