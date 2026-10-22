# Compatibility and interface conventions

[English](COMPATIBILITY.en.md) | [简体中文](COMPATIBILITY.md)

## Runtime scope

| Item | Scope and limitations |
| --- | --- |
| System | Windows x64; local solo and fixed P1/P2 hotseat; other systems unverified |
| Game | Checked Steam build 2.17.3.16375, Unity 6000.3.17f1; no promise for other builds |
| Loader | User-installed MelonLoader 0.7.3 Open-Beta; no dependencies redistributed |
| Python | Standard library, minimum 3.10; local 3.11.4; CI configured for 3.10/3.11/3.13; cloud execution needs actual run evidence |
| Steam | Official offline mode and local matches; online-login launch and online play unsupported |
| Installation | Initial dependencies may need network access; earlier clean-install evidence and later in-place upgrade evidence have distinct scopes |
| Native adapters | Display rules use checked build fingerprints; unknown builds do not receive guessed HP scales or missing values |

Earlier local evidence covers observations, dynamic candidates, actual action receipts, ordinary solo endings, fixed-seat hotseat handoff/notifications/save restoration, Cloak hints for approach/departure/peace changes, native third-party diplomacy broadcasts, ruin technology/stars/unit/Explorer/population rewards, and upgrade interruptions. This does not establish every tribe, ability, or condition. Cymanti, ice breaking, full special-tribe flows, and ruins creating cities remain outside accepted coverage. Native tests require the user's own game; no decompiled output or generated game assemblies are distributed.

MelonInfo's `0.3.0` is an internal marker until a public version/tag is established. Equal version strings do not mean identical DLLs; use SHA256 and verification inputs.

## Change policy and recovery

Keep draft status before actual publication. Optional additive fields may remain within a schema revision. Renamed/removed fields, changed units/status meanings, or new required fields require the relevant schema update and migration instructions. This is the publication convention; automatic major-version session migration is not implemented.

Actions and candidates bind to episode/player/stream/stateVersion. Restart, reload, or changing matches must not reinterpret a new ticket as an old one. Reread stale candidates; query original unknown/timed-out operations instead of resubmitting. Unsupported signatures/builds return unavailable or partial, never success inferred from absent fields.

First-install rollback restores absence of the bridge DLL; upgrade rollback restores the old DLL. Neither changes saves, controlled-seat configuration, or other mods. Deployment requires a stopped game, the expected root layout, and a target inside that game's `Mods` directory.

The SDK provides player information, actions, and explicit local lifecycle administration. Raw diagnostics and `audit` are not a permission sandbox. Public CI runs SDK, frozen receipt, and temporary-directory tooling tests without downloading, compiling against, or uploading game assets. Native regression remains local to a game owner.

## Startup readiness

In `bridge-startup-1`, PID, started, transport, queues, and seat fields identify the queue owner. `startupUiReady=true` and `startupUiPhase=start_menu`, with `readinessContract=native_start_menu`, establish that this startup reached the native main menu. Older ready files without these fields are insufficient.

This startup proof remains after launch; it does not establish that the game is still in a menu or ready for action. Use observe/handoff for current state. Readiness updates do not change the process's `started` marker. Timeouts do not automatically restart, close windows, select saves, or issue game commands.

## Public interface and schemas

CLI names match public `PolytopiaSDK` methods except that CLI `resume` calls `resume_pending`, and SDK-only `handoff_start` is used through CLI `watch_handoff`. `close` and Python context exit release the local session, not the game. Source signatures and `agentcli.py --help` are authoritative for parameters/defaults.

