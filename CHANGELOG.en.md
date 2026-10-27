# 0.4.0 announcement (GitHub release)

[中文](RELEASE_NOTES.md)

This version brings native PolyMod integration, major special-tribe mechanics and interface fixes from natural gameplay into one release. The GitHub tag is `v0.4.0`; PolyMod, MelonLoader and Bridge ready metadata display `0.4.0`.

A `.polymod` runtime archive and auditable build/package tools accompany the independent MelonLoader fallback. Native catalogs supply dynamic technologies, units and buildings. Maintenance follows vanilla rule execution, with no individual acceptance program for every third-party mod or universal guarantee for new effects.

Encounter acknowledgements tolerate a known auxiliary-text sampling change while still rejecting changed titles, bodies, options and differing nonempty reward values. Known empty pending queues have consistent semantics. A selected city's reward display can close while other cities retain pending choices. Public SDK resolution advances displays and original receipts, stopping at real choices.

Exida preview separates primary damage from neighboring poison. Island movement adds algae-spawn queue predictions across the path, including its origin. Unexplored terrain stays unknown; previews execute no builds and do not simulate the complete future world.

Verification normalizes Windows long paths and 8.3 aliases before enforcing output-directory protections for src/tests/tools.

Downloads: `agent_bridge-0.4.0.polymod`, source/SDK ZIP, same-source MelonLoader fallback DLL and SHA256 files. Follow the [installation guide](INSTALL.en.md), preserving original saves and unresolved sessions when switching loaders.

Vanilla special-tribe coverage includes historical live samples. New fixes in this final candidate have separate regression and packaging evidence, without claiming a new exhaustive live campaign. Online multiplayer and arbitrary third-party special rules remain outside the support promise. This version is released on GitHub. Submission to the PolyMod website has not been performed.

---

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
