# Source selection and external dependencies

[English](SOURCE_PROVENANCE.en.md) | [简体中文](SOURCE_PROVENANCE.md)

## Included files

`tools/prepare_release.py` creates an explicit source selection rather than recursively copying the game directory. `release-manifest.json` records relative paths, byte sizes, and SHA256 hashes. It does not certify ownership of every copyright or establish that publication has occurred.

| Group | Purpose and provenance boundary |
| --- | --- |
| `src/*.cs` | Project bridge adapters, projections, commands, and receipts; invokes local native interfaces without including game method implementations or decompiled dumps |
| Python SDK, CLI, transport, observation, and memory modules | Project interface and data processing; the compatibility `client.py` exports only the pure queue transport |
| C#/Python tests and minimal fixtures | Project tests, trimmed structured receipts, and synthetic data; not full match archives or game binaries |
| `tools/*.py` | Project verification, installation/rollback, visible startup, archiving, export tools, and their tests |
| `deploy.ps1`, `start_ai_match.py` | Retired-entry notices without side effects, preventing old arguments from launching historical automation; no original historical scripts |
| Markdown, CI, ignore rules | Project documentation in Chinese and English, plus build configuration; official GitHub Actions are referenced upstream |

Game type/member names, build fingerprints, and limited adapter-location metadata support interoperability and version identification. They do not grant rights to game content. The owner has selected MIT for project code; source/fixture provenance and contribution authority still need to be confirmed before public release.

## Excluded files

- Game executables, GameAssembly, UnityPlayer, assets, saves, and generated interop assemblies.
- Local copies of MelonLoader, Harmony, IL2CPP runtimes, and generated dependencies.
- Decompiled source, native disassembly dumps, and analysis directories.
- Private campaigns/repairs, full live logs, JSONL queues, authentication records, personal paths, screenshots, and desktop-control helpers.
- Local verification outputs, build references, installation backups, startup records, and durable lifecycle receipts.

`.gitignore` is not a replacement for explicit selection. Review the contents and provenance of any additional files before committing them.

## External components

| Component | Source | Redistributed here |
| --- | --- | --- |
| The Battle of Polytopia | User's own legitimate installation | No |
| [MelonLoader](https://github.com/LavaGang/MelonLoader) | User installs it following official instructions | No |
| [Harmony](https://github.com/pardeike/Harmony) | Local MelonLoader environment provides the reference | No |
| Python and Windows .NET components | User installation or operating system | No |
| Official GitHub Actions | CI resolves upstream actions | Their source/dependencies are not copied into this package |

Project code uses the owner's chosen [MIT license](LICENSE), retaining the public repository account xsmfder and the collective contributors attribution without private names or email addresses. Game content and external components are outside this project's MIT grant and retain their respective upstream rights and licenses. The project license does not authorize redistribution of game assets or generated game assemblies.