| Methods | Return data and purpose | Boundaries |
| --- | --- | --- |
| `setup_status`, `setup_action`, `wait_setup` | Native solo menus, current options, original setup receipt | Separate administration, not tactical candidates; creates only explicitly chosen solo games without replacing an active match |
| `saved_games`, `load_game`, `save_game`, `return_to_menu`, `quit_game`, `wait_lifecycle` | `game-lifecycle-1`, operationId, native hotseat list | Fresh session for load; verified save-file write before leaving; no other-seat actions or forced process termination |
| `observe` | Outer `binding/world/popup/enemyMemory`; details adds cities, relations, leaderboard, notifications | Player observation; human/curtain boundaries yield `world=null`; mixed-version details are discarded together |
| `game_info`, `unit_properties` | `native`: `game-info-1`, `unit-properties-1` | Native settings, disclosed players, friendly/currently visible enemy panels; no enumeration of hidden opponents |
| `score_snapshot`, `result` | `native`: `score-snapshot-1`, `result-1`, frozen `native-final-1` | Live statistics separate from frozen endings; explicitly archived episodes can be read in menus with seat validation |
| `threat_map`, `attack_preview`, `composite_preview` | `visible-threat-1`, `attack-preview-1`, `move-composite-preview-1` | Predictions are not damage results; no hidden enemy pathfinding; unsupported effects stay partial/unknown |
| `tribe_info`, `leaderboard` | `tribe-info-1`, `leaderboard-1`; open/read/close | Default read is passive; open/close are native UI writes; query original opening/closing requests rather than navigating again |
| `list_actions` | `binding/actions/sources/discovery`; each row has actionId/scope/command/args/eligible/native | Dynamic native candidates; do not submit false/null eligibility; descriptors are current-version proposals |
| `execute`, `wait_result`, `resume_pending` | native/ticket/binding/retryAllowed and SDK status | Revalidate and checkpoint intent before writes; query the original ticket; never retry unknown/timeout by resubmission |
| `acknowledge_settled_unknown` | settled_effect_unknown; ok=false/canContinue=true/retryAllowed=false | Explicitly accepts an unknown effect only after original settlement and idle/scope proof; does not turn it into success |
| `events`, `notifications` | `visible-events-1`, `public-notifications-1`; separate streams/cursors | Preserve historyGap/hasMore; gaps require observation synchronization, not action replay |
| `choose_popup`, `dismiss_resolved_reward` | SDK status and native callback/display-close evidence | Index is a native button ID; callback submission/display closure does not prove reward or parent completion |
| `handoff_status`, `handoff_start`, `watch_handoff` | Native `handoff-1`, handoff state, target map | Fixed seat only; respect human turns and wait for native map readiness after the start-turn callback |

`polytopia-sdk-1` is the Python envelope; `polytopia-sdk-session-1` is a local checkpoint, not a network response. Native world uses `decision-world-3`; `bridge-schema-3` describes the raw interface. Domains have separate schemas. `action-settlement-1` describes an original ticket, while `visible-combat-state-1` describes the current observation pipeline. Do not interchange them. Most native fields are under `native`, but observation, discovery, and setup have their own outer structures; do not universally unpack `native.world`.

Internal CombatFrame/CombatTicket objects, native wrappers, CLR fields, and reflection caches are not public models. Consume explicit JSON projections. Raw call/get/set and audit remain separate diagnostics, not formal SDK methods or security isolation.

## Fields, statuses, and errors

| Group | Contract |
| --- | --- |
| episodeId/playerId/streamId/stateVersion | Descriptor/receipt identity; a new stream after reload cannot replace the old one. observationRevision is an observation counter, not completion evidence |
| Player/unit selectors | Player IDs are 1–254; an index is separate; 0/255 are not controlled seats. Native unit IDs are uint; unreadable is not zero. sightingId only associates local memory |
| Coordinates | Integer world `(x,y)`, not screen pixels; construction/lookups also validate width/height. Do not wrap array indices into out-of-bounds coordinates |
| HP/combat values | Raw health/damage retain native HP units. Public UI stats and converted display HP are separate; unknown builds do not receive guessed display values |
| Score/rating | Perfection uses points; Domination uses percent-points (100 means 100%), with multipliers/timeGoal separate, not zero-to-one probabilities |
| Empty/unknown/partial | null/unknown/unavailable differ from zero, false, or a complete empty list. Partial rows may remain; completeness decides whether absent rows mean absence. Incomplete popup totals are null; observedButtonCount counts only readable rows |
| Action receipts | accepted/submitted mean acceptance/dispatch. settlementComplete is pipeline completion; effectConfirmation is requested-effect evidence. actionCompleted requires explicit effect proof; audit snapshotComparison does not decide completion |

