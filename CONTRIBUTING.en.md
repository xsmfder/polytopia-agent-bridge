# Development and validation

[English](CONTRIBUTING.en.md) | [简体中文](CONTRIBUTING.md)

Numeric versions are recorded in VERSION and release-manifest.json. Maintainers align both changelogs and loader metadata, verify source hashes and pass CI. Use a numeric tag such as v0.4.0. The manual source workflow creates a downloadable draft only, without automatic publication. Attach verified same-version local runtime packages and follow the release checklist before publishing.

Address concrete issues in player-visible information, native commands, receipts, or maintenance. Preserve the distinction between unknown values and zero, false, or empty lists. Do not fill observations with hidden game data or replace actual effects with previews. Discover candidates from native interfaces rather than maintaining an exhaustive hardcoded ability table.

Run focused validation for affected modules, then batch related changes for regression. Record the failing behavior, the result after the change, and the actual deployment scope. Distinguish mock tests, historical replay, and live evidence; a missing sample is not a pass. Batch small fixes and ordinary live acceptance checks.

Report problems through [Issues](https://github.com/xsmfder/polytopia-agent-bridge/issues) and propose changes through [Pull requests](https://github.com/xsmfder/polytopia-agent-bridge/pulls). Reports may include field structures, error codes, sanitized minimal receipts, game/bridge versions, and reproduction steps. Do not attach full saves, authentication records, private queues, or game binaries.

Project code uses the [MIT license](LICENSE). Confirm that you have the right to contribute code under that license. Preserve third-party attribution and licenses when introducing external code. Never commit local game or loader dependencies. Build and validation instructions are in [INSTALL.en.md](INSTALL.en.md).

Maintenance follows vanilla execution, without an individual acceptance task for every third-party mod. Current version: 0.4.0. Version changes must align VERSION, C#, the PolyMod manifest and bilingual documentation, with version checks. Distinguish discovery of new content from its effect-confirmation loop.
