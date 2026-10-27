# Compatibility and evidence boundaries

[中文](COMPATIBILITY.md)

Version **0.4.0**; checked game 2.17.3.16375 / Unity 6000.3.17f1, PolyMod 1.2.18 / BepInEx 6.0.0-be.785, and separate MelonLoader 0.7.3. Do not infer compatibility after upgrades from matching type names alone. SDK: Python 3.10+ standard library.

Maintained scope: local native rules, player-visible observation and native execution. Steam online startup and Overlay have prior manual samples; they do not establish online multiplayer gameplay support. Use offline/local single-player and owned hotseat seats for the supported agent path.

## Coverage

| Scope | Existing evidence | Limits |
| --- | --- | --- |
| Aquarion | Major Flood/Waterways mechanics, ordinary-tribe Drain and hotseat handoff samples | Not every unit, combination or cross-tribe case |
| Elyrion | Major Enchant, Sanctuary, dragon growth and tribe-visible ruin-hint samples | Hints are not unrestricted fog information; combinations are not exhaustive |
| Polaris | Freeze, break-ice and major ice-mechanic samples | Current native state/menus govern; combinations are not exhaustive |
| Cymanti | Swarm/Swift, double attack, explosion damage/poison, centipede eating chains, infection/growth, stomp/algae and building/decompose samples; a natural-game victory | Private foreign effects remain withheld; final new-preview/driver candidates must be distinguished from historical live samples |
| PolyMod catalogs | Controlled dynamic tech/unit/building discovery, execution, confirmation and save/reload | No per-mod acceptance program or universal new-effect guarantee |

Earlier samples establish mechanism coverage at their recorded binaries. New 0.4.0 popup/queue/preview fixes have separate frozen-fixture and full-regression evidence; preparation alone does not turn those into a new full live campaign or exhaustive special-tribe acceptance.

## Contracts

| Field/status | Meaning |
| --- | --- |
| episodeId/playerId/streamId/stateVersion | Candidate and receipt scope; process reload does not reinterpret old tickets |
| accepted/submitted | Request accepted or UI callback invoked, not effect confirmation |
| confirmed | Original settled action has target-associated effect evidence |
| needs_resolution | Actual pending display/choice requires handling |
| unknown/null/partial | Missing evidence or incomplete coverage, distinct from zero/false/empty |
| pendingCityReward.status=none | Known readable empty owned reward queue; missing/throwing/unreadable collections stay unavailable |
| popup.rewardDisplay | Current owned city identity, recorded selected reward and city-scoped dismiss proof |
| compositeEffects.effect=PoisonSplash | Conditional poison-only neighboring branch; primary damage is separate |
| terrainEffects | Conditional AlgaeSpawn queue prediction on disclosed native path tiles, including origin |
| result | Frozen natural terminal authority; ordinary actionable-turn reads may be unavailable after match end |

Hidden terrain, invisible enemy locations and foreign private effect arrays are withheld. Enemy memory records last visibility and does not update hidden entities. Preview runs no command and cannot replace post-action receipts. Movement can transform identity/terrain or reveal hidden blockers; these are not fully simulated. New mod content is dynamically listed where native interfaces permit, but arbitrary new effects may remain partial/unknown.

The native 0.4.0 DLL passed actual PolyMod startup, main-menu queries and native graceful exit. This startup check does not replace in-match testing of the new queue/preview paths; their new-path conclusions remain bounded by the corresponding offline regressions.