SDK observed/discovered/archived are read/discovery states, not action success. confirmed establishes the requested result within that operation's scope. needs_resolution requires an explicit choice; handoff/human_turn indicate a boundary; state_changed requires a full reread. rejected, unknown, not_confirmed, timeout, and unavailable have different recovery paths. Preserve original tickets, bindings, and pending writes.

`errorCode` is for machine branching, error/reason explains it, and native retains original results. New native codes may pass through; unfamiliar codes are not success or permission to retry.

| Family | Fixed SDK codes | Response |
| --- | --- | --- |
| Arguments/descriptors | INVALID_ARGUMENT, INVALID_ACTION, INVALID_BINDING, INVALID_RESPONSE, BINDING_REQUIRED | Correct input/reread; input errors do not erase earlier pending writes |
| Seat/version | IDENTITY_UNVERIFIED, EPISODE_UNKNOWN, EPISODE_CHANGED, SEAT_CHANGED, SEAT_MISMATCH, STREAM_CHANGED, VERSION_UNKNOWN, STATE_CHANGED, STALE_ACTION | Reject mixed scopes; a fresh session does not prove an old request succeeded |
| Discovery/reads/previews | ACTION_NOT_AVAILABLE, NOT_CONTROLLED_TURN, READ_NOT_READY, READ_SCOPE_CHANGED, READ_SCOPE_UNKNOWN, READ_TARGET_CHANGED, PREVIEW_NOT_READY, PREVIEW_SCOPE_CHANGED | Reread the domain/wait for the pipeline; do not repeat potentially effective writes |
| Pending/tickets | UNRESOLVED_ACTION, RECOVERY_REQUIRED, ACTION_NOT_SETTLED, RECEIPT_MISMATCH, EXPLICIT_ACK_REQUIRED | Query originals or retain uncertainty; do not forcibly unlock or replace a ticket |
| Session files | SESSION_CLOSED, SESSION_INVALID, SESSION_IO_ERROR, SESSION_REQUIRED, SESSION_SCOPE_MISMATCH, ACTIVE_SDK_PRESERVED | Resolve local locks/checkpoints/scope without deleting unknown intents |
| Setup | INVALID_SETUP_CONTEXT, INVALID_SETUP_RECEIPT, SETUP_ACTION_UNAVAILABLE, SETUP_IDENTITY_UNVERIFIED, SETUP_PENDING, SETUP_RECEIPT_MISMATCH, SINGLEPLAYER_SEAT_ONE_REQUIRED | Reread menu/query setup; do not repeat continue |
| Popups/handoff/displays | POPUP_CHANGED, REWARD_DISPLAY_NOT_CURRENT, HANDOFF_NOT_READY, PUBLIC_DISPLAY_SCOPE_CHANGED, PUBLIC_DISPLAY_SCOPE_UNKNOWN, NOTIFICATION_SCOPE_CHANGED | Observe the current presentation; never substitute a new popup choice |
| Lifecycle | FRESH_SESSION_REQUIRED, LIFECYCLE_RECEIPT_REQUIRED, LIFECYCLE_SCOPE_MISMATCH | Use the proper session/original operation; do not replay writes. Native GAME_PIPELINE_NOT_IDLE and QUIT_RECEIPT_NOT_DURABLE are not success |

CLI input errors also use INVALID_INPUT; transport exceptions use TRANSPORT_UNAVAILABLE. Preserve the actual transport/native response. For example, STALE_ACTION_CONTEXT requires new candidates; SETTLEMENT_TICKET_UNKNOWN does not prove nonexecution; INVOKE_FAILED/REQUEST_FAILED may follow execution. Only consistent non-dispatch evidence such as accepted=false/dispatched=false/outcome=not_dispatched permits treating a request as unexecuted. An error code or ok=false alone is insufficient.

Normal CLI output is one JSON object. Exit codes: **0** successful read/confirmation; **2** argparse usage; **3** rejected; **4** timeout; **5** unknown/not_confirmed/unavailable or ok=false; **6** accepted but pending, opening/closing, needs_resolution, handoff/human_turn, history_gap, or state_changed. Code 3 does not prove that an original action never executed; inspect session and dispatch evidence. Code 0 for a read is not action completion.

