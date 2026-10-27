# PolyMod 发布要求与准备

[English](POLYMOD_PUBLISH.en.md) · [安装](INSTALL.md) · [发布清单](RELEASE_CHECKLIST.md)

核对日期：2026-10-27。目标运行环境 PolyMod 1.2.18。这里区分已确认的包格式与尚未执行的网站提交；本文件不代表网站已经接收或审核本模组。

## 文件与清单

官方 Wiki 将 `.polymod` 定义为适于分发的 ZIP 容器；清单应在根目录，DLL 与清单同级。当前加载器源码也接受文件夹及 `.zip`，本项目采用 `.polymod` 分发。[官方 Mod 文档](https://github.com/PolyModdingTeam/PolyMod/wiki/1.-Mod)、[当前 Loader 源码](https://github.com/PolyModdingTeam/PolyMod/blob/main/src/Loader.cs)。

```text
agent_bridge-0.4.0.polymod
  manifest.json
  PolytopiaAgentBridge.PolyMod.dll
  LICENSE
```

不能再包一层项目文件夹。`id=agent_bridge` 符合原生小写字母/下划线限制；`version=0.4.0` 使用当前 `System.Version` 支持的数字格式，不能为迎合 GitHub tag 直接写 `v0.4.0` 或 `0.4.0-beta.1`。保留 name、description、authors、dependencies 和 client。实际模板在 [polymod/manifest.json](polymod/manifest.json)；游戏依赖固定到已核对版本。[当前 Manifest 定义](https://github.com/PolyModdingTeam/PolyMod/blob/main/src/Mod.cs)。

`client=true` 表示本包不改写游戏规则目录；它不授予联网操作权限，也不证明联网对战兼容。本项目按本地原版规则维护，不打包临时扩展测试模组。

## PolyScript 与版号

Wiki 的静态 Load 示例较旧；当前 API 使用 `PolyScriptBase` 的 Load/Unload 生命周期。本入口继承 `PolyMod.Api.PolyScript`，使用 net6.0、BepInEx/HarmonyX 与本地 interop 引用。以当前版本源码和实机环境为准，不把旧 Wiki 示例当成当前唯一签名。[官方 PolyScripts 文档](https://github.com/PolyModdingTeam/PolyMod/wiki/4.-PolyScripts)、[当前 PolyScript API](https://github.com/PolyModdingTeam/PolyMod/blob/main/src/Api/PolyScript.cs)。

版本规则：`VERSION`、C# BridgeRelease、PolyMod 清单、MelonInfo、ready 和公告统一为 `0.4.0`；GitHub tag 为 `v0.4.0`，其中 v 是 tag 前缀。程序集数字版本为 `0.4.0.0`，InformationalVersion 为 `0.4.0`。预发布状态使用 GitHub 的 prerelease 标志，避免清单字符串无法解析。

## 本项目提交材料

- `agent_bridge-0.4.0.polymod` 及 SHA256；结构只含清单、自有 Bridge DLL 和 LICENSE。
- 源码/SDK ZIP、源码清单、MIT 许可及双语安装、范围、更新公告。
- GitHub 仓库/版本链接、作者列表和中英文简介可直接采用本包文档。
- 保留本地完整回归、构建来源与候选实测记录；私人报告不作为公开包内容。

通过 [polymod.dev](https://polymod.dev/) 的实际账号和提交页面完成发布。公开首页/Wiki 能确认包格式，但本次未登录提交界面，未确认账号权限、图片、大小限制或审核要求，不能把这些猜成已满足。发布时按当前页面补齐，并以网站显示的接收状态为准。此处未提交、未上传，也未声称已 Verified。

不能分发游戏二进制、生成 interop、加载器依赖、原生反编译转储、个人存档、运行队列、密钥或认证信息。打包工具仅显式选取自有组件；原版特殊种族的既有样例及新修复的回归边界见[兼容说明](COMPATIBILITY.md)。
