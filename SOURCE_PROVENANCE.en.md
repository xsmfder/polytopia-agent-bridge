# Source selection and dependency provenance

[中文](SOURCE_PROVENANCE.md)

`tools/prepare_release.py` exports an explicit selection. `release-manifest.json` records relative paths, sizes and SHA256. Passing a scan does not imply publication or automatically settle all external rights.

| Content | Source and boundary |
| --- | --- |
| src/*.cs | Owned interface, projection, native-command adaptation and receipt code; no game method implementations or native decompilation dumps |
| Python SDK/CLI/transport/observation/memory | Project code; client.py is a pure queue-import compatibility entry |
| tests and minimal fixtures | Project regression and reduced player-visible interface samples, excluding full campaign logs/saves |
| tools | Local builds, validation, deployment/rollback, startup, source selection and packaging/tests; the PolyMod build script references local dependencies only |
| VERSION and polymod/manifest.json | Unified version and runtime-package metadata |
| Bilingual Markdown, CI and ignore rules | Project documentation/workflows; official GitHub Actions resolve upstream |

The source ZIP excludes game/loader DLLs, generated interop, runtime logs, private reports, saves, configuration and credentials. The `.polymod` contains only its manifest, owned net6 Bridge DLL and LICENSE. The MelonLoader fallback distributes only the owned Bridge DLL. Compile references are not redistributed dependencies.

Local references come from the user's legitimate game, [PolyMod](https://github.com/PolyModdingTeam/PolyMod), [BepInEx](https://github.com/BepInEx/BepInEx), [MelonLoader](https://github.com/LavaGang/MelonLoader) and their Harmony/runtime environments. External components and game content retain their own rights/licenses, outside the project's MIT grant.

Project code uses the [MIT license](LICENSE), retaining xsmfder and collective contributor attribution without private names/emails. Verify the provenance of additions; `.gitignore` cannot replace explicit selection and archive auditing.