This mapping is not an ability whitelist or exhaustive future native field/error table. Versioned schemas, local full verification, DLL/SDK hashes, and deployment records establish identity; the internal 0.3.0 marker cannot replace them.

## Decision projections

`ownedTechs` is the native researched set. An empty array is definitive only with ownedTechsStatus=ok; unreadable sets remain null/unavailable. Deprecated availableTechs does not overwrite formal unknown values.

Normalized tiles retain visualStyle's native tribe/skin and status, land, explored/currentlyVisible, and last-observed metadata. In this build, inSight aliases explored; independent transient tile visibility is null when unestablished. Style is not territorial ownership, and explicitly unexplored tiles do not enter terrain projections.

currentState/matchEnded express phase, not winners or final components. Use SDK.result() for frozen results and detailed observation/domain interfaces for cities, tasks, and construction. A bare world lacking an embedded domain does not imply that the domain is unimplemented.

## Cities and interruptions

cityState.hasCityWall may be null when reward information is incomplete without positive evidence. A readable wall is true; complete absence is false. Unreadable populationNeededToUpgrade is null with reasonCodes, while a real zero stays zero.

Optional cityUpgradeSemantics in city-options-6 explains that raw canCityBeUpgraded is cached construction-population potential. It may change after reload and is not current upgrade eligibility. Read the deficit from populationNeededToUpgrade; use pendingCityReward and real candidates for rewards.

For a parent ticket needing a choice, explicitly submit the current city reward or peace_response. Child writes are checkpointed and native candidates revalidated while preserving the parent. Query the parent after the response. Unrelated new actions, unknown writes, and pending children remain blocked; this does not allow duplicate EndTurn or automatic peace acceptance.

## History and readability

The enemy-note soft limit of 512 applies to old notes, not currently visible units. Later pruning discloses truncation counts. Native ticket history is 128; public event history is 2048. Expired tickets are unknown; event gaps use historyGap. Expiration does not justify retry. Disk audit logs are archived separately and are not automatically bounded.

Notifications read actual displayed DiplomacyNotification text, not stale parser caches or internal participant objects. captureComplete=false or unreadableNoticeCount>0 means a notification was shown but visible text was unreadable; events=[] is not proof of no notification. This differs from capacity historyGap and does not cover messages before observer startup. Missing fields in old frames do not establish new-version completeness.

visible-events-1 own_invisible_threat_changed identifies the friendly detector through unitId/coords. before/after contain present/status/authority. before=null means no earlier stream record; present=null means an unknown read, neither means false. actingPlayerId/actorId/nativeAction/nativeTargetId/nativeTargetCoord do not expose the hidden source. Unchanged values are deduplicated; removing a unit does not emit a reassuring false; history does not replace current world hints.

## Durable quit progress

Lifecycle history is separate from tactical ticket history. Before native Quit, final progress is atomically written under lifecycle-receipts using operationId, PID, bridgeStarted, and controlledPlayerId. A later session query can read it after process termination. A matching receipt plus proof that the original process exited confirms the exit effect; the receipt alone and mere queue failure do not. exitCause remains not_verified. If durable writing fails, QUIT_RECEIPT_NOT_DURABLE stops automatic Quit. These local records are not public source or save data.

## Diagnostics

Reply logging is not delivery or completion evidence. ticket means the newly allocated ticket for that request; requestedTicket is only query intent; context=last_frame is historical context. Ordinary errors expose public phase/category, not hidden objects from native exception text.

readPreparation describes preparation of bridge-managed code without invoking game queries. complete does not prove game/map/action success and does not determine startupUiReady. Local OnUpdate timings are not GPU timings or rendered FPS.

Caches and native wrappers are not public models. Keep unknown/partial when fields are unreadable rather than combining fallback paths into apparently complete responses. Recheck changed builds. Warm-state performance and old candidates cannot establish fresh installation, cross-machine behavior, or cloud CI success.
