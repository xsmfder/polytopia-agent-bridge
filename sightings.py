"""Enemy sighting memory built ONLY from normalized legal observations.

Rules enforced here (codex-dsh-007):
* Input must already be normalized (see observation.py): complete, verified identity, ordered
  revision. Anything else is rejected WITHOUT modifying memory - a failed or partial observation
  is not an "empty" observation and must never be read as "I lost sight of everything".
* Revisions are monotonic: a repeated revision is an idempotent no-op, an older revision is
  rejected and can never roll back time or HP.
* Losing sight freezes the record. No position, HP, upgrade, conversion or path is ever updated
  from behind the fog, and not-seeing is not death.
* Seeing the same owner+type again on the same tile after a gap is NOT confirmation that it is the
  same unit: it creates a NEW sighting with `association="candidate"` pointing at the old one. The
  old record stays frozen. Only a contiguous observation (seen in the immediately previous applied
  revision, same tile, same owner/type) may update a record in place.
* `confirmedDestroyed` is terminal and can never be revived.
* Destruction events must be legally associated with ONE specific sighting; otherwise they are kept
  as unresolved and change nothing.
* No internal unit id is ever used for identity, and none is exposed to the planner.
"""
from __future__ import annotations

import copy

STATUS_VISIBLE = "visible"
STATUS_UNOBSERVED = "unobserved"
STATUS_DESTROYED = "confirmedDestroyed"


class ObservationRejected(Exception):
    """The observation may not be applied; memory was not modified."""


class StaleObservation(ObservationRejected):
    """Older or duplicate revision."""


class Sighting(object):
    def __init__(self, sid, owner, unit_type, hp, position, turn, revision, association="new",
                 candidate_of=None, public_info=None):
        self.sighting_id = sid
        self.owner = owner
        self.unit_type = unit_type
        self.hp = hp
        self.public_info = copy.deepcopy(public_info)
        self.position = position
        self.last_seen_turn = turn
        self.last_seen_revision = revision
        self.first_seen_turn = turn
        self.status = STATUS_VISIBLE
        self.observation_lost = False
        self.association = association          # "new" | "candidate"
        self.candidate_of = candidate_of
        self.last_continuity_evidence = None

    def freeze(self):
        self.status = STATUS_UNOBSERVED
        self.observation_lost = True
        self.last_continuity_evidence = None    # the chain of evidence is now broken

    def to_planner_dict(self):
        """Everything a human player could legitimately have written down. No internal id."""
        return {
            "sightingId": self.sighting_id,
            "owner": self.owner,
            "unitType": self.unit_type,
            "hp": self.hp,
            "publicInfo": copy.deepcopy(self.public_info),
            "lastSeenPosition": list(self.position) if self.position else None,
            "lastSeenTurn": self.last_seen_turn,
            "lastSeenObservationRevision": self.last_seen_revision,
            "firstSeenTurn": self.first_seen_turn,
            "status": self.status,
            "observationLost": self.observation_lost,
            "association": self.association,
            "candidateOf": self.candidate_of,
            "associationEvidence": self.last_continuity_evidence,
            "identityCertainty": "assumed" if self.association != "new" else "unlinked",
        }


