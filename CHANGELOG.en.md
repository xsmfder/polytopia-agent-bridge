# Changelog

[English](CHANGELOG.en.md) | [简体中文](CHANGELOG.md)

## 0.1.0-beta.1 — 2026-10-22

First public beta, tagged `v0.1.0-beta.1`. This is a source prerelease with an evolving API, without game content, the loader, or a prebuilt mod. See [INSTALL.en.md](INSTALL.en.md) for installation and local builds.

### Capabilities

- Local single-player and fixed-seat Pass and Play observation, dynamic candidates, and a JSON SDK/CLI.
- Original-operation execution, actual-effect confirmation, city-upgrade/diplomacy interruptions, and parent/child receipt recovery.
- Technology-gated resources, public map markers, last-visible enemy memory, and friendly invisible-threat hint events.
- Combat and movement previews, building and economic information, native diplomacy/tasks/rankings, and frozen final feedback.
- Local native save loading, saving, save-and-return, normal quit requests, and recoverable lifecycle receipts.
- Local build verification, first installation/upgrade/rollback, visible startup, and explicit source export.

### Validation and known limitations

- Recorded local validation includes 90 C# suites/3303 assertions, 459 SDK tests, 5 frozen recovery tests, and tooling regression. Actual GitHub CI passed on Windows with Python 3.10, 3.11, and 3.13. Cloud CI neither runs the game nor builds the native mod.
- Fixes include durable recovery of delayed quit progress and consistent comparison of Windows short/long paths.
- Earlier clean-install evidence is kept separate from later upgrade/lifecycle acceptance. This beta has not been installed again from zero; earlier candidate acceptance is not claimed as a complete clean install of this candidate.
- New formal hotseat management paths still lack complete live acceptance. Cymanti, ice breaking, ruins creating cities, other machines, and fully disconnected first installation are outside accepted live coverage.
- The bridge provides observation and execution, without a built-in combat policy or trained model.

### Boundaries

- Windows x64 and local offline matches; users install the loader themselves.
- No redistribution of game assets, the loader, generated references, or personal match data.
- Game versions, fixed seats, unknown/partial semantics, and unaccepted branches are described in [COMPATIBILITY.en.md](COMPATIBILITY.en.md).
- Project code is MIT licensed. Candidate installation coverage, actual GitHub CI, and publication status are tracked in [RELEASE_CHECKLIST.en.md](RELEASE_CHECKLIST.en.md).
