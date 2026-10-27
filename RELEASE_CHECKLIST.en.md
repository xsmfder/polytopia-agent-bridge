# 0.4.0 preparation and publication checklist

[中文](RELEASE_CHECKLIST.md)

## Preparation

- Reacquire authoritative sources and preserve raw natural-game evidence and the deployed reward fix. Distinguish tactical mistakes from interface defects.
- Fix auxiliary-text rejection, pending rewards, public queue resolution and conditional area/terrain previews. Run targeted and full same-source regressions.
- Align `VERSION=0.4.0`, C# metadata, manifest and both loader displays; use GitHub tag `v0.4.0`.
- Select sources through `tools/prepare_release.py`; check sensitive content, personal paths, missing local documentation links and external dependencies.
- Include Chinese/English README, installation, usage, compatibility, provenance, contribution, changelog, announcement and PolyMod publication documents.
- Rerun Python/tool checks in the clean source selection. Match the complete C# source set and hashes across regression and net6 PolyMod builds.
- Prepare `.polymod`, same-source MelonLoader DLL, source/SDK ZIP and SHA256 files; inspect actual extracted bytes, versions and root layout.
- Preserve deployment/rollback evidence. Separate historical live samples, frozen replay, offline fixtures and final-candidate live checks.

This lists requirements; only actual delivery-directory verification records establish completion.

## Publication

Preparation alone creates no public tag, main-branch push, public Release or PolyMod submission. Do not relabel the historical 0.1.0-beta.1 release as a new binary.

Before publishing, inspect the intended commit and `release-manifest.json`. Use `v0.4.0` and attach exact checked artifacts. Use bilingual RELEASE_NOTES; the GitHub prerelease flag is separate from the numeric manifest version. The source workflow creates drafts only. Attach locally verified binaries separately and verify downloaded bytes before publishing.

Submit the `.polymod` package using the [publication notes](POLYMOD_PUBLISH.en.md) and current website form requirements. Report publication state, assisted checks and remaining coverage honestly. Individual acceptance of all third-party mods is not a release gate.