class EnemyMemory(object):
    def __init__(self, match_id, controlled_player_id):
        self.match_id = match_id
        self.controlled_player_id = controlled_player_id
        self._next_id = 1
        self.memory = {}
        self.current_visible = []
        self.last_applied_revision = None
        self.last_visible_ids = set()
        self.unresolved_destroyed_events = []
        self.reset_reason = None
        self.rejected = []

    # ------------------------------------------------------------------- helpers
    def _validate(self, obs):
        if not isinstance(obs, dict):
            raise ObservationRejected("observation must be a mapping")
        if obs.get("completeness") != "complete":
            raise ObservationRejected("observation is not complete")
        if obs.get("controlledPlayerId") is None:
            raise ObservationRejected("observation has no controlled player id")
        if not obs.get("matchId"):
            raise ObservationRejected("observation has no match id")
        rev = obs.get("observationRevision")
        if not isinstance(rev, int) or isinstance(rev, bool):
            raise ObservationRejected("observationRevision must be an integer")
        if obs.get("units") is None:
            raise ObservationRejected("observation has no visible units list")

    def _maybe_reset(self, obs):
        if (obs["matchId"] != self.match_id
                or obs["controlledPlayerId"] != self.controlled_player_id):
            self.memory = {}
            self.current_visible = []
            self._next_id = 1
            self.last_applied_revision = None
            self.last_visible_ids = set()
            self.unresolved_destroyed_events = []
            self.match_id = obs["matchId"]
            self.controlled_player_id = obs["controlledPlayerId"]
            self.reset_reason = "identity_or_match_changed"
            return True
        return False

    # ------------------------------------------------------------------- public
    def observe(self, obs):
        """Apply one normalized observation. Raises on stale/duplicate input without modifying."""
        self._validate(obs)
        self._maybe_reset(obs)
        rev = obs["observationRevision"]
        if self.last_applied_revision is not None and rev == self.last_applied_revision:
            return self.current_visible            # idempotent replay
        if self.last_applied_revision is not None and rev < self.last_applied_revision:
            self.rejected.append(("stale_revision", rev))
            raise StaleObservation("revision %d is older than %d"
                                   % (rev, self.last_applied_revision))

        turn = obs.get("turn")
        contiguous = (self.last_applied_revision is not None
                      and rev == self.last_applied_revision + 1)
        refreshed, current = set(), []

        for u in obs.get("units") or []:
            pos = tuple(u["coords"]) if u.get("coords") else None
            matched = None
            if contiguous:
                for s in self.memory.values():
                    if (s.sighting_id in self.last_visible_ids and s.status == STATUS_VISIBLE
                            and s.owner == u.get("owner") and s.unit_type == u.get("unitType")
                            and pos is not None and s.position == pos):
                        matched = s
                        break
            if matched is not None:
                matched.hp = u.get("hp")            # contiguous evidence may refresh a record
                matched.public_info = copy.deepcopy(u.get("publicInfo"))
                matched.last_seen_turn = turn
                matched.last_seen_revision = rev
                matched.position = pos
                matched.association = "assumed_continuation"
                matched.last_continuity_evidence = "assumed_contiguous_samples"
                refreshed.add(matched.sighting_id)
                current.append(matched)
                continue

            candidate_of = None
            for s in self.memory.values():
                if s.owner == u.get("owner") and s.unit_type == u.get("unitType"):
                    candidate_of = s.sighting_id
                    break
            # A re-appearance can never be confirmed as the same unit, and never revives a kill.
            s = Sighting(self._next_id, u.get("owner"), u.get("unitType"), u.get("hp"), pos,
                         turn, rev, "candidate" if candidate_of is not None else "new", candidate_of,
                         public_info=u.get("publicInfo"))
            self._next_id += 1
            self.memory[s.sighting_id] = s
            refreshed.add(s.sighting_id)
            current.append(s)

        for s in self.memory.values():
            if s.sighting_id not in refreshed and s.status == STATUS_VISIBLE:
                s.freeze()

        # Destruction must be legally associated with EXACTLY ONE specific sighting. Matching on
        # owner+type+position is per-record association, not a batch: two candidate records on the
        # same tile make the event ambiguous, so it stays unresolved instead of killing both.
        for d in obs.get("destroyed") or []:
            dpos = tuple(d.get("coords")) if d.get("coords") else None
            hits = [s for s in self.memory.values()
                    if s.owner == d.get("owner") and s.unit_type == d.get("unitType")
                    and s.position == dpos and s.status != STATUS_DESTROYED
                    and (s.sighting_id in refreshed or s.sighting_id in self.last_visible_ids)]
            if len(hits) == 1:
                hits[0].status = STATUS_DESTROYED
                hits[0].observation_lost = False
                hits[0].last_continuity_evidence = "legally_associated_destruction_event"
            else:
                self.unresolved_destroyed_events.append(dict(d))

        self.current_visible = current
        self.last_applied_revision = rev
        self.last_visible_ids = set(refreshed)
        return current

    # ------------------------------------------------------------- planner views
    def current_visible_enemies(self):
        return [s.to_planner_dict() for s in self.current_visible]

    def enemy_memory(self):
        return [s.to_planner_dict() for s in sorted(self.memory.values(), key=lambda x: x.sighting_id)]

    def lost_sightings(self):
        return [s.to_planner_dict() for s in self.memory.values() if s.status == STATUS_UNOBSERVED]
