# 源码选集与外部依赖

[English](SOURCE_PROVENANCE.en.md) | [简体中文](SOURCE_PROVENANCE.md)

## 提交范围

交付目录由`tools/prepare_release.py`显式选集生成，不从游戏目录递归复制。`release-manifest.json`记录相对路径、字节数和SHA256；它不证明已获得全部版权许可，也不表示已发布。

| 提交内容 | 用途与来源边界 |
| --- | --- |
| `src/*.cs` | 本项目Bridge适配、投影、命令和回执实现；行为通过本机原生接口调用，不附游戏方法实现或反编译转储 |
| Python SDK、CLI、传输、观察和记忆模块 | 本项目本地接口与数据处理；兼容client.py只导出纯队列传输 |
| C#/Python测试及最小夹具 | 本项目测试，裁剪的结构化回执及人工数据；不是完整对局档案或游戏二进制 |
| `tools/*.py` | 本项目验证、安装/回退、可见启动、归档和选集工具及测试 |
| `deploy.ps1`、`start_ai_match.py` | 无副作用的退役入口提示，防止旧参数触发历史自动操作；不含原历史脚本 |
| Markdown、CI、忽略规则 | 项目文档和构建流程；官方GitHub Actions通过上游引用 |

游戏类型/成员名称、构建指纹和少量适配定位数据用于互操作与版本识别，不授予游戏内容使用权。项目所有者已选择MIT；正式发布前仍需确认源码/夹具来源和贡献授权。

## 排除范围

- 游戏EXE、GameAssembly、UnityPlayer、资产、存档和生成的互操作程序集。
- MelonLoader、Harmony、IL2CPP运行时及生成依赖的本机副本。
- 反编译输出、原生反汇编转储和分析目录。
- 私人campaign/repairs、完整实机日志、JSONL队列、认证记录、用户路径、截图和桌面操作脚本。
- 本机验证输出、构建引用、安装备份和启动记录。
- lifecycle-receipts中的本机退出进度记录。

`.gitignore`不能代替选集；额外文件加入仓库前也须进行内容和来源审阅。

## 外部组件

| 组件 | 获取方式 | 随本项目分发 |
| --- | --- | --- |
| The Battle of Polytopia | 用户自己的正常游戏安装 | 否 |
| [MelonLoader](https://github.com/LavaGang/MelonLoader) | 用户按官方说明自行安装 | 否 |
| [Harmony](https://github.com/pardeike/Harmony) | 本机MelonLoader环境提供引用 | 否 |
| Python、Windows .NET组件 | 用户安装或操作系统提供 | 否 |
| GitHub Actions官方action | CI从上游解析 | 不复制其源码和依赖 |

本项目代码按项目所有者选择采用[MIT许可证](LICENSE)，保留公开仓库账号xsmfder及项目贡献者集体署名，不使用私人姓名或邮箱。游戏内容和外部组件不在本项目MIT授权范围内。外部组件各自遵循上游许可，不能通过本项目的LICENSE获得其游戏资产或生成程序集的分发权。
