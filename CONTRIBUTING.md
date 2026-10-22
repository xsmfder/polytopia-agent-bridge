# 开发与验证

[English](CONTRIBUTING.en.md) | [简体中文](CONTRIBUTING.md)

Beta发行版本写在release-manifest.json。维护者更新双语变更记录及精确文件清单，通过CI后推送对应`v*-beta.*` tag。发布工作流先运行三个Python版本的离线测试，再生成固定文件顺序和时间戳的同名源码ZIP及SHA256，上传草稿后下载逐字节核对，成功才转为公开预发布。不需要个人令牌或游戏依赖。

修复具体的玩家信息、原生命令、回执或维护问题。保持unknown与0/false/空列表不同；不要用隐藏数据补齐玩家视野，或用预览替代实际效果。新候选从游戏接口读取，不维护一个硬编码技能全集。

先运行受影响模块的定向验证，整批完成后统一回归。报告修改前失败、修改后结果和实际安装范围。模拟测试、历史回放、真实现场样本分别标注；没有样本不写通过。小修成批，普通现场验收合并执行。

通过仓库的[Issues](https://github.com/xsmfder/polytopia-agent-bridge/issues)反馈问题，通过[Pull requests](https://github.com/xsmfder/polytopia-agent-bridge/pulls)提交修改。报告可包含字段结构、错误码、经过脱敏的最小回执、游戏/桥接版本及复现步骤；不要附完整存档、认证记录、私人队列或游戏二进制。

项目代码采用[MIT许可证](LICENSE)。提交贡献前应确认自己有权按该许可提供代码；需要第三方代码时保留其来源和许可，不将游戏或加载器的本机依赖放入仓库。安装与验证入口见[INSTALL.md](INSTALL.md)。
