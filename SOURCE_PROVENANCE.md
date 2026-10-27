# 源码选集与依赖来源

[English](SOURCE_PROVENANCE.en.md)

源码由 `tools/prepare_release.py` 按显式清单导出；`release-manifest.json` 记录相对路径、大小和 SHA256。检索通过不表示已经发布，也不自动完成所有外部权利核对。

| 内容 | 来源与边界 |
| --- | --- |
| src/*.cs | 本项目接口、投影、原生命令适配和回执实现；不含游戏方法实现或原生反编译转储 |
| Python SDK/CLI/传输/观察/记忆 | 项目自有代码；client.py 为纯队列导出兼容入口 |
| tests 与最小 fixtures | 项目回归及裁剪的可见公开接口样本，不含完整对局日志或存档 |
| tools | 本地构建、验证、部署回退、启动、源码选择、打包及测试；PolyMod 构建脚本仅引用本地依赖 |
| VERSION、polymod/manifest.json | 本项目统一版号与运行包元数据 |
| 双语 Markdown、CI、忽略规则 | 本项目说明和流程；官方 GitHub Actions 按上游引用 |

源码 ZIP 不含游戏/加载器 DLL、生成 interop、运行日志、私人报告、存档、配置或密钥。`.polymod` 只含 manifest、自有 net6 Bridge DLL 和 LICENSE；MelonLoader 回退只分发自有 Bridge DLL。源码构建引用不构成依赖再分发。

本机依赖来自用户合法游戏、[PolyMod](https://github.com/PolyModdingTeam/PolyMod)、[BepInEx](https://github.com/BepInEx/BepInEx)、[MelonLoader](https://github.com/LavaGang/MelonLoader)和各自提供的 Harmony/运行时环境。游戏和外部组件保留自身权利与许可，不属于本项目 MIT 授权。

项目采用 [MIT 许可证](LICENSE)，保留 xsmfder 与集体贡献者署名，不公开私人姓名或邮箱。新增文件须确认来源；`.gitignore` 不能代替明确选集和打包审计。
