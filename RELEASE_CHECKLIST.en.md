# Release preparation and acceptance

[English](RELEASE_CHECKLIST.en.md) | [简体中文](RELEASE_CHECKLIST.md)

This checklist separates local source preparation, clean installation, upgrade acceptance, and actual publication. A completed feature checklist is not a completed release.

## Local preparation

- [x] README describes project capabilities, scope, and usage; private repair history is excluded.
- [x] Chinese and English versions of all eight public Markdown documents are included, with language links and matching interface boundaries. LICENSE is the original English MIT text.
- [x] INSTALL covers user-installed official MelonLoader, build references, Steam offline mode, initial network dependencies, and rollback.
- [x] Explicit source export excludes game/loader binaries, generated references, personal runtime data, and private history links.
- [x] The owner selected MIT. LICENSE and source/fixture/dependency provenance boundaries are included.
- [x] The original source selection passed independent-directory first installation, core solo actions, manual save restoration, bridge rollback, and reinstall on this machine. User-level saves were shared, and reinstall checked startup readiness rather than another match.
- [x] Lifecycle interfaces passed full local regression and visible solo load/save/menu/quit/restart checks. Hotseat saves remained byte-identical.
- [x] The independent DSH lifecycle report covers manual source replacement and **upgrade deployment**, not a clean installation. Its successful load/save/menu/quit results remain valid within that scope.
- [x] Durable delayed-quit recovery passed 90 C# suites/3303 assertions, 459 Python tests, 5 recovery tests, and 114 tooling tests. In the visible solo run, the first receipt was returning_to_menu/quitScheduled=false; querying only after process exit returned confirmed/saved=true/processExited=true, with one quit submission. Bilingual links and Python example syntax were checked.
- [x] Installation applicability was reviewed: loader, compile references, deployment layout, and install/rollback procedures are unchanged; the new receipt directory uses the already-required writable workspace. Acceptance uses upgrade and functional regression evidence, not a newly claimed clean installation.
- [ ] New formal hotseat management entry points need live acceptance; earlier low-level hotseat restoration evidence is not automatically a full acceptance of these entry points.

## External publication steps

- [x] Repository [xsmfder/polytopia-agent-bridge](https://github.com/xsmfder/polytopia-agent-bridge), issue/contribution channels, and final source selection are confirmed; the initial repository's MIT attribution is retained.
- [ ] Establish the formal release version and tag.
- [x] Actual [GitHub CI for cfe55d7](https://github.com/xsmfder/polytopia-agent-bridge/actions/runs/39733774017) passed SDK, frozen recovery, tooling, and source inventory checks on Windows with Python 3.10, 3.11, and 3.13. The first run exposed inconsistent Windows short/long path comparisons; verification output boundaries were corrected, and the failed run remains available.
- [ ] Configure applicable repository permissions and secret/push protection.
- [x] Public source is pushed to main, preserving the initial MIT commit. Both README languages credit the maintainer, Codex (OpenAI), and DSH agent. The named local source archive includes its exact SHA256 and scope manifest.
- [ ] Select a formal release version and create its tag/Release. A source push does not constitute a formal release.

## Meaning of the evidence

Earlier gameplay evidence includes solo, fixed-seat hotseat, notifications, save restoration, public markers, settled actions, supported ruin rewards, and Cloak hint changes with peace. Ruins creating cities, Cymanti, and ice breaking remain outside the first release's accepted live branches.

The DSH lifecycle follow-up used an existing installation after files were replaced. It found that a delayed query could miss final quit progress after an active-match exit; that is tracked separately from the successful `--wait` cases. A fix must not simply claim that every disappeared process means successful saving.

Fully disconnected first installation, other machines, online operation, complete loader uninstallation, Steam library-button launch, and the new formal hotseat management paths are not established by that report. Preserve exact scope and candidate hashes when describing results.

Project licensing is separate from game and dependency rights; see [GitHub's licensing guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository). Source is uploaded to the public repository above; no formal tag or Release has been created. Keep cloud CI and local live-game evidence separate, and update these checks only with actual results.
